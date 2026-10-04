using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace GamerTool.Services;

public sealed class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// What Windows says this endpoint physically is, from
    /// <c>PKEY_AudioEndpoint_FormFactor</c>. -1 when the property could not be read.
    /// <para>
    /// Carried rather than resolved so the decision about what to do with it can
    /// happen in <see cref="OutputRouting"/>, which is pure and testable. Reading the
    /// property needs a device, and a test cannot have one.
    /// </para>
    /// <para>
    /// -1 rather than a default of 10 (UnknownFormFactor) because those two are not
    /// the same claim. A driver that answers "unknown" has told us it does not know,
    /// which is worth nothing; a property that could not be read at all is a failure
    /// we may be able to distinguish and log. Both end up falling back to the name
    /// hints, so the difference is only visible in the log, but conflating them would
    /// hide the difference permanently.
    /// </para>
    /// </summary>
    public int FormFactor { get; set; } = -1;

    /// <summary>
    /// True only when Windows has positively said this is display audio.
    /// <para>
    /// False is not the same as "not a display": <see cref="FormFactor"/> of -1 means
    /// nobody asked. Callers that need to tell those apart must check the number
    /// rather than read this as an answer.
    /// </para>
    /// </summary>
    public bool IsDisplayAudio => FormFactor == OutputRouting.DisplayAudioFormFactor;
}

public sealed class AudioDeviceManager
{
    // This class used to carry its own hand written COM bindings for the
    // multimedia enumerator, roughly two hundred lines of CoClass, interface,
    // PROPVARIANT and property store declarations, used only to list output
    // devices.
    //
    // It was removed because it could not coexist with NAudio, which declares
    // the same CoClass and the same interfaces for the same underlying objects.
    // .NET resolves a managed wrapper type against a CLSID and hands the cached
    // one to every later activation, so whichever binding activated first won
    // and the other could not be cast to what it expected. In practice this file
    // won, because the device manager is a field on the main window, and the
    // spectrum's loopback capture then failed every single time with
    //
    //   Unable to cast object of type 'MMDeviceEnumeratorClass' to type
    //   'NAudio.CoreAudioApi.Interfaces.MMDeviceEnumeratorComObject'
    //
    // That is not an ordering problem to be worked around. NAudio first and the
    // app first each work once, but going back to NAudio after this file has run
    // fails again, so no call order makes both survive. The duplicate binding had
    // to go, and NAudio already provides the same three answers.
    //
    // Please do not reintroduce a local ComImport declaration of the enumerator.
    // The clash is invisible in a unit test, and the only symptom is a dead
    // spectrum at runtime, which is a slow and confusing way to rediscover it.
    public event Action<string>? StatusChanged;

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices()
    {
        List<AudioDeviceInfo> devices = new();
        try
        {
            using MMDeviceEnumerator enumerator = new();
            foreach (MMDevice device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                // Disposed per iteration. An MMDevice holds a COM reference to the
                // endpoint, and this is called on every startup, every rescan and
                // every restore - so without it each of those leaves one reference
                // per output device alive until the finaliser gets to them. The
                // loopback feed disposes its own for exactly this reason, with the
                // same note; the comment simply had not reached here.
                using (device)
                {
                    string name = string.Empty;
                    try
                    {
                        // NAudio already falls back from the friendly name to the
                        // device description, which is what the property store walk
                        // this replaced was doing by hand.
                        name = device.FriendlyName;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex.Message);
                    }

                    try
                    {
                        devices.Add(new AudioDeviceInfo
                        {
                            // Inside the same guard as the name. An endpoint
                            // unplugged between the enumeration and this read
                            // throws here, and the outer catch then abandoned the
                            // rest of the list and returned whatever had been
                            // collected - so one disappearing device could hide
                            // every device after it.
                            Id = device.ID,
                            Name = string.IsNullOrWhiteSpace(name)
                                ? "OUTPUT " + (devices.Count + 1).ToString()
                                : name,
                            FormFactor = ReadFormFactor(device),
                        });
                    }
                    catch (Exception ex)
                    {
                        // A device that cannot be read at all is left out of the list
                        // rather than added half-built, and the reason it vanished
                        // has to be recorded here: from the user's side a missing
                        // endpoint is indistinguishable from one Windows did not
                        // report, and "my headset is not in the list" is not
                        // something anybody can act on without this line.
                        //
                        // Nothing read off the device to name it in. This catch
                        // covers the read of device.ID, so asking for the ID again
                        // to put in the message raises the same failure out of the
                        // handler. The name already read is the only identifier
                        // known to be safe, and it is empty when even that failed.
                        TraceLog.Write(
                            string.IsNullOrWhiteSpace(name) ? "OUTPUT device skipped" : "OUTPUT device skipped: " + name,
                            ex);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("NO DEVICE");

            // The whole enumeration failed, so the picker will be empty and every
            // other explanation for that is a guess. Debug.WriteLine was compiled
            // out of Release, which is the build that ships, so this reached nobody.
            TraceLog.Write("OUTPUT device list unreadable", ex);
        }

        return devices;
    }

    /// <summary>
    /// What Windows says this endpoint physically is, or -1 when it will not say.
    /// <para>
    /// Read as <c>PKEY_AudioEndpoint_FormFactor</c>, which is the answer to the
    /// question the old name-based filter was guessing at: whether this is a monitor's
    /// HDMI audio, which is silent because there are no speakers behind it. Verified
    /// against mmdeviceapi.h - the GUID is
    /// <c>{1DA5D803-D492-4EDD-8C23-E0C0FFEE7F0E}</c> and DigitalAudioDisplayDevice is
    /// 9 - and against a real machine, where a monitor endpoint answered 9 and every
    /// set of speakers answered 1.
    /// </para>
    /// <para>
    /// Three separate failures all mean "ask the name instead", and they are handled
    /// as one because from here they are indistinguishable and act the same way: the
    /// property being absent from an endpoint's store (some virtual drivers do not
    /// publish it), the indexer throwing, and the value arriving as something other
    /// than a <c>uint</c>. The last is not hypothetical - the property is documented
    /// as VT_UI4 but the COM marshalling decides what actually arrives in the box, so
    /// this reads it as a uint and treats anything else as no answer rather than
    /// casting whatever turned up.
    /// </para>
    /// </summary>
    private static int ReadFormFactor(MMDevice device)
    {
        try
        {
            PropertyStore store = device.Properties;

            // Contains first, because the indexer is documented to throw rather than
            // return nothing, and "the driver does not publish this" is a normal
            // answer rather than an exceptional one.
            if (!store.Contains(PropertyKeys.PKEY_AudioEndpoint_FormFactor))
            {
                return -1;
            }

            return store[PropertyKeys.PKEY_AudioEndpoint_FormFactor].Value is uint value
                ? (int)value
                : -1;
        }
        catch (Exception ex)
        {
            // Debug only, not TraceLog. This runs for every endpoint on every launch,
            // and a driver with no form factor would otherwise fill the log with a
            // line the user cannot act on. The failure is already carried by the -1.
            Debug.WriteLine(ex.Message);
            return -1;
        }
    }

    public string GetDefaultOutputName()
    {
        try
        {
            using MMDeviceEnumerator enumerator = new();
            using MMDevice device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            return device.FriendlyName;
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.Message);
            return string.Empty;
        }
    }
}

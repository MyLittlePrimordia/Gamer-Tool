using System;
using System.Collections.Generic;
using System.Diagnostics;
using NAudio.CoreAudioApi;

namespace GamerTool.Services;

public sealed class AudioDeviceInfo
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;
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

                devices.Add(new AudioDeviceInfo
                {
                    Id = device.ID,
                    Name = string.IsNullOrWhiteSpace(name) ? "OUTPUT " + (devices.Count + 1).ToString() : name,
                });
            }
        }
        catch (Exception ex)
        {
            StatusChanged?.Invoke("NO DEVICE");
            Debug.WriteLine(ex.Message);
        }

        return devices;
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

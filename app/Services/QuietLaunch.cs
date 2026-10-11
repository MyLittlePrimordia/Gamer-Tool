using System;
using System.Runtime.InteropServices;
using System.Text;

namespace GamerTool.Services;

/// <summary>
/// Starts a program without Windows putting up its "application starting" cursor.
/// <para>
/// FxSound's command line is the same executable as its window, so it is a GUI
/// program as far as Windows is concerned, and every time one is created the
/// system shows the arrow with the little spinner until the new process either
/// opens a window or goes away. Reading the engine's state starts one of these
/// every second and a half while the window is open, which is a cursor that
/// flickers for no reason anyone can see.
/// </para>
/// <para>
/// CreateProcess has a flag for exactly this, STARTF_FORCEOFFFEEDBACK, and
/// <see cref="System.Diagnostics.Process"/> offers no way to set it, so this goes
/// to CreateProcess directly. It is used for the status read only. The commands
/// that change the engine still go through Process.Start, which is the path they
/// were tuned against.
/// </para>
/// </summary>
internal static class QuietLaunch
{
    private const uint CreateNoWindow = 0x08000000;
    private const int StartfForceOffFeedback = 0x00000080;
    private const uint WaitObject0 = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref StartupInfo startupInfo,
        out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// Runs the program and waits up to <paramref name="waitMs"/> for it to finish.
    /// Returns false only when the process could not be created at all, in which
    /// case nothing ran and the caller should fall back to starting it the ordinary
    /// way. Otherwise the outcome is in <paramref name="exited"/> and
    /// <paramref name="exitCode"/>.
    /// </summary>
    public static bool TryRun(string exePath, string arguments, int waitMs, out bool exited, out int exitCode)
    {
        exited = false;
        exitCode = -1;

        try
        {
            // Quoted the way Process.Start quotes it, so a path with spaces in it
            // (Program Files) reaches the program as one piece.
            StringBuilder commandLine = new();
            commandLine.Append('"').Append(exePath).Append('"');
            if (!string.IsNullOrEmpty(arguments))
            {
                commandLine.Append(' ').Append(arguments);
            }

            StartupInfo startup = new()
            {
                cb = Marshal.SizeOf<StartupInfo>(),
                dwFlags = StartfForceOffFeedback,
            };

            if (!CreateProcessW(
                    exePath,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreateNoWindow,
                    IntPtr.Zero,
                    null,
                    ref startup,
                    out ProcessInformation info))
            {
                TraceLog.Write("FX quiet launch failed, error "
                    + Marshal.GetLastWin32Error().ToString(System.Globalization.CultureInfo.InvariantCulture));
                return false;
            }

            try
            {
                exited = WaitForSingleObject(info.hProcess, (uint)Math.Max(0, waitMs)) == WaitObject0;

                if (exited && GetExitCodeProcess(info.hProcess, out uint code))
                {
                    exitCode = unchecked((int)code);
                }
                else if (exited)
                {
                    // Finished, but the code could not be read: not a success.
                    exitCode = -1;
                }

                return true;
            }
            finally
            {
                _ = CloseHandle(info.hProcess);
                _ = CloseHandle(info.hThread);
            }
        }
        catch (Exception ex)
        {
            TraceLog.Write("FX quiet launch", ex);
            return false;
        }
    }
}

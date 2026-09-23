using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace GamerTool.Services;

/// <summary>
/// HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio is owned by
/// TrustedInstaller. Administrators can modify existing registry VALUES under
/// it, but cannot create, delete, or rename subKEYS (like FxProperties, which
/// often doesn't exist yet on an endpoint that has never had an APO). This is
/// the same class of permission trap that caused the original "Access is
/// denied" bug, one layer deeper in the stack.
///
/// The documented manual fix (see https://github.com/dechamps/APO) is: open
/// regedit, take ownership of the Audio key as Administrators, grant Full
/// Control. This class does exactly that programmatically, using the same
/// Win32 mechanism regedit itself uses (SeTakeOwnershipPrivilege +
/// SetAccessControl), so GamerTool's setup never requires the user to touch
/// the registry editor by hand.
///
/// Must be called from an already-elevated (admin) process — the privileges
/// exist on every admin token but are disabled by default, so we enable them
/// explicitly first.
/// </summary>
public static class RegistryOwnershipHelper
{
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool LookupPrivilegeValue(string? host, string name, out LUID luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool AdjustTokenPrivileges(IntPtr tokenHandle, bool disableAllPrivileges,
        ref TOKEN_PRIVILEGES newState, uint bufferLength, IntPtr previousState, IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    private struct LUID_AND_ATTRIBUTES { public LUID Luid; public uint Attributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TOKEN_PRIVILEGES { public uint PrivilegeCount; public LUID_AND_ATTRIBUTES Privileges; }

    private const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    private const uint TOKEN_QUERY = 0x0008;
    private const uint SE_PRIVILEGE_ENABLED = 0x0002;

    private static void EnablePrivilege(string privilegeName)
    {
        if (!OpenProcessToken(System.Diagnostics.Process.GetCurrentProcess().Handle,
                TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out IntPtr tokenHandle))
            throw new InvalidOperationException(
                $"Could not open process token to enable {privilegeName}. Is this process elevated?");

        try
        {
            if (!LookupPrivilegeValue(null, privilegeName, out LUID luid))
                throw new InvalidOperationException($"Could not look up privilege {privilegeName}.");

            var tp = new TOKEN_PRIVILEGES
            {
                PrivilegeCount = 1,
                Privileges = new LUID_AND_ATTRIBUTES { Luid = luid, Attributes = SE_PRIVILEGE_ENABLED }
            };

            if (!AdjustTokenPrivileges(tokenHandle, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero))
                throw new InvalidOperationException($"Could not enable privilege {privilegeName}.");
        }
        finally
        {
            CloseHandle(tokenHandle);
        }
    }

    /// <summary>
    /// Takes ownership of <paramref name="keyPath"/> (relative to HKLM) as the
    /// local Administrators group, then grants that group Full Control, so
    /// subsequent code can freely create/delete subkeys under it. Reversible
    /// only by another explicit ownership change — this is a deliberate,
    /// narrowly-scoped permission change, not a blanket registry unlock.
    /// </summary>
    public static void TakeOwnershipAndGrantAdmins(string keyPath)
    {
        EnablePrivilege("SeTakeOwnershipPrivilege");
        EnablePrivilege("SeRestorePrivilege");

        var adminsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        // Step 1: open with the minimal rights needed to change ownership, and set it.
        using (var key = Registry.LocalMachine.OpenSubKey(keyPath,
                   RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.TakeOwnership))
        {
            if (key == null)
                throw new InvalidOperationException($"Registry key not found: HKLM\\{keyPath}");

            var security = new RegistrySecurity();
            security.SetOwner(adminsSid);
            key.SetAccessControl(security);
        }

        // Step 2: now that Administrators own it, reopen with ChangePermissions
        // rights and grant Full Control so normal SetValue/CreateSubKey calls work.
        using (var key = Registry.LocalMachine.OpenSubKey(keyPath,
                   RegistryKeyPermissionCheck.ReadWriteSubTree, RegistryRights.ChangePermissions))
        {
            if (key == null)
                throw new InvalidOperationException($"Registry key not found after taking ownership: HKLM\\{keyPath}");

            var security = key.GetAccessControl();
            security.AddAccessRule(new RegistryAccessRule(
                adminsSid,
                RegistryRights.FullControl,
                InheritanceFlags.ContainerInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            key.SetAccessControl(security);
        }
    }
}

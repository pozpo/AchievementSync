using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AchievementSync.Desktop.Services;

/// <summary>Stores the OAuth token in the current Windows user's Credential Manager.</summary>
internal sealed class WindowsCredentialTokenStore
{
    private const string Target = "AchievementSync/GitHub/OAuthToken";
    private const uint GenericCredential = 1;
    private const uint PersistLocal = 2;
    private const int NotFound = 1168;

    public string? Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("GitHub sign-in storage requires Windows Credential Manager.");
        if (!CredRead(Target, GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error, "Could not read the AchievementSync token from Windows Credential Manager.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            if (credential.CredentialBlobSize > 5120) throw new InvalidDataException("The stored GitHub credential has an invalid size.");
            var bytes = new byte[(int)credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try { return Encoding.UTF8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Write(string token)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("GitHub sign-in storage requires Windows Credential Manager.");
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("The access token is empty.", nameof(token));

        var bytes = Encoding.UTF8.GetBytes(token);
        var blobPointer = Marshal.AllocHGlobal(bytes.Length);
        var targetPointer = Marshal.StringToHGlobalUni(Target);
        var usernamePointer = Marshal.StringToHGlobalUni("AchievementSync");
        var zeroedBytes = new byte[bytes.Length];
        try
        {
            Marshal.Copy(bytes, 0, blobPointer, bytes.Length);
            var credential = new NativeCredential
            {
                Type = GenericCredential,
                TargetName = targetPointer,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blobPointer,
                Persist = PersistLocal,
                UserName = usernamePointer
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the AchievementSync token to Windows Credential Manager.");
        }
        finally
        {
            Marshal.Copy(zeroedBytes, 0, blobPointer, zeroedBytes.Length);
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.FreeHGlobal(blobPointer);
            Marshal.FreeHGlobal(targetPointer);
            Marshal.FreeHGlobal(usernamePointer);
        }
    }

    public void Delete()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("GitHub sign-in storage requires Windows Credential Manager.");
        if (!CredDelete(Target, GenericCredential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NotFound) throw new Win32Exception(error, "Could not remove the AchievementSync token from Windows Credential Manager.");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string targetName, uint type, uint flags, out IntPtr credential);

    [DllImport("Advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string targetName, uint type, uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}

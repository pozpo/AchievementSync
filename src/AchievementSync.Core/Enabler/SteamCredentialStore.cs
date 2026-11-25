using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AchievementSync.Core.Enabler;

/// <summary>Stores Steam credentials (username, password, refresh token) in Windows Credential Manager.</summary>
public sealed class SteamCredentialStore
{
    private const string Target = "AchievementSync/Steam/Credentials";
    private const uint GenericCredential = 1;
    private const uint PersistLocal = 2;
    private const int NotFound = 1168;
    private readonly string _target;

    public SteamCredentialStore(string target = Target) =>
        _target = string.IsNullOrWhiteSpace(target) ? throw new ArgumentException("A credential target is required.", nameof(target)) : target;

    public SteamCredentials? Read()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Steam credential storage requires Windows Credential Manager.");
        if (!CredRead(_target, GenericCredential, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == NotFound) return null;
            throw new Win32Exception(error, "Could not read the Steam credential from Windows Credential Manager.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            if (credential.CredentialBlobSize > 5120) throw new InvalidDataException("The stored Steam credential has an invalid size.");
            var bytes = new byte[(int)credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                var json = Encoding.UTF8.GetString(bytes);
                return JsonSerializer.Deserialize<SteamCredentials>(json);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Write(SteamCredentials credentials)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Steam credential storage requires Windows Credential Manager.");
        if (credentials is null) throw new ArgumentNullException(nameof(credentials));
        if (string.IsNullOrWhiteSpace(credentials.Username)) throw new ArgumentException("The Steam username is empty.", nameof(credentials.Username));

        var json = JsonSerializer.Serialize(credentials);
        var bytes = Encoding.UTF8.GetBytes(json);
        var blobPointer = Marshal.AllocHGlobal(bytes.Length);
        var targetPointer = Marshal.StringToHGlobalUni(_target);
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
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the Steam credential to Windows Credential Manager.");
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
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Steam credential storage requires Windows Credential Manager.");
        if (!CredDelete(_target, GenericCredential, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != NotFound) throw new Win32Exception(error, "Could not remove the Steam credential from Windows Credential Manager.");
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

/// <summary>Steam credentials stored in Windows Credential Manager.</summary>
public sealed class SteamCredentials
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string RefreshToken { get; set; } = string.Empty;
    public string? RefreshTokenExpiresAtUtc { get; set; }
    public DateTime LastLoginUtc { get; set; }
    public string? AccountName { get; set; }
    public string? PersonaName { get; set; }
    public ulong SteamId64 { get; set; }
}

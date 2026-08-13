using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace VrcVa.Windows.Security;

internal sealed class WindowsCredentialStore(
    string targetName = "VrcVa/OpenAIApiKey")
{
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaximumSecretUtf8Bytes = 2_048;

    public string? Read()
    {
        if (!CredRead(targetName, CredentialTypeGeneric, 0, out nint credentialPointer))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            throw new Win32Exception(error, "Windows Credential Manager could not read the API key.");
        }

        try
        {
            NativeCredential credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == nint.Zero || credential.CredentialBlobSize == 0)
            {
                return null;
            }

            byte[] bytes = new byte[credential.CredentialBlobSize];
            try
            {
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public void Write(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            throw new ArgumentException("API key cannot be empty.", nameof(secret));
        }

        byte[] bytes = Encoding.UTF8.GetBytes(secret.Trim());
        if (bytes.Length > MaximumSecretUtf8Bytes)
        {
            CryptographicOperations.ZeroMemory(bytes);
            throw new ArgumentException("API key is too long.", nameof(secret));
        }

        nint blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            NativeCredential credential = new()
            {
                Type = CredentialTypeGeneric,
                TargetName = targetName,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = Environment.UserName,
            };

            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Windows Credential Manager could not save the API key.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            for (int index = 0; index < bytes.Length; index++)
            {
                Marshal.WriteByte(blob, index, 0);
            }

            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete()
    {
        if (CredDelete(targetName, CredentialTypeGeneric, 0))
        {
            return;
        }

        int error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
        {
            throw new Win32Exception(error, "Windows Credential Manager could not delete the API key.");
        }
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(
        string target,
        uint type,
        uint flags,
        out nint credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint credential);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }
}

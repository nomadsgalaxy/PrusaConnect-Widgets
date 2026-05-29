using System;
using System.Runtime.InteropServices;

namespace PrusaConnect.Core.Storage;

/// <summary>
/// Thin P/Invoke wrapper over Windows Credential Manager (CredRead). We use it to
/// read PrusaSlicer's stored OAuth tokens so the widget can borrow its session
/// instead of running its own browser flow.
/// </summary>
public static class WindowsCredentialManager
{
    public sealed record StoredCredential(string TargetName, string? UserName, byte[] Blob);

    /// <summary>
    /// Read a generic credential by target name. Returns <c>null</c> if no
    /// entry exists (NOT an error - the caller decides what to do).
    /// </summary>
    public static StoredCredential? Read(string targetName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);

        if (!CredRead(targetName, CRED_TYPE_GENERIC, 0, out IntPtr credPtr))
        {
            int err = Marshal.GetLastWin32Error();
            if (err == ERROR_NOT_FOUND)
            {
                return null;
            }
            throw new System.ComponentModel.Win32Exception(err);
        }

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            byte[] blob = new byte[cred.CredentialBlobSize];
            if (cred.CredentialBlobSize > 0 && cred.CredentialBlob != IntPtr.Zero)
            {
                Marshal.Copy(cred.CredentialBlob, blob, 0, (int)cred.CredentialBlobSize);
            }
            return new StoredCredential(cred.TargetName ?? targetName, cred.UserName, blob);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    private const int CRED_TYPE_GENERIC = 1;
    private const int ERROR_NOT_FOUND = 1168;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string? TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("Advapi32.dll", SetLastError = true, EntryPoint = "CredReadW",
               CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}

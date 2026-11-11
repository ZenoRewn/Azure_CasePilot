using System.ComponentModel;
using System.Runtime.InteropServices;
using CasePilot.Core.Abstractions;

namespace CasePilot.Platform;

/// <summary>
/// Windows 凭据管理器 (CRED_TYPE_GENERIC, CRED_PERSIST_LOCAL_MACHINE). 写入空值即删除.
/// 只允许 <c>CasePilot/</c> 与 <c>CasePilot.Test/</c> 前缀, 避免误改其他程序的凭据.
/// </summary>
public sealed partial class CredentialStore : ISecretStore
{
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;
    private const int ERROR_NOT_FOUND = 1168;
    private const int MaxBlob = 5 * 512;

    public string? Read(string target)
    {
        Check(target);
        if (!CredReadW(target, CRED_TYPE_GENERIC, 0, out var ptr))
        {
            var err = Marshal.GetLastPInvokeError();
            if (err == ERROR_NOT_FOUND) return null;
            throw new Win32Exception(err);
        }
        try
        {
            var c = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (c.CredentialBlobSize == 0 || c.CredentialBlob == 0) return null;
            return Marshal.PtrToStringUni(c.CredentialBlob, c.CredentialBlobSize / 2);
        }
        finally { CredFree(ptr); }
    }

    public void Write(string target, string? value)
    {
        Check(target);
        if (string.IsNullOrEmpty(value))
        {
            if (!CredDeleteW(target, CRED_TYPE_GENERIC, 0))
            {
                var err = Marshal.GetLastPInvokeError();
                if (err != ERROR_NOT_FOUND) throw new Win32Exception(err);
            }
            return;
        }
        var size = value.Length * 2;
        if (size > MaxBlob) throw new ArgumentException("凭据过长。", nameof(value));
        var blob = Marshal.StringToHGlobalUni(value);
        var name = Marshal.StringToHGlobalUni(target);
        var user = Marshal.StringToHGlobalUni("CasePilot");
        try
        {
            var c = new CREDENTIAL
            {
                Type = CRED_TYPE_GENERIC,
                TargetName = name,
                CredentialBlobSize = size,
                CredentialBlob = blob,
                Persist = CRED_PERSIST_LOCAL_MACHINE,
                UserName = user,
            };
            if (!CredWriteW(ref c, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            Marshal.ZeroFreeGlobalAllocUnicode(blob);
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(user);
        }
    }

    private static void Check(string target)
    {
        if (string.IsNullOrWhiteSpace(target) ||
            !(target.StartsWith("CasePilot/", StringComparison.Ordinal) || target.StartsWith("CasePilot.Test/", StringComparison.Ordinal)))
            throw new ArgumentException($"凭据名称必须以 CasePilot/ 开头: {target}", nameof(target));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public nint CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredReadW(string target, int type, int flags, out nint credential);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredWriteW(ref CREDENTIAL credential, int flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CredDeleteW(string target, int type, int flags);

    [LibraryImport("advapi32.dll")]
    private static partial void CredFree(nint buffer);
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CasePilot.Core.Abstractions;

namespace CasePilot.Platform;

/// <summary>剪贴板 (Win32 CF_UNICODETEXT) 与打开链接 (仅 https 与 ms-settings).</summary>
public sealed partial class ShellOps : IShell
{
    [GeneratedRegex("^ms-settings:[a-z0-9-]+$")]
    private static partial Regex SettingsUri();

    public static bool IsAllowedUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (SettingsUri().IsMatch(url)) return true;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(u.Host);
    }

    public void OpenUrl(string url)
    {
        if (!IsAllowedUrl(url)) throw new ArgumentException($"不允许打开的链接: {url}", nameof(url));
        using var _ = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    public void Copy(string text)
    {
        const uint CF_UNICODETEXT = 13, GMEM_MOVEABLE = 2;
        for (var i = 0; !OpenClipboard(0); i++)
        {
            if (i >= 10) throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法打开剪贴板。");
            Thread.Sleep(20);
        }
        try
        {
            EmptyClipboard();
            var bytes = (text.Length + 1) * 2;
            var h = GlobalAlloc(GMEM_MOVEABLE, (nuint)bytes);
            if (h == 0) throw new OutOfMemoryException();
            var p = GlobalLock(h);
            try
            {
                Marshal.Copy(text.ToCharArray(), 0, p, text.Length);
                Marshal.WriteInt16(p, text.Length * 2, 0);
            }
            finally { GlobalUnlock(h); }
            if (SetClipboardData(CF_UNICODETEXT, h) == 0)
            {
                GlobalFree(h);
                throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
        finally { CloseClipboard(); }
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenClipboard(nint owner);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseClipboard();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool EmptyClipboard();

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial nint SetClipboardData(uint format, nint mem);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalLock(nint mem);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalUnlock(nint mem);

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalFree(nint mem);
}

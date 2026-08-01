using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Hypertree.Launch;

namespace Hypertree.Platform.Windows;

/// <summary>
/// Windows <see cref="IAppIconProvider"/>: pulls the icon the shell would show for an entry and hands it
/// back as PNG bytes, so nothing GDI crosses into the OS-free layers. Two paths, chosen by the launch
/// target: a Start-menu <c>.lnk</c> (or any file) goes through <c>SHGetFileInfo</c> → <c>HICON</c>, which
/// keeps the icon's alpha; a packaged app ("shell:AppsFolder\…" moniker) has no file to point at, so it
/// goes through <c>IShellItemImageFactory</c> → <c>HBITMAP</c>. Best-effort throughout — an unextractable
/// icon is a null, never an exception — and every native handle is released.
/// </summary>
public sealed class ShellIconProvider : IAppIconProvider
{
    private const int IconSize = 48; // requested icon edge in px; the launcher draws it at 20

    public byte[]? GetIconPng(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        return path.StartsWith("shell:", StringComparison.OrdinalIgnoreCase)
            ? FromShellItem(path)  // packaged app — no file, resolve the moniker's image
            : FromFileIcon(path);  // a .lnk / .url / .exe / file — the shell's file icon
    }

    // ── Start-menu shortcuts & files: SHGetFileInfo → HICON (alpha-correct) ──────────────

    private const uint SHGFI_ICON = 0x000000100;
    private const uint SHGFI_LARGEICON = 0x000000000; // 32×32 — scaled down cleanly for a launcher row

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public nint hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string pszPath, uint dwFileAttributes,
                                             ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(nint hIcon);

    private static byte[]? FromFileIcon(string path)
    {
        var info = new SHFILEINFO();
        nint result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(),
                                    SHGFI_ICON | SHGFI_LARGEICON);
        if (result == 0 || info.hIcon == 0) return null;

        try
        {
            using Icon icon = Icon.FromHandle(info.hIcon);
            using Bitmap bmp = icon.ToBitmap();
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(info.hIcon); // SHGetFileInfo hands us an owned HICON — release it either way
        }
    }

    // ── Packaged apps: SHCreateItemFromParsingName → IShellItemImageFactory → HBITMAP ────

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; public SIZE(int c) { cx = c; cy = c; } }

    // SIIGBF: icon (not a thumbnail), and accept a bigger source we can scale down for crispness.
    private const int SIIGBF_ICONONLY = 0x04;
    private const int SIIGBF_BIGGERSIZEOK = 0x01;

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(SIZE size, int flags, out nint phbm);
    }

    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string pszPath, nint pbc, in Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint hObject);

    private static byte[]? FromShellItem(string parsingName)
    {
        IShellItemImageFactory? factory = null;
        nint hbitmap = 0;
        try
        {
            SHCreateItemFromParsingName(parsingName, 0, IID_IShellItemImageFactory, out factory);
            factory.GetImage(new SIZE(IconSize), SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbitmap);
            if (hbitmap == 0) return null;

            // The factory hands back a 32-bit bitmap; FromHbitmap reads it as opaque RGB, so a transparent
            // corner comes through near-black — invisible against the launcher's near-black card, which is
            // where these ever render. Good enough without an alpha-preserving DIB copy.
            using Bitmap bmp = Image.FromHbitmap(hbitmap);
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbitmap != 0) DeleteObject(hbitmap);
            if (factory is not null) Marshal.FinalReleaseComObject(factory);
        }
    }
}

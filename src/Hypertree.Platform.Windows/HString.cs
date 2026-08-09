using System.Runtime.InteropServices;

namespace Hypertree.Platform.Windows;

/// <summary>
/// Manual WinRT HSTRING marshalling via combase.dll. .NET 5+ removed the built-in
/// <c>UnmanagedType.HString</c> marshaller, so the virtual-desktop name APIs (which take/return
/// HSTRINGs) go through this helper instead. Discovered during the M0 spike — see
/// docs/design/m0-findings.md.
/// </summary>
internal static class HString
{
    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string src, int length, out nint hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(nint hstring);

    [DllImport("combase.dll")]
    private static extern nint WindowsGetStringRawBuffer(nint hstring, out uint length);

    /// <summary>Create an HSTRING. Free it with <see cref="Delete"/>.</summary>
    /// <remarks>
    /// On any non-success HRESULT the string wasn't created, so we return a clean null handle rather than
    /// the (possibly partial) out value: <see cref="Delete"/> then no-ops and the caller degrades to an
    /// empty name. That's the right failure mode here — the desktop-name APIs this feeds are best-effort
    /// and tolerate an empty name, whereas throwing would take the tray down on a create/rename.
    /// </remarks>
    public static nint Create(string s)
        => WindowsCreateString(s, s.Length, out nint h) == 0 ? h : 0; // 0 == S_OK

    public static void Delete(nint h)
    {
        if (h != 0) WindowsDeleteString(h);
    }

    /// <summary>Read an HSTRING to a managed string (empty if null).</summary>
    public static string Read(nint h)
    {
        if (h == 0) return "";
        nint buf = WindowsGetStringRawBuffer(h, out uint len);
        return len == 0 ? "" : Marshal.PtrToStringUni(buf, (int)len) ?? "";
    }
}

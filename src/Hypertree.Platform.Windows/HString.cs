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
    public static nint Create(string s)
    {
        WindowsCreateString(s, s.Length, out nint h);
        return h;
    }

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

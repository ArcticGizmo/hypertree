using System.Runtime.InteropServices;

namespace Hypertree.Cli;

/// <summary>
/// Console output conventions: where each kind of message goes, and whether colour is appropriate.
/// </summary>
/// <remarks>
/// The rules are the ordinary command-line ones, and they matter because this tool is meant to be piped
/// and scripted as often as it's read: results go to stdout so <c>htree list | grep</c> works, diagnostics
/// go to stderr so they don't pollute that pipe, and colour is emitted only when a human on a terminal is
/// actually going to see it — never into a redirect, and never when <c>NO_COLOR</c> is set.
/// </remarks>
internal static class Output
{
    private static readonly bool Colour = ShouldColour();

    public const string Reset = "[0m";
    public const string Dim = "[2m";
    public const string Bold = "[1m";
    public const string Cyan = "[36m";
    public const string Yellow = "[33m";
    public const string Red = "[31m";

    /// <summary>Wrap <paramref name="text"/> in an escape, or return it untouched when colour is off.</summary>
    public static string Paint(string text, string colour) => Colour ? colour + text + Reset : text;

    /// <summary>A result line — the thing the caller asked for. stdout.</summary>
    public static void Line(string text = "") => Console.Out.WriteLine(text);

    /// <summary>A diagnostic. stderr, so it stays out of a pipe, and prefixed so it's identifiable there.</summary>
    public static void Error(string text) => Console.Error.WriteLine($"{Paint("htree:", Red)} {text}");

    private static bool ShouldColour()
    {
        // https://no-color.org — any value at all means "don't".
        if (Environment.GetEnvironmentVariable("NO_COLOR") is not null) return false;
        if (Console.IsOutputRedirected) return false;
        return !OperatingSystem.IsWindows() || TryEnableVirtualTerminal();
    }

    // Modern Windows consoles understand ANSI, but only once the mode bit is set; the older conhost
    // never will. Asking the OS to turn it on — and believing the answer — is the only way to know which
    // one we're talking to, and it beats sniffing environment variables for terminal emulators.
    private static bool TryEnableVirtualTerminal()
    {
        try
        {
            nint handle = GetStdHandle(StdOutputHandle);
            if (handle == 0 || handle == -1) return false;
            if (!GetConsoleMode(handle, out uint mode)) return false; // not a console at all
            if ((mode & EnableVirtualTerminalProcessing) != 0) return true;
            return SetConsoleMode(handle, mode | EnableVirtualTerminalProcessing);
        }
        catch { return false; }
    }

    private const int StdOutputHandle = -11;
    private const uint EnableVirtualTerminalProcessing = 0x0004;

    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint GetStdHandle(int nStdHandle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetConsoleMode(nint handle, out uint mode);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleMode(nint handle, uint mode);
}

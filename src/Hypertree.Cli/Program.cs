using System.Reflection;
using Hypertree.Ipc;

namespace Hypertree.Cli;

/// <summary>
/// <c>htree</c> — Hypertree from the command line.
/// </summary>
/// <remarks>
/// <para>Reads are served straight from <c>%APPDATA%\hypertree\status.json</c>, which the tray keeps
/// current; only <c>goto</c> needs the tray itself, over the control pipe. That split is what makes
/// <c>status</c> cheap enough to put in a shell prompt: it opens one small file and exits, with no IPC
/// round trip and nothing to wake up.</para>
///
/// <para>Exit codes are the contract (see <see cref="ExitCode"/>) — 0 done, 1 no tray, 2 unknown target,
/// 3 bad usage, 4 the tray refused. Scripts branch on those; the text is for humans.</para>
/// </remarks>
internal static class Program
{
    private static int Main(string[] argv)
    {
        var args = Args.Parse(argv);

        if (args.Has("--version") || args.Has("-V"))
        {
            Output.Line(Version);
            return ExitCode.Ok;
        }

        if (args.Command is null || args.Has("--help") || args.Has("-h"))
        {
            // No command isn't an error worth a non-zero exit when help was what they wanted, but a bare
            // invocation with no idea what to do is: it usually means a script built an empty argument.
            Help.Print();
            return args.Command is null && !args.Has("--help") && !args.Has("-h")
                ? ExitCode.BadUsage
                : ExitCode.Ok;
        }

        return args.Command switch
        {
            "status" => Commands.Status(args),
            "list" or "ls" => Commands.List(args),
            "goto" or "go" => Commands.Goto(args),
            "populate" or "pop" => Commands.Populate(args),
            "watch" => Commands.Watch(args),
            "help" => Ok(Help.Print),
            _ => Unknown(args.Command),
        };
    }

    private static int Ok(Action action)
    {
        action();
        return ExitCode.Ok;
    }

    private static int Unknown(string command)
    {
        Output.Error($"Unknown command '{command}'. Try: htree help");
        return ExitCode.BadUsage;
    }

    /// <summary>The CLI's own version, from the assembly, so it can't drift from the build.</summary>
    public static string Version =>
        Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion.Split('+')[0]
        ?? "0.0.0";
}

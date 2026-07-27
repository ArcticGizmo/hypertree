using Hypertree.Status;

namespace Hypertree.Cli;

/// <summary>
/// The help text. Written out longhand rather than generated from a parser, because the examples are the
/// part people actually read and they're worth choosing by hand — including the prompt-integration ones,
/// which are the reason <c>status</c> is shaped the way it is.
/// </summary>
internal static class Help
{
    public static void Print()
    {
        string b(string s) => Output.Paint(s, Output.Bold);
        string d(string s) => Output.Paint(s, Output.Dim);

        Output.Line($"{b("htree")} {Program.Version} — Hypertree from the command line");
        Output.Line();
        Output.Line(b("USAGE"));
        Output.Line("  htree <command> [options]");
        Output.Line();
        Output.Line(b("COMMANDS"));
        Output.Line($"  status              Where you are now, as {d("branch/desktop")}");
        Output.Line("  list, ls            The stack, top to bottom, main in its slot");
        Output.Line("  goto, go <target>   Jump to a branch, or a desktop on it");
        Output.Line("  watch               Stream position changes until interrupted");
        Output.Line("  help                This text");
        Output.Line();
        Output.Line(b("TARGETS"));
        Output.Line("  main                The main timeline, at its remembered desktop");
        Output.Line("  <branch>            A branch, at its resume desktop");
        Output.Line("  <branch>/<desktop>  A specific desktop, by label or 1-based position");
        Output.Line($"  --id <guid>         A branch by its stable id {d("(what other tools use)")}");
        Output.Line();
        Output.Line($"  {d("Branch names match exactly, then case-insensitively, then by unique prefix.")}");
        Output.Line($"  {d("An ambiguous name is refused, never guessed — it lists the ids to pick from.")}");
        Output.Line();
        Output.Line(b("OPTIONS"));
        Output.Line("  --json              Machine-readable output (watch emits JSON Lines)");
        Output.Line("  -a, --all           list: expand every desktop, not just the resume point");
        Output.Line("      --branch        status: print only the branch name");
        Output.Line("      --desktop       status: print only the desktop label");
        Output.Line("  -v, --verbose       goto: print where it landed");
        Output.Line("  -V, --version       Print the version");
        Output.Line();
        Output.Line(b("EXIT CODES"));
        Output.Line("  0 done   1 no tray running   2 unknown target   3 bad usage   4 tray refused");
        Output.Line();
        Output.Line(b("EXAMPLES"));
        Output.Line($"  htree goto perch               {d("# that branch's resume desktop")}");
        Output.Line($"  htree goto perch/docs          {d("# a named desktop on it")}");
        Output.Line($"  htree goto notes/2             {d("# the second desktop on 'notes'")}");
        Output.Line($"  htree list --all               {d("# the whole layout")}");
        Output.Line($"  htree watch | while read p; do notify \"$p\"; done");
        Output.Line();
        Output.Line($"  {d("PowerShell prompt:")}");
        Output.Line("  function prompt {");
        Output.Line("    $ht = htree status 2>$null");
        Output.Line("    if ($ht) { \"[$ht] PS $($PWD.Path)> \" } else { \"PS $($PWD.Path)> \" }");
        Output.Line("  }");
        Output.Line();
        Output.Line($"  {d("bash/zsh prompt:")}");
        Output.Line("  PS1='$(htree status 2>/dev/null | sed \"s/.*/[&] /\")\\w\\$ '");
        Output.Line();
        Output.Line(b("NOTES"));
        Output.Line($"  Reads come from {d(StatusFile.FilePath)}");
        Output.Line("  which the tray keeps current — including switches made outside Hypertree");
        Output.Line("  (Win+Ctrl+Arrow, Task View). Only 'goto' talks to the tray.");
    }
}

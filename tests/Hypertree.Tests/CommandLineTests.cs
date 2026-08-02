using Hypertree.Launch;
using Xunit;

namespace Hypertree.Tests;

/// <summary>
/// <see cref="CommandLine.Split"/> turning a typed command line into target + arguments (so the recipe
/// builder can accept a natural single line), and <see cref="CommandLine.Join"/> recomposing it for display.
/// </summary>
public class CommandLineTests
{
    [Theory]
    [InlineData("code C:\\proj", "code", "C:\\proj")]
    [InlineData("wt -d \"C:\\a b\"", "wt", "-d \"C:\\a b\"")]
    [InlineData("notepad", "notepad", "")]
    [InlineData("", "", "")]
    [InlineData("   spaced   out  ", "spaced", "out")]
    public void Split_takes_the_first_token_as_the_target(string input, string target, string args)
    {
        var (t, a) = CommandLine.Split(input);
        Assert.Equal(target, t);
        Assert.Equal(args, a);
    }

    [Fact]
    public void Split_unwraps_a_leading_quoted_path_with_spaces()
    {
        var (t, a) = CommandLine.Split("\"C:\\Program Files\\App\\app.exe\" --flag x");
        Assert.Equal(@"C:\Program Files\App\app.exe", t);
        Assert.Equal("--flag x", a);
    }

    [Fact]
    public void Join_requotes_a_target_with_spaces()
    {
        Assert.Equal("\"C:\\Program Files\\app.exe\" --flag", CommandLine.Join(@"C:\Program Files\app.exe", "--flag"));
        Assert.Equal("code C:\\proj", CommandLine.Join("code", "C:\\proj"));
        Assert.Equal("notepad", CommandLine.Join("notepad", ""));
    }

    [Fact]
    public void Split_then_join_round_trips_a_simple_line()
    {
        var (t, a) = CommandLine.Split("code C:\\proj");
        Assert.Equal("code C:\\proj", CommandLine.Join(t, a));
    }
}

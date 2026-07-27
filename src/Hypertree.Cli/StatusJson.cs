using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Hypertree.Status;

namespace Hypertree.Cli;

/// <summary>
/// Source-generated serialisation for <c>--json</c> output, in both shapes the CLI emits.
/// </summary>
/// <remarks>
/// Two contexts rather than one because indentation is fixed at generation time, and the two uses want
/// opposite things: a human running <c>htree list --json</c> wants it readable, while <c>htree watch
/// --json</c> must emit one self-contained line per event so it can be piped into anything that reads
/// JSON Lines. Core keeps its own context internal, so the CLI declares its own rather than widening it.
/// </remarks>
internal static partial class StatusJson
{
    public static JsonTypeInfo<StatusSnapshot> Indented => IndentedContext.Default.StatusSnapshot;
    public static JsonTypeInfo<StatusSnapshot> Compact => CompactContext.Default.StatusSnapshot;

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true)]
    [JsonSerializable(typeof(StatusSnapshot))]
    internal sealed partial class IndentedContext : JsonSerializerContext;

    [JsonSourceGenerationOptions(
        PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false)]
    [JsonSerializable(typeof(StatusSnapshot))]
    internal sealed partial class CompactContext : JsonSerializerContext;
}

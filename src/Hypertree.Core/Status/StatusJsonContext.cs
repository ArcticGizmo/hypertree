using System.Text.Json.Serialization;

namespace Hypertree.Status;

/// <summary>
/// Source-generated serialisation for the status contract.
/// </summary>
/// <remarks>
/// Reflection-based <c>JsonSerializer</c> can't be trimmed or ahead-of-time compiled, and <c>htree</c>
/// wants to be: a CLI whose <c>status</c> command is meant to sit in a shell prompt runs on every command
/// the user types, so process startup is the dominant cost and AOT removes most of it. Generating the
/// converters here keeps that option open for both sides — the tray writes the file with the same code
/// the CLI reads it with.
/// <para>
/// Indentation is on because a human may well open this file, or diff it, while working out why a reader
/// is showing something unexpected.
/// </para>
/// </remarks>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(StatusSnapshot))]
internal sealed partial class StatusJsonContext : JsonSerializerContext;

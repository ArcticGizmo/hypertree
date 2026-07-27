using System.Text.Json.Serialization;

namespace Hypertree.Ipc;

/// <summary>
/// Source-generated serialisation for the control protocol, for the same reason as
/// <c>StatusJsonContext</c>: <c>htree</c> is published ahead-of-time compiled, and reflection-based
/// serialisation can't be. Compact rather than indented — nothing reads the wire but the two ends.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ControlRequest))]
[JsonSerializable(typeof(ControlResponse))]
internal sealed partial class IpcJsonContext : JsonSerializerContext;

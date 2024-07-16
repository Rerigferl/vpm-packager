using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

[JsonSerializable(typeof(JsonNode))]
internal sealed partial class JsonSerializerContexts : JsonSerializerContext;
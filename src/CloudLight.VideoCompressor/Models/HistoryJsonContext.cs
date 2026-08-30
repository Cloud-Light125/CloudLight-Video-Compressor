using System.Text.Json.Serialization;

namespace CloudLight.VideoCompressor.Models;

[JsonSerializable(typeof(List<CompressionHistoryEntry>))]
internal partial class HistoryJsonContext : JsonSerializerContext;

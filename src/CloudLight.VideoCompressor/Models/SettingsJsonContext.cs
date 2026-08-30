using System.Text.Json.Serialization;

namespace CloudLight.VideoCompressor.Models;

[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(CompressionRule))]
internal partial class SettingsJsonContext : JsonSerializerContext;

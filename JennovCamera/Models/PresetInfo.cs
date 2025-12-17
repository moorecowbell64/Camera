using System.Text.Json.Serialization;

namespace JennovCamera.Models;

public class PresetInfo
{
    [JsonPropertyName("index")]
    public int Index { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;
}

public class PresetList
{
    [JsonPropertyName("presets")]
    public PresetInfo[] Presets { get; set; } = Array.Empty<PresetInfo>();
}

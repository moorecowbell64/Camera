using System.Text.Json.Serialization;

namespace JennovCamera.Models;

public class JsonRpcResponse<T>
{
    [JsonPropertyName("result")]
    public T? Result { get; set; }

    [JsonPropertyName("error")]
    public JsonRpcError? Error { get; set; }

    [JsonPropertyName("session")]
    public int? Session { get; set; }

    [JsonPropertyName("id")]
    public int Id { get; set; }

    public bool IsSuccess => Error == null;
}

public class JsonRpcError
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("message")]
    public string Message { get; set; } = string.Empty;
}

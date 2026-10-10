using System.Text.Json.Serialization;

namespace SyncAccount.Models;

public class HealthResponse
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("db")]
    public string? Db { get; set; }

    [JsonPropertyName("redis")]
    public string? Redis { get; set; }
}

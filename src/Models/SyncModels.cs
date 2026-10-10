using System.Text.Json.Serialization;

namespace SyncAccount.Models;

public class SyncResponse
{
    [JsonPropertyName("success")]
    public bool Success { get; set; }

    [JsonPropertyName("data")]
    public SyncUserData? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("code")]
    public string? Code { get; set; }
}

public class SyncUserData
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("image")]
    public string? Image { get; set; }
}

public sealed class AccountSyncResult
{
    private AccountSyncResult(bool isRegistered, string? name, byte[]? avatar, string reason)
    {
        IsRegistered = isRegistered;
        Name = name;
        Avatar = avatar;
        Reason = reason;
    }

    public bool IsRegistered { get; }
    public string? Name { get; }
    public byte[]? Avatar { get; }
    public string Reason { get; }

    public static AccountSyncResult Registered(string name, byte[]? avatar, string reason) =>
        new(true, name, avatar, reason);

    public static AccountSyncResult Guest(string? name, byte[]? avatar) =>
        new(false, name, avatar, "Account is not registered.");
}

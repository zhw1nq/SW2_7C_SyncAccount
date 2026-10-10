namespace SyncAccount.Models;

public class PluginConfig
{
    public string ApiBaseUrl { get; set; } = "https://acsync.vhming.com";
    public string ApiKey { get; set; } = "";
    public string UserAgent { get; set; } = "VHMing-API-Sync-Go/1.0";
    public string MockAvatarUrl { get; set; } = "https://acsync.vhming.com/api/sync/00000000000000000/avatar.jpg";
    public string HealthUrl { get; set; } = "https://acsync.vhming.com/health";
    public bool EnableHealthCheck { get; set; } = false;
    public int TimeoutSeconds { get; set; } = 5;
    public string SoundSuccess { get; set; } = "sounds/items/rush_captured_enemy_01.vsnd_c";
    public string SoundFailed { get; set; } = "sounds/music/altare_01/chooseteam.vsnd_c";
    public int GuestKickDelaySeconds { get; set; } = 23;
}

namespace octo_fiesta.Models.Settings;

public class MusicHelperSettings
{
    public bool Enabled { get; set; } = false;
    public string BrowseScope { get; set; } = "synthetic-only";
    public string ResolverUrl { get; set; } = "http://resolver:4588";
    public string StationId { get; set; } = "discovery-001";
    public string GatewayUrl { get; set; } = "http://n8n:5678/webhook/music-requests";
    public string WebhookSecretFile { get; set; } = "/run/secrets/music_webhook_secret";
    public string GhostResponseMode { get; set; } = "subsonic_error";
    public bool DisableScrobbling { get; set; } = true;
    public int StationCacheSeconds { get; set; } = 60;
}

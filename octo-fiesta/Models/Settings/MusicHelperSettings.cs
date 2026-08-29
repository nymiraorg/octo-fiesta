namespace octo_fiesta.Models.Settings;

public class MusicHelperSettings
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// "synthetic-only": browse endpoints return ONLY the lab station; nothing is
    /// relayed to the backing Navidrome. Good for an isolated ghost-only test
    /// provider, but a client (Symfonium) may not treat it as a real library.
    ///
    /// "merge" (recommended for a unified single provider): browse endpoints
    /// proxy Navidrome and splice the lab station's artist/album/songs into the
    /// result, so the client syncs the whole real library plus the station.
    /// </summary>
    public string BrowseScope { get; set; } = "merge";
    public string ResolverUrl { get; set; } = "http://resolver:4588";
    public string StationId { get; set; } = "discovery-001";
    public string GatewayUrl { get; set; } = "http://n8n:5678/webhook/music-requests";
    public string WebhookSecretFile { get; set; } = "/run/secrets/music_webhook_secret";
    public string GhostResponseMode { get; set; } = "subsonic_error";
    public bool DisableScrobbling { get; set; } = true;
    public int StationCacheSeconds { get; set; } = 60;
}

using System.Net.Http.Json;
using System.Xml.Linq;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using octo_fiesta.Models.Domain;
using octo_fiesta.Models.Settings;
using octo_fiesta.Services.Subsonic;

namespace octo_fiesta.Services.MusicHelper;

public class MusicHelperService
{
    private static readonly XNamespace SubsonicNamespace = "http://subsonic.org/restapi";
    private const string SubsonicVersion = "1.16.1";
    public const string Provider = "musichelper";
    public const string ArtistId = "ext-musichelper-artist-listenbrainz-radio";
    public const string AlbumId = "ext-musichelper-album-discovery-001";
    public const string PlaceholderCoverArtId = "musichelper-placeholder";

    private readonly HttpClient _httpClient;
    private readonly MusicHelperSettings _settings;
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly ILogger<MusicHelperService> _logger;
    private StationResponse? _cachedStation;
    private DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;

    public MusicHelperService(
        IHttpClientFactory httpClientFactory,
        IOptions<MusicHelperSettings> settings,
        SubsonicResponseBuilder responseBuilder,
        ILogger<MusicHelperService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _responseBuilder = responseBuilder;
        _logger = logger;
    }

    public bool Enabled => _settings.Enabled;
    public bool SyntheticOnly => _settings.Enabled && string.Equals(_settings.BrowseScope, "synthetic-only", StringComparison.OrdinalIgnoreCase);
    public bool DisableScrobbling => _settings.Enabled && _settings.DisableScrobbling;

    public bool IsMusicHelperSongId(string id) =>
        id.StartsWith("ext-musichelper-song-", StringComparison.OrdinalIgnoreCase);

    public string? RecordingMbidFromSongId(string id)
    {
        if (!IsMusicHelperSongId(id))
        {
            return null;
        }
        return id["ext-musichelper-song-".Length..].Trim().ToLowerInvariant();
    }

    public bool IsMusicHelperArtistId(string id) =>
        string.Equals(id, ArtistId, StringComparison.OrdinalIgnoreCase);

    public bool IsMusicHelperAlbumId(string id) =>
        string.Equals(id, AlbumId, StringComparison.OrdinalIgnoreCase);

    public async Task<StationResponse> GetStationAsync(CancellationToken cancellationToken)
    {
        if (_cachedStation is not null && DateTimeOffset.UtcNow < _cachedUntil)
        {
            return _cachedStation;
        }

        var url = $"{_settings.ResolverUrl.TrimEnd('/')}/music-helper/stations/{Uri.EscapeDataString(_settings.StationId)}";
        var station = await _httpClient.GetFromJsonAsync<StationResponse>(url, cancellationToken)
            ?? new StationResponse();
        for (var index = 0; index < station.Tracks.Count; index++)
        {
            station.Tracks[index].Ordinal = index + 1;
        }
        _cachedStation = station;
        _cachedUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, _settings.StationCacheSeconds));
        return station;
    }

    public async Task<Song?> GetSongAsync(string id, CancellationToken cancellationToken)
    {
        var mbid = RecordingMbidFromSongId(id);
        if (string.IsNullOrWhiteSpace(mbid))
        {
            return null;
        }

        var station = await GetStationAsync(cancellationToken);
        var track = station.Tracks.FirstOrDefault(t => string.Equals(t.RecordingMbid, mbid, StringComparison.OrdinalIgnoreCase));
        return track is null ? null : ToSong(station, track);
    }

    public async Task<Album> GetAlbumAsync(CancellationToken cancellationToken)
    {
        var station = await GetStationAsync(cancellationToken);
        return new Album
        {
            Id = AlbumId,
            Title = station.Album,
            Artist = station.Artist,
            ArtistId = ArtistId,
            SongCount = station.Tracks.Count,
            CoverArtUrl = PlaceholderCoverArtId,
            IsLocal = false,
            ExternalProvider = Provider,
            ExternalId = station.StationId,
            Songs = station.Tracks.Select(track => ToSong(station, track)).ToList()
        };
    }

    public async Task<Artist> GetArtistAsync(CancellationToken cancellationToken)
    {
        var station = await GetStationAsync(cancellationToken);
        return new Artist
        {
            Id = ArtistId,
            Name = station.Artist,
            AlbumCount = 1,
            ImageUrl = PlaceholderCoverArtId,
            IsLocal = false,
            ExternalProvider = Provider,
            ExternalId = "listenbrainz-radio"
        };
    }

    public async Task<IActionResult> SyntheticBrowseResponseAsync(string endpoint, string format, CancellationToken cancellationToken)
    {
        var artist = await GetArtistAsync(cancellationToken);
        var album = await GetAlbumAsync(cancellationToken);
        var normalized = endpoint.Trim('/').ToLowerInvariant();

        if (format == "json")
        {
            object payload = normalized switch
            {
                "rest/ping" or "rest/ping.view" => new { status = "ok", version = SubsonicVersion },
                "rest/getmusicfolders" or "rest/getmusicfolders.view" => new { status = "ok", version = "1.16.1", musicFolders = new { musicFolder = new[] { new { id = "musichelper-lab", name = "MusicHelper Lab" } } } },
                "rest/getartists" or "rest/getartists.view" or "rest/getindexes" or "rest/getindexes.view" => new { status = "ok", version = "1.16.1", artists = new { index = new[] { new { name = "L", artist = new[] { _responseBuilder.ConvertArtistToJson(artist) } } } } },
                "rest/getalbumlist" or "rest/getalbumlist.view" or "rest/getalbumlist2" or "rest/getalbumlist2.view" => new { status = "ok", version = "1.16.1", albumList = new { album = new[] { _responseBuilder.ConvertAlbumToJson(album) } }, albumList2 = new { album = new[] { _responseBuilder.ConvertAlbumToJson(album) } } },
                _ => new { status = "ok", version = "1.16.1" }
            };
            return _responseBuilder.CreateJsonResponse(payload);
        }

        return normalized switch
        {
            "rest/ping" or "rest/ping.view" => XmlRoot(),
            "rest/getmusicfolders" or "rest/getmusicfolders.view" => XmlRoot(
                new XElement(SubsonicNamespace + "musicFolders",
                    new XElement(SubsonicNamespace + "musicFolder",
                        new XAttribute("id", "musichelper-lab"),
                        new XAttribute("name", "MusicHelper Lab")))),
            "rest/getartists" or "rest/getartists.view" => XmlRoot(
                new XElement(SubsonicNamespace + "artists",
                    new XElement(SubsonicNamespace + "index",
                        new XAttribute("name", "L"),
                        _responseBuilder.ConvertArtistToXml(artist, SubsonicNamespace)))),
            "rest/getindexes" or "rest/getindexes.view" => XmlRoot(
                new XElement(SubsonicNamespace + "indexes",
                    new XElement(SubsonicNamespace + "index",
                        new XAttribute("name", "L"),
                        _responseBuilder.ConvertArtistToXml(artist, SubsonicNamespace)))),
            "rest/getalbumlist" or "rest/getalbumlist.view" => XmlRoot(
                new XElement(SubsonicNamespace + "albumList",
                    _responseBuilder.ConvertAlbumToXml(album, SubsonicNamespace))),
            "rest/getalbumlist2" or "rest/getalbumlist2.view" => XmlRoot(
                new XElement(SubsonicNamespace + "albumList2",
                    _responseBuilder.ConvertAlbumToXml(album, SubsonicNamespace))),
            _ => XmlRoot()
        };
    }

    public async Task<IActionResult> Search3ResponseAsync(string query, string format, CancellationToken cancellationToken)
    {
        var artist = await GetArtistAsync(cancellationToken);
        var album = await GetAlbumAsync(cancellationToken);
        var cleanQuery = (query ?? string.Empty).Trim();
        var songs = album.Songs
            .Where(song => string.IsNullOrWhiteSpace(cleanQuery)
                || song.Title.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase)
                || song.Artist.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase)
                || album.Title.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase))
            .Select(song => _responseBuilder.ConvertSongToJson(song))
            .ToList();
        var includeStation = string.IsNullOrWhiteSpace(cleanQuery)
            || artist.Name.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase)
            || album.Title.Contains(cleanQuery, StringComparison.OrdinalIgnoreCase)
            || songs.Count > 0;

        if (format == "json")
        {
            return _responseBuilder.CreateJsonResponse(new
            {
                status = "ok",
                version = SubsonicVersion,
                searchResult3 = new
                {
                    song = songs,
                    album = includeStation ? new[] { _responseBuilder.ConvertAlbumToJson(album) } : Array.Empty<object>(),
                    artist = includeStation ? new[] { _responseBuilder.ConvertArtistToJson(artist) } : Array.Empty<object>()
                }
            });
        }

        var searchResult = new XElement(SubsonicNamespace + "searchResult3");
        if (includeStation)
        {
            searchResult.Add(_responseBuilder.ConvertArtistToXml(artist, SubsonicNamespace));
            searchResult.Add(_responseBuilder.ConvertAlbumToXml(album, SubsonicNamespace));
        }
        foreach (var song in album.Songs.Where(song => songs.Any(row => string.Equals(Convert.ToString(row["id"]), $"ext-musichelper-song-{song.ExternalId}", StringComparison.OrdinalIgnoreCase))))
        {
            searchResult.Add(_responseBuilder.ConvertSongToXml(song, SubsonicNamespace, AlbumId));
        }
        return XmlRoot(searchResult);
    }

    public async Task<string?> LocalNavidromeSongIdAsync(string id, CancellationToken cancellationToken)
    {
        var mbid = RecordingMbidFromSongId(id);
        if (string.IsNullOrWhiteSpace(mbid))
        {
            return null;
        }
        var station = await GetStationAsync(cancellationToken);
        return station.Tracks
            .FirstOrDefault(t => string.Equals(t.RecordingMbid, mbid, StringComparison.OrdinalIgnoreCase))
            ?.NavidromeSongId;
    }

    public async Task<string?> RequestHydrationAsync(string id, CancellationToken cancellationToken)
    {
        var song = await GetSongAsync(id, cancellationToken);
        if (song?.ExternalId is null)
        {
            return null;
        }

        var secret = await ReadSecretAsync(_settings.WebhookSecretFile, cancellationToken);
        using var request = new HttpRequestMessage(HttpMethod.Post, _settings.GatewayUrl)
        {
            Content = JsonContent.Create(new
            {
                source = "webhook",
                requester = "musichelper-lab",
                rawInput = $"ghost stream {song.ExternalId}",
                intent = "track",
                recordingMbid = song.ExternalId,
                requestType = "ghost_hydration",
                options = new { dryRun = false, executionMode = "queue_only", claimAndRun = false, claimLimit = 10 }
            })
        };
        if (!string.IsNullOrWhiteSpace(secret))
        {
            request.Headers.TryAddWithoutValidation("X-Music-Acquisition-Secret", secret);
        }

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var payload = await response.Content.ReadFromJsonAsync<MusicRequestResponse>(cancellationToken: cancellationToken);
            return payload?.RequestId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ghost hydration request failed for {SongId}", id);
            return null;
        }
    }

    public IActionResult GhostStreamResponse(string format, string? requestId)
    {
        var message = string.IsNullOrWhiteSpace(requestId)
            ? "Track hydration has been requested."
            : $"Track hydration has been requested: {requestId}";
        var mode = (_settings.GhostResponseMode ?? "subsonic_error").Trim().Replace("-", "_").ToLowerInvariant();
        return mode switch
        {
            "http_503" => new ObjectResult(new { ok = false, reason = "hydrating", requestId }) { StatusCode = StatusCodes.Status503ServiceUnavailable },
            "placeholder_audio" => new FileContentResult(PlaceholderWav(), "audio/wav") { EnableRangeProcessing = true },
            _ => _responseBuilder.CreateError(format, 70, message)
        };
    }

    public FileContentResult PlaceholderCoverArt()
    {
        var bytes = Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAH/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAEFAqf/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/ASP/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/ASP/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAY/Ar//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/IV//2gAMAwEAAgADAAAAEP/EFBQRAQAAAAAAAAAAAAAAAAAAABD/2gAIAQMBAT8QH//EFBQRAQAAAAAAAAAAAAAAAAAAABD/2gAIAQIBAT8QH//EFBABAQAAAAAAAAAAAAAAAAAAABD/2gAIAQEAAT8QH//Z");
        return new FileContentResult(bytes, "image/jpeg");
    }

    private static Song ToSong(StationResponse station, StationTrack track)
    {
        return new Song
        {
            Title = track.Title,
            Artist = track.Artist,
            ArtistId = ArtistId,
            Album = station.Album,
            AlbumId = AlbumId,
            Duration = track.DurationSeconds,
            Track = track.Ordinal,
            DiscNumber = 1,
            CoverArtUrl = PlaceholderCoverArtId,
            IsLocal = track.Availability == "local",
            ExternalProvider = Provider,
            ExternalId = track.RecordingMbid,
            Artists = new List<Artist> { new() { Id = ArtistId, Name = track.Artist } }
        };
    }

    private static async Task<string?> ReadSecretAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }
        var value = await File.ReadAllTextAsync(path, cancellationToken);
        return value.Trim();
    }

    private static byte[] PlaceholderWav()
    {
        return Convert.FromBase64String("UklGRiQAAABXQVZFZm10IBAAAAABAAEAQB8AAEAfAAABAAgAZGF0YQAAAAA=");
    }

    private static ContentResult XmlRoot(params object[] content)
    {
        var doc = new XDocument(
            new XElement(SubsonicNamespace + "subsonic-response",
                new XAttribute("status", "ok"),
                new XAttribute("version", SubsonicVersion),
                content));
        return new ContentResult { Content = doc.ToString(), ContentType = "application/xml; charset=utf-8" };
    }

    public sealed class StationResponse
    {
        [JsonPropertyName("stationId")]
        public string StationId { get; set; } = "discovery-001";
        [JsonPropertyName("artist")]
        public string Artist { get; set; } = "ListenBrainz Radio";
        [JsonPropertyName("album")]
        public string Album { get; set; } = "Global Radio - Discovery 001";
        [JsonPropertyName("tracks")]
        public List<StationTrack> Tracks { get; set; } = new();
    }

    public sealed class StationTrack
    {
        [JsonPropertyName("recordingMbid")]
        public string RecordingMbid { get; set; } = "";
        [JsonPropertyName("artist")]
        public string Artist { get; set; } = "";
        [JsonPropertyName("title")]
        public string Title { get; set; } = "";
        [JsonPropertyName("durationSeconds")]
        public int DurationSeconds { get; set; }
        [JsonPropertyName("availability")]
        public string Availability { get; set; } = "ghost";
        [JsonPropertyName("navidromeSongId")]
        public string? NavidromeSongId { get; set; }
        [JsonIgnore]
        public int Ordinal { get; set; }
    }

    private sealed class MusicRequestResponse
    {
        [JsonPropertyName("requestId")]
        public string? RequestId { get; set; }
    }
}

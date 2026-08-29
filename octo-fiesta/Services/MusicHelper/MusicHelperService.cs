using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    // Playlist surface: Symfonium fetches remote playlist tracks live (getPlaylist)
    // rather than persisting them via library sync, so ghosts work here.
    public const string PlaylistIdPrefix = "ext-musichelper-playlist-";
    public string StationPlaylistId => $"{PlaylistIdPrefix}{_settings.StationId}";
    public bool IsMusicHelperPlaylistId(string id) =>
        id.StartsWith(PlaylistIdPrefix, StringComparison.OrdinalIgnoreCase);

    private readonly HttpClient _httpClient;
    private readonly MusicHelperSettings _settings;
    private readonly SubsonicResponseBuilder _responseBuilder;
    private readonly SubsonicProxyService _proxyService;
    private readonly ILogger<MusicHelperService> _logger;
    private StationResponse? _cachedStation;
    private DateTimeOffset _cachedUntil = DateTimeOffset.MinValue;

    public MusicHelperService(
        IHttpClientFactory httpClientFactory,
        IOptions<MusicHelperSettings> settings,
        SubsonicResponseBuilder responseBuilder,
        SubsonicProxyService proxyService,
        ILogger<MusicHelperService> logger)
    {
        _httpClient = httpClientFactory.CreateClient();
        _settings = settings.Value;
        _responseBuilder = responseBuilder;
        _proxyService = proxyService;
        _logger = logger;
    }

    public bool Enabled => _settings.Enabled;
    public bool SyntheticOnly => _settings.Enabled && string.Equals(_settings.BrowseScope, "synthetic-only", StringComparison.OrdinalIgnoreCase);
    /// <summary>Merge mode: proxy Navidrome for browse and splice the station in.</summary>
    public bool Merge => _settings.Enabled && !SyntheticOnly;
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

    private static string EndpointName(string endpoint)
    {
        var normalized = endpoint.Trim('/').ToLowerInvariant();
        if (normalized.StartsWith("rest/")) normalized = normalized["rest/".Length..];
        if (normalized.EndsWith(".view")) normalized = normalized[..^".view".Length];
        return normalized;
    }

    /// <summary>
    /// A valid, minimal albumInfo / artistInfo(2) response for a lab-station id.
    /// The backing Navidrome has no such id, so proxying would return error 70
    /// and cause the client to drop the whole album (and its ghost songs).
    /// </summary>
    public IActionResult EmptyInfoResponse(string element, string format)
    {
        // element is one of: "albumInfo", "artistInfo", "artistInfo2"
        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var body = new JsonObject
            {
                ["subsonic-response"] = new JsonObject
                {
                    ["status"] = "ok",
                    ["version"] = SubsonicVersion,
                    [element] = new JsonObject(),
                },
            };
            return new ContentResult
            {
                Content = body.ToJsonString(),
                ContentType = "application/json; charset=utf-8",
                StatusCode = 200,
            };
        }
        return XmlRoot(new XElement(SubsonicNamespace + element));
    }

    // Navidrome's Subsonic routes are case-sensitive camelCase and want ".view".
    private static readonly Dictionary<string, string> CanonicalMethod = new(StringComparer.OrdinalIgnoreCase)
    {
        ["getartists"] = "getArtists",
        ["getindexes"] = "getIndexes",
        ["getalbumlist"] = "getAlbumList",
        ["getalbumlist2"] = "getAlbumList2",
        ["getmusicfolders"] = "getMusicFolders",
    };

    /// <summary>
    /// Handle a browse endpoint. In synthetic-only mode the station fully
    /// replaces the response. In merge mode the backing Navidrome response is
    /// proxied and the station's artist/album/songs are spliced in so the client
    /// syncs the whole real library plus the lab station.
    /// </summary>
    public async Task<IActionResult> SyntheticBrowseResponseAsync(string endpoint, string format, CancellationToken cancellationToken)
    {
        var name = EndpointName(endpoint);

        if (Merge)
        {
            return await MergeBrowseResponseAsync(name, format, cancellationToken);
        }

        // ---- synthetic-only ----
        var artist = await GetArtistAsync(cancellationToken);
        var album = await GetAlbumAsync(cancellationToken);

        if (format == "json")
        {
            object payload = name switch
            {
                "ping" => new { status = "ok", version = SubsonicVersion },
                "getmusicfolders" => new { status = "ok", version = "1.16.1", musicFolders = new { musicFolder = new[] { new { id = "musichelper-lab", name = "MusicHelper Lab" } } } },
                "getartists" or "getindexes" => new { status = "ok", version = "1.16.1", artists = new { index = new[] { new { name = "L", artist = new[] { _responseBuilder.ConvertArtistToJson(artist) } } } } },
                "getalbumlist" or "getalbumlist2" => new { status = "ok", version = "1.16.1", albumList = new { album = new[] { _responseBuilder.ConvertAlbumToJson(album) } }, albumList2 = new { album = new[] { _responseBuilder.ConvertAlbumToJson(album) } } },
                _ => new { status = "ok", version = "1.16.1" }
            };
            return _responseBuilder.CreateJsonResponse(payload);
        }

        return name switch
        {
            "ping" => XmlRoot(),
            "getmusicfolders" => XmlRoot(
                new XElement(SubsonicNamespace + "musicFolders",
                    new XElement(SubsonicNamespace + "musicFolder",
                        new XAttribute("id", "musichelper-lab"),
                        new XAttribute("name", "MusicHelper Lab")))),
            "getartists" => XmlRoot(
                new XElement(SubsonicNamespace + "artists",
                    new XElement(SubsonicNamespace + "index",
                        new XAttribute("name", "L"),
                        _responseBuilder.ConvertArtistToXml(artist, SubsonicNamespace)))),
            "getindexes" => XmlRoot(
                new XElement(SubsonicNamespace + "indexes",
                    new XElement(SubsonicNamespace + "index",
                        new XAttribute("name", "L"),
                        _responseBuilder.ConvertArtistToXml(artist, SubsonicNamespace)))),
            "getalbumlist" => XmlRoot(
                new XElement(SubsonicNamespace + "albumList",
                    _responseBuilder.ConvertAlbumToXml(album, SubsonicNamespace))),
            "getalbumlist2" => XmlRoot(
                new XElement(SubsonicNamespace + "albumList2",
                    _responseBuilder.ConvertAlbumToXml(album, SubsonicNamespace))),
            _ => XmlRoot()
        };
    }

    // ---- playlist surface -------------------------------------------------
    // Symfonium fetches remote playlist tracks live via getPlaylist, so ghost
    // tracks can be exposed here even though they won't survive library sync.

    private JsonObject StationPlaylistSummary(int songCount, int totalDurationSeconds) => new()
    {
        ["id"] = StationPlaylistId,
        ["name"] = "MusicHelper Lab — Discovery",
        ["comment"] = "Not-yet-acquired recommendations. Play a track to hydrate it.",
        ["owner"] = "musichelper",
        ["public"] = true,
        ["songCount"] = songCount,
        ["duration"] = totalDurationSeconds,
        ["created"] = DateTime.UtcNow.ToString("o"),
        ["changed"] = DateTime.UtcNow.ToString("o"),
        ["coverArt"] = PlaceholderCoverArtId,
    };

    /// <summary>getPlaylists: proxy Navidrome and append the synthetic station playlist.</summary>
    public async Task<IActionResult> GetPlaylistsMergeAsync(CancellationToken cancellationToken)
    {
        var proxyParams = new Dictionary<string, string>(_capturedParameters, StringComparer.OrdinalIgnoreCase) { ["f"] = "json" };
        JsonObject? proxied = null;
        try
        {
            var (body, _) = await _proxyService.RelayAsync("rest/getPlaylists.view", proxyParams);
            if (JsonNode.Parse(body) is JsonObject r && r["subsonic-response"] is JsonObject sr)
                proxied = sr.DeepClone() as JsonObject;
        }
        catch (Exception ex) { _logger.LogWarning(ex, "MusicHelper merge: getPlaylists proxy failed"); }

        var response = proxied ?? new JsonObject { ["status"] = "ok", ["version"] = SubsonicVersion };

        int count = 0, dur = 0;
        try
        {
            var ghosts = await GhostSongsAsync(cancellationToken);
            count = ghosts.Count;
            dur = ghosts.Sum(s => s.Duration ?? 0);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "MusicHelper: station fetch for getPlaylists failed"); }

        var container = EnsureObject(response, "playlists");
        EnsureArray(container, "playlist").Add(StationPlaylistSummary(count, dur));

        return JsonResult(response);
    }

    /// <summary>getPlaylist?id=&lt;station&gt;: return the station's ghost songs as a playlist.</summary>
    public async Task<IActionResult> GetStationPlaylistAsync(string format, CancellationToken cancellationToken)
    {
        var ghosts = await GhostSongsAsync(cancellationToken);
        var summary = StationPlaylistSummary(ghosts.Count, ghosts.Sum(s => s.Duration ?? 0));

        if (format.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            var entry = summary.DeepClone()!.AsObject();
            var arr = new JsonArray();
            foreach (var s in ghosts)
                arr.Add(JsonSerializer.SerializeToNode(_responseBuilder.ConvertSongToJson(s)));
            entry["entry"] = arr;
            return JsonResult(new JsonObject { ["status"] = "ok", ["version"] = SubsonicVersion, ["playlist"] = entry });
        }

        var xml = new XElement(SubsonicNamespace + "playlist",
            new XAttribute("id", StationPlaylistId),
            new XAttribute("name", "MusicHelper Lab — Discovery"),
            new XAttribute("songCount", ghosts.Count),
            new XAttribute("duration", ghosts.Sum(s => s.Duration ?? 0)),
            new XAttribute("public", "true"),
            new XAttribute("owner", "musichelper"),
            new XAttribute("created", DateTime.UtcNow.ToString("o")),
            new XAttribute("coverArt", PlaceholderCoverArtId));
        foreach (var s in ghosts)
            xml.Add(_responseBuilder.ConvertSongToXml(s, SubsonicNamespace, StationPlaylistId));
        return XmlRoot(xml);
    }

    /// <summary>The station's non-local tracks, as Song objects.</summary>
    private async Task<List<Song>> GhostSongsAsync(CancellationToken cancellationToken)
    {
        var station = await GetStationAsync(cancellationToken);
        return station.Tracks
            .Where(t => !string.Equals(t.Availability, "local", StringComparison.OrdinalIgnoreCase))
            .Select(t => ToSong(station, t))
            .ToList();
    }

    private IActionResult JsonResult(JsonObject subsonicResponse) => new ContentResult
    {
        Content = new JsonObject { ["subsonic-response"] = subsonicResponse }.ToJsonString(),
        ContentType = "application/json; charset=utf-8",
        StatusCode = 200,
    };

    /// <summary>
    /// Proxy Navidrome for a browse endpoint (JSON) and splice the lab station
    /// into the result. XML falls back to a proxy-only relay so we never break
    /// clients that ask for XML (Symfonium uses JSON).
    /// </summary>
    private async Task<IActionResult> MergeBrowseResponseAsync(string name, string format, CancellationToken cancellationToken)
    {
        var proxyParams = new Dictionary<string, string>(_capturedParameters, StringComparer.OrdinalIgnoreCase)
        {
            ["f"] = "json",
        };

        var canonical = CanonicalMethod.TryGetValue(name, out var m) ? m : name;
        JsonObject? proxied = null;
        try
        {
            var (body, _) = await _proxyService.RelayAsync($"rest/{canonical}.view", proxyParams);
            if (JsonNode.Parse(body) is JsonObject root && root["subsonic-response"] is JsonObject sr)
            {
                // Detach from the parsed root so we can re-parent it.
                proxied = sr.DeepClone() as JsonObject;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MusicHelper merge: proxy of rest/{Endpoint} failed; returning station-only", name);
        }

        var response = proxied ?? new JsonObject
        {
            ["status"] = "ok",
            ["version"] = SubsonicVersion,
        };

        try
        {
            switch (name)
            {
                case "getartists":
                case "getindexes":
                {
                    var artist = await GetArtistAsync(cancellationToken);
                    var artistsKey = name == "getindexes" ? "indexes" : "artists";
                    var container = EnsureObject(response, artistsKey);
                    var index = EnsureArray(container, "index");
                    index.Add(new JsonObject
                    {
                        ["name"] = "☆",
                        ["artist"] = new JsonArray { ToJsonNode(_responseBuilder.ConvertArtistToJson(artist)) },
                    });
                    break;
                }
                case "getalbumlist":
                case "getalbumlist2":
                {
                    var album = await GetAlbumAsync(cancellationToken);
                    var albumNode = ToJsonNode(_responseBuilder.ConvertAlbumToJson(album));
                    foreach (var key in new[] { "albumList", "albumList2" })
                    {
                        if (response[key] is null) continue;
                        var container = EnsureObject(response, key);
                        EnsureArray(container, "album").Insert(0, albumNode!.DeepClone());
                    }
                    // if Navidrome returned neither (unlikely), add albumList2
                    if (response["albumList"] is null && response["albumList2"] is null)
                    {
                        var container = EnsureObject(response, name == "getalbumlist" ? "albumList" : "albumList2");
                        EnsureArray(container, "album").Add(albumNode);
                    }
                    break;
                }
                case "getmusicfolders":
                {
                    var container = EnsureObject(response, "musicFolders");
                    EnsureArray(container, "musicFolder").Add(new JsonObject
                    {
                        ["id"] = "musichelper-lab",
                        ["name"] = "MusicHelper Lab",
                    });
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MusicHelper merge: splice for rest/{Endpoint} failed", name);
        }

        var wrapper = new JsonObject { ["subsonic-response"] = response };
        return new ContentResult
        {
            Content = wrapper.ToJsonString(),
            ContentType = "application/json; charset=utf-8",
            StatusCode = 200,
        };
    }

    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;
        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    private static JsonArray EnsureArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray existing) return existing;
        // Subsonic JSON collapses single-element arrays to objects; normalize.
        if (parent[key] is JsonObject single)
        {
            var arr = new JsonArray { single.DeepClone() };
            parent[key] = arr;
            return arr;
        }
        var created = new JsonArray();
        parent[key] = created;
        return created;
    }

    private static JsonNode? ToJsonNode(object value) =>
        JsonSerializer.SerializeToNode(value);

    // The full parameter set of the current request (credentials + query args
    // like type/size/offset), captured by the controller so a merge proxy call
    // can forward everything Navidrome needs.
    private Dictionary<string, string> _capturedParameters = new(StringComparer.OrdinalIgnoreCase);
    public void CaptureCredentials(Dictionary<string, string> parameters)
    {
        _capturedParameters = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
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

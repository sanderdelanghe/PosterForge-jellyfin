using System.Globalization;
using System.Net;
using System.Text.Json;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.PosterForge;

/// <summary>
/// Offers up to 5 logos x 5 backgrounds as selectable "remote images". The images are
/// rendered on demand by <see cref="PosterController"/>; this class only builds the list.
/// </summary>
public sealed class PosterForgeImageProvider(IHttpClientFactory httpFactory, ILocalizationManager localization) : IRemoteImageProvider
{
    private const int Count = 5;
    private const string TmdbImages = "https://image.tmdb.org/t/p/w780";

    private sealed record Art(string Url, string? Lang);

    public string Name => "PosterForge";

    public bool Supports(BaseItem item) => item is Movie or Series or Season;

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item) => [ImageType.Primary];

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var cfg = Plugin.Instance!.Configuration;
        var season = item as Season;
        BaseItem? source = season is null ? item : season.Series;
        var tmdbId = source?.GetProviderId(MetadataProvider.Tmdb);
        if (source is null || tmdbId is null || string.IsNullOrEmpty(cfg.TmdbApiKey))
        {
            return [];
        }

        var isMovie = source is Movie;
        var http = httpFactory.CreateClient(NamedClient.Default);
        string TmdbUrl(string path) =>
            $"https://api.themoviedb.org/3/{path}/images?api_key={cfg.TmdbApiKey}&include_image_language=en,nl,null";

        // TMDB: posters textless -> en -> nl, logos en -> nl -> textless (same tiers as the web tool).
        var tmdb = await GetJson(http, TmdbUrl($"{(isMovie ? "movie" : "tv")}/{tmdbId}"), cancellationToken);
        var posters = Pick(Read(tmdb, "posters", TmdbImages), false, null, "en", "nl");
        var logos = Pick(Read(tmdb, "logos", TmdbImages), false, "en", "nl", null);

        // Fanart.tv: extra posters, and logos only when TMDB has none.
        var fanartId = isMovie ? tmdbId : source.GetProviderId(MetadataProvider.Tvdb);
        JsonElement? fanart = null;
        if (!string.IsNullOrEmpty(cfg.FanartApiKey) && fanartId is not null)
        {
            fanart = await GetJson(http, $"https://webservice.fanart.tv/v3/{(isMovie ? "movies" : "tv")}/{fanartId}?api_key={cfg.FanartApiKey}", cancellationToken);
        }

        posters = posters.Concat(Read(fanart, isMovie ? "movieposter" : "tvposter", "", "url", "lang")).Take(Count).ToList();
        if (logos.Count == 0)
        {
            string[] keys = isMovie
                ? ["hdmovielogo", "movielogo"]
                : ["hdtvlogo", "clearlogo"];
            logos = Pick(keys.SelectMany(k => Read(fanart, k, "", "url", "lang")), true, "en", "nl");
        }

        // Seasons: up to 5 textless season posters, then up to 5 textless series posters.
        if (season?.IndexNumber is { } number)
        {
            var seasonImages = await GetJson(http, TmdbUrl($"tv/{tmdbId}/season/{number}"), cancellationToken);
            var textless = Read(seasonImages, "posters", TmdbImages).Where(a => a.Lang is null).Take(Count)
                .Concat(Read(tmdb, "posters", TmdbImages).Where(a => a.Lang is null).Take(Count))
                .ToList();
            if (textless.Count > 0)
            {
                posters = textless;
            }
        }

        // Seasons print their own title; titles without any logo fall back to the item name.
        var seasonText = season is null ? null : SeasonText(season, source);
        var serverUrl = cfg.ServerUrl.TrimEnd('/');
        var basePath = new Uri(serverUrl).AbsolutePath.TrimEnd('/');

        var result = new List<RemoteImageInfo>();
        foreach (var poster in posters)
        {
            foreach (var logo in logos.Take(Count).Select(l => (Art?)l).DefaultIfEmpty(null))
            {
                var text = seasonText ?? (logo is null ? item.Name : null);
                var query = "/PosterForge/Render?bg=" + WebUtility.UrlEncode(poster.Url)
                    + (logo is null ? "" : "&logo=" + WebUtility.UrlEncode(logo.Url))
                    + (text is null ? "" : "&text=" + WebUtility.UrlEncode(text))
                    + (season is null ? "" : "&season=true");

                result.Add(new RemoteImageInfo
                {
                    ProviderName = Name,
                    Type = ImageType.Primary,
                    Width = 500,
                    Height = 750,
                    Url = serverUrl + query,          // fetched by the server itself
                    ThumbnailUrl = basePath + query   // fetched by the browser (same origin)
                });
            }
        }

        return result;
    }

    public Task<HttpResponseMessage> GetImageResponse(string url, CancellationToken cancellationToken) =>
        httpFactory.CreateClient(NamedClient.Default).GetAsync(url, cancellationToken);

    /// <summary>"Season 3" / "Seizoen 3" ... in the series' metadata language. Specials keep Jellyfin's name.</summary>
    private string SeasonText(Season season, BaseItem series)
    {
        if (season.IndexNumber is not { } number || number == 0)
        {
            return season.Name;
        }

        var language = series.GetPreferredMetadataLanguage();
        var pattern = string.IsNullOrEmpty(language)
            ? localization.GetLocalizedString("NameSeasonNumber")
            : localization.GetLocalizedString("NameSeasonNumber", language);
        return string.Format(CultureInfo.InvariantCulture, pattern, number);
    }

    /// <summary>Returns the first language tier that has matches (null = textless).</summary>
    private static List<Art> Pick(IEnumerable<Art> items, bool fallbackToAll, params string?[] langs)
    {
        var list = items.ToList();
        foreach (var lang in langs)
        {
            if (list.Where(a => a.Lang == lang).ToList() is { Count: > 0 } match)
            {
                return match;
            }
        }

        return fallbackToAll ? list : [];
    }

    private static IEnumerable<Art> Read(JsonElement? root, string property, string baseUrl, string pathKey = "file_path", string langKey = "iso_639_1")
    {
        if (root is not { } r || !r.TryGetProperty(property, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var e in array.EnumerateArray())
        {
            if (!e.TryGetProperty(pathKey, out var pathProp) || pathProp.GetString() is not { Length: > 0 } path)
            {
                continue;
            }

            // TMDB serves rasterized PNGs on /w780/ when requesting .png instead of .svg
            if (baseUrl.Length > 0 && path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                path = Path.ChangeExtension(path, ".png");
            }

            var lang = e.TryGetProperty(langKey, out var l) ? l.GetString() : null;
            if (string.IsNullOrEmpty(lang) || lang == "00")
            {
                lang = null;
            }

            yield return new Art(baseUrl + path, lang);
        }
    }

    private static async Task<JsonElement?> GetJson(HttpClient http, string url, CancellationToken ct)
    {
        try
        {
            using var response = await http.GetAsync(url, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            return doc.RootElement.Clone();
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }
}
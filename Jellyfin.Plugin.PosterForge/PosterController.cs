using MediaBrowser.Common.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using SkiaSharp;

namespace Jellyfin.Plugin.PosterForge;

/// <summary>Renders a poster on demand. Anonymous so the browser can load thumbnails; only TMDB/Fanart.tv sources are accepted.</summary>
[ApiController]
[AllowAnonymous]
[Route("PosterForge")]
public class PosterController(IHttpClientFactory httpFactory, IMemoryCache cache) : ControllerBase
{
    private static readonly string[] AllowedSources = ["https://image.tmdb.org/t/p/", "https://assets.fanart.tv/"];

    [HttpGet("Render")]
    public async Task<IActionResult> Render(
        [FromQuery] string bg,
        [FromQuery] string? logo,
        [FromQuery] string? text,
        [FromQuery] bool season,
        CancellationToken cancellationToken)
    {
        if (!IsAllowed(bg) || (!string.IsNullOrEmpty(logo) && !IsAllowed(logo)))
        {
            return BadRequest();
        }

        using var background = await Load(bg, cancellationToken);
        using var logoImage = string.IsNullOrEmpty(logo) ? null : await Load(logo, cancellationToken);
        if (background is null)
        {
            return NotFound();
        }

        return File(PosterRenderer.Render(background, logoImage, text, season, Plugin.Instance!.Configuration), "image/jpeg");
    }

    private static bool IsAllowed(string url) => AllowedSources.Any(p => url.StartsWith(p, StringComparison.Ordinal));

    private async Task<SKImage?> Load(string url, CancellationToken ct)
    {
        if (!cache.TryGetValue("posterforge:" + url, out byte[]? bytes))
        {
            try
            {
                bytes = await httpFactory.CreateClient(NamedClient.Default).GetByteArrayAsync(url, ct);
                cache.Set("posterforge:" + url, bytes, TimeSpan.FromMinutes(10));
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        return bytes is null ? null : SKImage.FromEncodedData(bytes);
    }
}
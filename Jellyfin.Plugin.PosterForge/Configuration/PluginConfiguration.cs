using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.PosterForge.Configuration;

public class PluginConfiguration : BasePluginConfiguration
{
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Optional. Adds Fanart.tv posters and acts as logo fallback.</summary>
    public string FanartApiKey { get; set; } = string.Empty;

    /// <summary>How Jellyfin reaches itself (include the base path, if you use one).</summary>
    public string ServerUrl { get; set; } = "http://127.0.0.1:8096";

    public int BlurPx { get; set; } = 40;

    public int SeasonBlurPx { get; set; } = 60;

    /// <summary>Must be installed on the server; falls back to the default font.</summary>
    public string FontFamily { get; set; } = "DM Sans";
}

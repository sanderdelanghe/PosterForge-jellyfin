using Jellyfin.Plugin.PosterForge.Configuration;
using SkiaSharp;

namespace Jellyfin.Plugin.PosterForge;

/// <summary>Port of the PosterForge web tool's canvas logic.</summary>
internal static class PosterRenderer
{
    private const int W = 500, H = 750;
    private const int BottomMargin = 80;      // title sits this far from the bottom edge
    private const int BoxW = 392, BoxH = 200; // logo is contain-fit into this box
    private const int MaxFontSize = 100, MaxSeasonFontSize = 48, MinFontSize = 16;
    private const int TitleGap = 16;          // space between season text and the logo above it
    private const float LineHeight = 1.08f;
    private const float FrostBlur = 10;       // how blurred the "glass" behind the letters is
    private const byte FrostTint = 100;       // white tint inside the letters (0-255)
    private const float BlurSpread = 0.5f;    // blur fades in from this fraction of the poster height
    private const float ShadeRatio = 0.55f;   // bottom portion covered by the shade
    private const float ShadeAlpha = 0.75f;
    private const float ShadeDarken = 0.5f;
    private const byte MaxShadeChannel = 38;  // prevents bright/white backgrounds from producing a washed-out gray shade

    private static readonly SKSamplingOptions Sampling = new(SKFilterMode.Linear, SKMipmapMode.Linear);

    /// <summary>
    /// Poster: logo (or text if there is no logo) at the bottom.
    /// Season: single-line season text at the bottom, logo above it, stronger blur.
    /// </summary>
    public static byte[] Render(SKImage background, SKImage? logo, string? text, bool season, PluginConfiguration cfg)
    {
        using var surface = SKSurface.Create(new SKImageInfo(W, H));
        var canvas = surface.Canvas;
        canvas.Clear(new SKColor(0x11, 0x11, 0x11));

        DrawBackground(canvas, background, season ? cfg.SeasonBlurPx : cfg.BlurPx);
        DrawShade(canvas, ShadeColor(background));

        float bottom = H - BottomMargin;
        if (!string.IsNullOrWhiteSpace(text))
        {
            bottom = DrawText(surface, text, bottom, cfg.FontFamily, singleLine: season) - (logo is null ? 0 : TitleGap);
        }

        if (logo is not null)
        {
            var scale = Math.Min((float)BoxW / logo.Width, (float)BoxH / logo.Height);
            float dw = logo.Width * scale, dh = logo.Height * scale;
            canvas.DrawImage(logo, SKRect.Create((W - dw) / 2, bottom - dh, dw, dh), Sampling);
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Jpeg, 92);
        return data.ToArray();
    }

    /// <summary>Sharp copy underneath, blurred copy on top masked by a bottom-weighted gradient.</summary>
    private static void DrawBackground(SKCanvas canvas, SKImage image, int blur)
    {
        using (var sharp = CoverPaint(image, W, H, 0))
        {
            canvas.DrawRect(0, 0, W, H, sharp);
        }

        if (blur <= 0)
        {
            return;
        }

        canvas.SaveLayer();
        var pad = blur * 3f; // the clamped shader extends past the canvas, so blurred edges never fade out
        using (var blurred = CoverPaint(image, W, H, blur))
        {
            canvas.DrawRect(-pad, -pad, W + 2 * pad, H + 2 * pad, blurred);
        }

        using var mask = new SKPaint
        {
            BlendMode = SKBlendMode.DstIn,
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, H * (1 - BlurSpread)),
                new SKPoint(0, H),
                new[] { SKColors.Transparent, SKColors.Black },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, 0, W, H, mask);
        canvas.Restore();
    }

    /// <summary>Paint that fills with the image cropped to cover w x h (optionally blurred).</summary>
    private static SKPaint CoverPaint(SKImage image, int w, int h, float blur)
    {
        var scale = Math.Max((float)w / image.Width, (float)h / image.Height);
        var matrix = SKMatrix.CreateScaleTranslation(scale, scale, (w - image.Width * scale) / 2, (h - image.Height * scale) / 2);
        return new SKPaint
        {
            Shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp, Sampling, matrix),
            ImageFilter = blur > 0 ? SKImageFilter.CreateBlur(blur, blur) : null
        };
    }

    /// <summary>Darkened average colour of the part of the artwork the shade covers.</summary>
    private static SKColor ShadeColor(SKImage image)
    {
        const int N = 60;
        using var bitmap = new SKBitmap(N, N);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = CoverPaint(image, N, N, 0))
        {
            canvas.DrawRect(0, 0, N, N, paint);
        }

        long r = 0, g = 0, b = 0, count = 0;
        for (var y = (int)(N * (1 - ShadeRatio)); y < N; y++)
        {
            for (var x = 0; x < N; x++)
            {
                var px = bitmap.GetPixel(x, y);
                r += px.Red; g += px.Green; b += px.Blue; count++;
            }
        }

        var k = 1 - ShadeDarken;
        var sr = (byte)Math.Min(MaxShadeChannel, r / count * k);
        var sg = (byte)Math.Min(MaxShadeChannel, g / count * k);
        var sb = (byte)Math.Min(MaxShadeChannel, b / count * k);
        return new SKColor(sr, sg, sb);
    }

    private static void DrawShade(SKCanvas canvas, SKColor color)
    {
        var top = H * (1 - ShadeRatio);
        using var paint = new SKPaint
        {
            Shader = SKShader.CreateLinearGradient(
                new SKPoint(0, top),
                new SKPoint(0, H),
                new[] { color.WithAlpha(0), color.WithAlpha((byte)(255 * ShadeAlpha)) },
                SKShaderTileMode.Clamp)
        };
        canvas.DrawRect(0, top, W, H - top, paint);
    }

    /// <summary>
    /// Draws frosted-glass text: the artwork behind the letters is blurred and tinted white, with a
    /// soft shadow for contrast on bright backgrounds. Single-line mode (used for seasons) scales down
    /// to fit BoxW on one line without wrapping; multi-line mode wraps inside BoxW x BoxH.
    /// Ends at <paramref name="bottom"/> and returns the top.
    /// </summary>
    private static float DrawText(SKSurface surface, string text, float bottom, string fontFamily, bool singleLine)
    {
        using var typeface = SKTypeface.FromFamilyName(fontFamily, new SKFontStyle(SKFontStyleWeight.ExtraBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright));
        using var font = new SKFont(typeface) { Embolden = typeface.FontWeight < 800 };

        // Largest size at which the text fits the box.
        float size = (singleLine ? MaxSeasonFontSize : MaxFontSize) + 2;
        List<string> lines;
        do
        {
            size -= 2;
            font.Size = size;
            lines = singleLine ? [text] : Wrap(text, font);
        }
        while (size > MinFontSize && (lines.Count * size * LineHeight > BoxH || lines.Any(l => font.MeasureText(l) > BoxW)));

        var lineHeight = size * LineHeight;
        var top = bottom - lines.Count * lineHeight;
        using var path = new SKPath();
        for (var i = 0; i < lines.Count; i++)
        {
            var x = (W - font.MeasureText(lines[i])) / 2;
            using var line = font.GetTextPath(lines[i], new SKPoint(x, top + lineHeight * (i + 0.8f)));
            path.AddPath(line);
        }

        var canvas = surface.Canvas;

        // Soft shadow behind the letters so bright backdrops don't wash out the frosted glass.
        using (var shadow = new SKPaint
        {
            Color = SKColors.Black.WithAlpha(90),
            IsAntialias = true,
            ImageFilter = SKImageFilter.CreateDropShadowOnly(0, 2, 6, 6, SKColors.Black.WithAlpha(110))
        })
        {
            canvas.DrawPath(path, shadow);
        }

        using var backdrop = surface.Snapshot();
        canvas.Save();
        canvas.ClipPath(path, SKClipOperation.Intersect, true);
        using (var blur = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(FrostBlur, FrostBlur) })
        {
            canvas.DrawImage(backdrop, 0, 0, Sampling, blur);
        }

        using (var tint = new SKPaint { Color = SKColors.White.WithAlpha(FrostTint) })
        {
            canvas.DrawPaint(tint);
        }

        canvas.Restore();
        return top;
    }

    private static List<string> Wrap(string text, SKFont font)
    {
        var lines = new List<string>();
        var current = "";
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = current.Length == 0 ? word : current + " " + word;
            if (current.Length > 0 && font.MeasureText(candidate) > BoxW)
            {
                lines.Add(current);
                current = word;
            }
            else
            {
                current = candidate;
            }
        }

        if (current.Length > 0)
        {
            lines.Add(current);
        }

        return lines;
    }
}
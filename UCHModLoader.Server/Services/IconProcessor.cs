using SkiaSharp;
using static System.Net.Mime.MediaTypeNames;

namespace UCHModLoader.Server.Services;

/// <summary>
/// Normalizes an uploaded icon: decodes the image, center-crops to a square,
/// resizes to IconSize, and re-encodes as PNG. Returns null when the bytes
/// are not a decodable image.
/// </summary>
public static class IconProcessor
{
    public const int IconSize = 512;

    // A 1 MB file can still decode to gigabytes of pixels; reject absurd
    // dimensions from the header before allocating anything.
    private const int MaxDimension = 4096;

    public static byte[]? Normalize(byte[] uploadedBytes)
    {
        try
        {
            var bounds = SKBitmap.DecodeBounds(uploadedBytes);
            if (bounds.Width <= 0 || bounds.Height <= 0 ||
                bounds.Width > MaxDimension || bounds.Height > MaxDimension)
                return null;

            using var original = SKBitmap.Decode(uploadedBytes);
            if (original is null) return null;

            var side = Math.Min(original.Width, original.Height);
            if (side <= 0) return null;

            var cropX = (original.Width - side) / 2;
            var cropY = (original.Height - side) / 2;

            using var cropped = new SKBitmap(side, side);
            using (var canvas = new SKCanvas(cropped))
            {
                canvas.DrawBitmap(
                    original,
                    new SKRect(cropX, cropY, cropX + side, cropY + side),
                    new SKRect(0, 0, side, side));
            }

            using var resized = cropped.Resize(
                new SKImageInfo(IconSize, IconSize, SKColorType.Rgba8888, SKAlphaType.Premul),
                SKFilterQuality.High);
            if (resized is null) return null;

            using var image = SKImage.FromBitmap(resized);
            using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
            return encoded?.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[IconProcessor] {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
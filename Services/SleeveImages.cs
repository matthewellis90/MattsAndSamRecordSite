using SkiaSharp;

namespace MattsRecords.Services;

internal static class SleeveImages
{
    public const int MaxFileBytes = 14 * 1024 * 1024;
    public const int MaxRequestBytes = 30 * 1024 * 1024;
    private const int LongEdge = 1600;

    public static async Task<byte[]> ReencodeAsync(IFormFile file, CancellationToken cancellationToken)
    {
        if (file.Length is <= 0 or > MaxFileBytes)
            throw new InvalidDataException("Choose an image of up to 14 MB.");

        await using var input = file.OpenReadStream();
        // A seekable, independent stream also works for multipart sections.
        using var source = new MemoryStream((int)file.Length);
        await input.CopyToAsync(source, cancellationToken);
        source.Position = 0;
        using var encoded = new SKManagedStream(source);
        using var codec = SKCodec.Create(encoded);
        if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png or SKEncodedImageFormat.Webp))
            throw new InvalidDataException("Choose a readable JPEG, PNG or WebP image.");

        var width = codec.Info.Width;
        var height = codec.Info.Height;
        if (width <= 0 || height <= 0 || (long)width * height > 40_000_000)
            throw new InvalidDataException("Choose a photo with up to 40 megapixels.");

        cancellationToken.ThrowIfCancellationRequested();
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels()) != SKCodecResult.Success)
            throw new InvalidDataException("This image could not be fully decoded.");

        var origin = codec.EncodedOrigin;
        var rotated = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        var orientedWidth = rotated ? height : width;
        var orientedHeight = rotated ? width : height;
        var scale = Math.Min(1f, (float)LongEdge / Math.Max(orientedWidth, orientedHeight));
        var surfaceInfo = new SKImageInfo(Math.Max(1, (int)Math.Round(orientedWidth * scale)),
            Math.Max(1, (int)Math.Round(orientedHeight * scale)));
        using var surface = SKSurface.Create(surfaceInfo);
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.White);
        canvas.Scale((float)surfaceInfo.Width / orientedWidth, (float)surfaceInfo.Height / orientedHeight);
        ApplyOrientation(canvas, origin, width, height);
        using var raster = SKImage.FromBitmap(bitmap);
        canvas.DrawImage(raster, 0, 0, new SKSamplingOptions(SKFilterMode.Linear));
        using var image = surface.Snapshot();
        using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 85);
        if (jpeg is null)
            throw new InvalidDataException("This image could not be saved.");
        return jpeg.ToArray();
    }

    private static void ApplyOrientation(SKCanvas canvas, SKEncodedOrigin origin, int width, int height)
    {
        switch (origin)
        {
            case SKEncodedOrigin.TopRight:
                canvas.Translate(width, 0);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.BottomRight:
                canvas.Translate(width, height);
                canvas.RotateDegrees(180);
                break;
            case SKEncodedOrigin.BottomLeft:
                canvas.Translate(0, height);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.LeftTop:
                canvas.RotateDegrees(90);
                canvas.Scale(1, -1);
                break;
            case SKEncodedOrigin.RightTop:
                canvas.Translate(height, 0);
                canvas.RotateDegrees(90);
                break;
            case SKEncodedOrigin.RightBottom:
                canvas.Translate(height, width);
                canvas.RotateDegrees(90);
                canvas.Scale(-1, 1);
                break;
            case SKEncodedOrigin.LeftBottom:
                canvas.Translate(0, width);
                canvas.RotateDegrees(-90);
                break;
        }
    }
}

using System.Buffers.Binary;

namespace TvdiWebGis.Services;

public record RasterPreview(int Width, int Height, double West, double South, double East, double North,
    string Samples, bool Reduced, double? Mean, double? Min, double? Max);

public static class RasterPreviewService
{
    public static RasterPreview Create(BandData band)
    {
        RasterQuality.ValidateGrid(band);
        var width = Math.Min(band.Width, 768);
        var height = Math.Min(band.Height, 768);
        var bytes = new byte[width * height * sizeof(float)];
        for (int row = 0; row < height; row++)
        for (int col = 0; col < width; col++)
        {
            int y = (int)((row + 0.5) * band.Height / height);
            int x = (int)((col + 0.5) * band.Width / width);
            var value = band.Pixels[y * band.Width + x];
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan((row * width + col) * sizeof(float)),
                value is >= 0 and <= 1 ? value : float.NaN);
        }

        double sum = 0, min = 1, max = 0;
        int count = 0;
        foreach (var value in band.Pixels)
        {
            if (value is not (>= 0 and <= 1)) continue;
            sum += value;
            min = Math.Min(min, value);
            max = Math.Max(max, value);
            count++;
        }

        var gt = band.GeoTransform;
        return new(width, height, gt[0], gt[3] + gt[5] * band.Height,
            gt[0] + gt[1] * band.Width, gt[3], Convert.ToBase64String(bytes),
            width != band.Width || height != band.Height, count == 0 ? null : sum / count,
            count == 0 ? null : min, count == 0 ? null : max);
    }
}

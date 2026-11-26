using OSGeo.OSR;

namespace TvdiWebGis.Services;

public static class RasterQuality
{
    public static int CountValid(BandData band)
    {
        ValidateGrid(band);
        int count = 0;
        foreach (var value in band.Pixels)
        {
            if (float.IsNaN(value)) continue;
            if (value is < 0 or > 1)
                throw new InvalidDataException("Ảnh TVDI có pixel ngoài khoảng 0–1. Kiểm tra hệ số tỷ lệ và giá trị NoData.");
            count++;
        }

        if (count == 0) throw new InvalidDataException("Ảnh không có pixel TVDI hợp lệ.");
        return count;
    }

    public static void ValidateGrid(BandData band)
    {
        var gt = band.GeoTransform;
        if (string.IsNullOrWhiteSpace(band.Projection) || gt.Any(x => !double.IsFinite(x)) ||
            gt[1] <= 0 || gt[5] >= 0 || Math.Abs(gt[2]) > 1e-12 || Math.Abs(gt[4]) > 1e-12 ||
            gt[0] < -180 || gt[0] + gt[1] * band.Width > 180 ||
            gt[3] > 90 || gt[3] + gt[5] * band.Height < -90)
            throw new InvalidDataException("Cần GeoTIFF EPSG:4326 với lưới pixel không xoay.");

        using var actual = new SpatialReference(band.Projection);
        using var expected = new SpatialReference("");
        expected.ImportFromEPSG(4326);
        if (actual.IsSame(expected, null) != 1)
            throw new InvalidDataException("Hệ tọa độ ảnh phải là WGS 84 (EPSG:4326).");
    }

    public static void RequireSameGrid(BandData reference, BandData candidate)
    {
        if (reference.Width != candidate.Width || reference.Height != candidate.Height ||
            reference.GeoTransform.Where((value, index) => Math.Abs(value - candidate.GeoTransform[index]) > 1e-9).Any())
            throw new InvalidDataException("Ảnh lệch lưới. Hãy căn lại phạm vi, vị trí và độ phân giải theo cùng ảnh chuẩn.");
    }

    public static void RequireMonthlySeries(IReadOnlyList<DateOnly> periods)
    {
        for (int i = 1; i < periods.Count; i++)
        {
            if (periods[i] != periods[i - 1].AddMonths(1))
                throw new InvalidDataException($"Chuỗi tháng bị thiếu hoặc trùng giữa {periods[i - 1]:MM/yyyy} và {periods[i]:MM/yyyy}.");
        }
    }
}

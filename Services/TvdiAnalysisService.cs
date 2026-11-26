using Microsoft.EntityFrameworkCore;
using TvdiWebGis.Data;
using TvdiWebGis.Forecasting;

namespace TvdiWebGis.Services;

public record RasterQualityRow(DateOnly Period, string FileName, double? Coverage, string? Issue);
public record WardChange(string Code, string Name, double? Before, double? After, double? Delta, int Pixels, bool Estimated);
public record ForecastMetric(string Model, int Pixels, double? Mae, double? Rmse, IReadOnlyList<WardChange> WardErrors);
public record ValidationResult(DateOnly Target, DateOnly Origin, IReadOnlyList<ForecastMetric> Metrics);
public record WardTrendPoint(DateOnly Period, double? Mean, int Pixels, bool Estimated);

public class TvdiAnalysisService(
    IDbContextFactory<ApplicationDbContext> factory, GeoTiffService tiff, WardZonalService wards,
    IEnumerable<IForecastModel> models, SemaphoreSlim forecastGate)
{
    private const long MaxTrainingValues = 24_000_000;

    public async Task<IReadOnlyList<RasterQualityRow>> InspectAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var files = await db.RasterFiles.OrderBy(r => r.Period).ThenBy(r => r.Id)
            .Select(r => new { r.Id, r.Period, r.FileName }).ToListAsync(ct);
        var result = new List<RasterQualityRow>();
        BandData? reference = null;

        foreach (var file in files)
        {
            try
            {
                var band = await ReadAsync(db, file.Id, ct);
                var count = RasterQuality.CountValid(band);
                reference ??= band with { Pixels = [] };
                RasterQuality.RequireSameGrid(reference, band);
                result.Add(new(file.Period, file.FileName, (double)count / band.Pixels.Length, null));
            }
            catch (InvalidDataException ex)
            {
                result.Add(new(file.Period, file.FileName, null, ex.Message));
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<WardChange>> CompareAsync(Guid beforeId, Guid afterId, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var before = await ReadAsync(db, beforeId, ct);
        var after = await ReadAsync(db, afterId, ct);
        RasterQuality.CountValid(before);
        RasterQuality.CountValid(after);
        RasterQuality.RequireSameGrid(before, after);

        // Use the same valid pixel population in both months.
        for (int i = 0; i < before.Pixels.Length; i++)
        {
            if (float.IsNaN(before.Pixels[i]) || float.IsNaN(after.Pixels[i]))
                before.Pixels[i] = after.Pixels[i] = float.NaN;
        }

        var left = wards.Compute(before);
        var right = wards.Compute(after);
        return left.Zip(right, (a, b) => new WardChange(a.Code, a.Name, a.Mean, b.Mean,
            b.Mean - a.Mean, a.ValidPixels, a.Estimated || b.Estimated)).ToList();
    }

    public async Task<IReadOnlyList<WardTrendPoint>> TrendAsync(string code, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        var files = await db.RasterFiles.AsNoTracking().OrderBy(r => r.Period)
            .Select(r => new { r.Id, r.Period, r.Data }).ToListAsync(ct);
        var result = new List<WardTrendPoint>(files.Count);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            var stats = wards.Compute(tiff.Read(file.Data)).FirstOrDefault(s => s.Code == code);
            if (stats is null) throw new InvalidDataException("Không tìm thấy phường/xã đã chọn.");
            result.Add(new(file.Period, stats.Mean, stats.ValidPixels, stats.Estimated));
        }
        return result;
    }

    public async Task<ValidationResult> ValidateAsync(DateOnly target, int horizon, CancellationToken ct)
    {
        if (horizon is < 1 or > ForecastService.Horizon)
            throw new InvalidDataException("Hạn dự báo phải từ 1 đến 6 tháng.");

        var origin = target.AddMonths(-horizon);
        await using var db = await factory.CreateDbContextAsync(ct);
        var files = await db.RasterFiles.Where(r => r.Period <= origin || r.Period == target)
            .OrderBy(r => r.Period).ThenBy(r => r.Id)
            .Select(r => new { r.Id, r.Period }).ToListAsync(ct);
        var training = files.Where(r => r.Period <= origin).ToList();
        if (files.Count(r => r.Period == target) != 1 || training.Count < 3 || training[^1].Period != origin)
            throw new InvalidDataException("Cần một ảnh thực tế ở kỳ kiểm định và ít nhất ba ảnh đến đúng kỳ bắt đầu dự báo.");
        RasterQuality.RequireMonthlySeries(training.Select(r => r.Period).ToList());

        if (!await forecastGate.WaitAsync(0, ct))
            throw new InvalidDataException("Máy chủ đang tính dự báo. Vui lòng thử lại sau.");
        try
        {
            var actual = await ReadAsync(db, files.Single(r => r.Period == target).Id, ct);
            RasterQuality.CountValid(actual);
            if ((long)actual.Pixels.Length * (training.Count + horizon + 1) > MaxTrainingValues)
                throw new InvalidDataException("Chuỗi kiểm định quá lớn. Hãy dùng ảnh có độ phân giải thấp hơn.");

            var frames = new List<float[]>(training.Count);
            foreach (var item in training)
            {
                var band = await ReadAsync(db, item.Id, ct);
                RasterQuality.CountValid(band);
                RasterQuality.RequireSameGrid(actual, band);
                frames.Add(band.Pixels);
            }

            var series = new RasterSeries(training.Select(r => r.Period).ToList(), frames,
                actual.Width, actual.Height, actual.GeoTransform, actual.Projection);
            var predictions = new List<(string Name, float[] Pixels)> { ("Giữ nguyên tháng gần nhất", frames[^1]) };
            foreach (var model in models.Where(m => m.MinPeriods <= frames.Count))
            {
                try
                {
                    var output = await model.ForecastAsync(series, horizon, ct);
                    predictions.Add((model.DisplayName, output[^1]));
                }
                catch (InvalidDataException)
                {
                    // A model with insufficient valid samples is excluded from the common mask.
                }
            }

            var sums = new double[predictions.Count];
            var squares = new double[predictions.Count];
            int count = 0;
            for (int i = 0; i < actual.Pixels.Length; i++)
            {
                if (float.IsNaN(actual.Pixels[i]) || predictions.Any(p => float.IsNaN(p.Pixels[i])))
                {
                    actual.Pixels[i] = float.NaN;
                    foreach (var prediction in predictions) prediction.Pixels[i] = float.NaN;
                    continue;
                }
                count++;
                for (int j = 0; j < predictions.Count; j++)
                {
                    double error = (double)predictions[j].Pixels[i] - actual.Pixels[i];
                    sums[j] += Math.Abs(error);
                    squares[j] += error * error;
                }
            }

            var observedWards = count == 0 ? [] : wards.Compute(actual);
            var metrics = predictions.Select((p, i) => new ForecastMetric(p.Name, count,
                count == 0 ? null : sums[i] / count, count == 0 ? null : Math.Sqrt(squares[i] / count),
                count == 0 ? [] : observedWards.Zip(wards.Compute(actual with { Pixels = p.Pixels }),
                    (a, b) => new WardChange(a.Code, a.Name, a.Mean, b.Mean,
                        b.Mean - a.Mean, a.ValidPixels, a.Estimated || b.Estimated)).ToList())).ToList();
            return new(target, origin, metrics);
        }
        finally
        {
            forecastGate.Release();
        }
    }

    private async Task<BandData> ReadAsync(ApplicationDbContext db, Guid id, CancellationToken ct)
    {
        var data = await db.RasterFiles.Where(r => r.Id == id).Select(r => r.Data).SingleOrDefaultAsync(ct)
            ?? throw new InvalidDataException("Ảnh đã bị thay thế hoặc xóa. Hãy tải lại danh sách.");
        return tiff.Read(data);
    }
}

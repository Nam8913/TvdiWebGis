using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using TvdiWebGis.Data;
using TvdiWebGis.Data.Entities;
using TvdiWebGis.Forecasting;

namespace TvdiWebGis.Services;

public class ForecastService(
    IDbContextFactory<ApplicationDbContext> factory,
    IEnumerable<IForecastModel> models,
    GeoTiffService tiff,
    SettingsService settings)
{
    public const int Horizon = 6;
    public IReadOnlyList<IForecastModel> Models => models.ToList();

    /// User thường: luôn dùng model mặc định. Admin: dùng requestedKey nếu có.
    public async Task<string> ResolveModelKeyAsync(bool isAdmin, string? requestedKey)
    {
        var key = isAdmin && !string.IsNullOrWhiteSpace(requestedKey)
            ? requestedKey : await settings.GetDefaultModelAsync();
        return models.Any(m => m.Key == key) ? key : "linear";
    }

    public async Task<byte[]?> GetForecastAsync(string modelKey, int step, CancellationToken ct = default)
    {
        if (step < 1 || step > Horizon) return null;
        var model = models.First(m => m.Key == modelKey);

        await using var db = await factory.CreateDbContextAsync(ct);
        var files = await db.RasterFiles.AsNoTracking().OrderBy(r => r.Period).ToListAsync(ct);
        if (files.Count < model.MinPeriods) return null;

        var sig = Signature(files.Select(f => f.Id), modelKey);
        var cached = await db.ForecastResults.AsNoTracking()
            .FirstOrDefaultAsync(r => r.ModelKey == modelKey && r.Signature == sig && r.Step == step, ct);
        if (cached is not null) return cached.Data;

        // Đọc chuỗi ảnh
        var bands = files.Select(f => tiff.Read(f.Data)).ToList();
        var first = bands[0];
        if (bands.Any(b => b.Width != first.Width || b.Height != first.Height))
            throw new InvalidOperationException("Các ảnh phải cùng kích thước/lưới pixel.");

        var series = new RasterSeries(files.Select(f => f.Period).ToList(), bands.Select(b => b.Pixels).ToList(),
            first.Width, first.Height, first.GeoTransform, first.Projection);

        var outputs = await model.ForecastAsync(series, Horizon, ct);

        for (int k = 0; k < outputs.Count; k++)
        {
            db.ForecastResults.Add(new ForecastResult
            {
                ModelKey = modelKey, Signature = sig, Step = k + 1,
                Period = series.Periods[^1].AddMonths(k + 1),
                Data = tiff.Write(outputs[k], first.Width, first.Height, first.GeoTransform, first.Projection)
            });
        }
        // Xoá cache cũ của model này (chuỗi ảnh đã đổi)
        await db.ForecastResults.Where(r => r.ModelKey == modelKey && r.Signature != sig).ExecuteDeleteAsync(ct);
        await db.SaveChangesAsync(ct);

        return (await db.ForecastResults.AsNoTracking()
            .FirstAsync(r => r.ModelKey == modelKey && r.Signature == sig && r.Step == step, ct)).Data;
    }

    private static string Signature(IEnumerable<Guid> ids, string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key + string.Join('|', ids))));
}
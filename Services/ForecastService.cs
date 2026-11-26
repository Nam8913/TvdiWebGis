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
    SettingsService settings,
    SemaphoreSlim forecastGate)
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
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var metadata = await db.RasterFiles.AsNoTracking()
                .OrderBy(r => r.Period).ThenBy(r => r.Id)
                .Select(r => new { r.Id, r.Period }).ToListAsync(ct);
            if (metadata.Count < model.MinPeriods) return null;

            var periods = metadata.Select(r => r.Period).ToList();
            RasterQuality.RequireMonthlySeries(periods);
            var ids = metadata.Select(r => r.Id).ToArray();
            var sig = Signature(ids, modelKey);
            var cached = await ReadCacheAsync(sig);
            if (cached is not null) return cached;

            await forecastGate.WaitAsync(ct);
            try
            {
                cached = await ReadCacheAsync(sig);
                if (cached is not null) return cached;

                var bands = new List<BandData>(ids.Length);
                foreach (var id in ids)
                {
                    var bytes = await db.RasterFiles.Where(r => r.Id == id)
                        .Select(r => r.Data).FirstOrDefaultAsync(ct);
                    if (bytes is null) break;
                    var band = tiff.Read(bytes);
                    RasterQuality.CountValid(band);
                    if ((long)band.Pixels.Length * (ids.Length + Horizon) > 24_000_000)
                        throw new InvalidDataException("Chuỗi dự báo quá lớn. Hãy dùng ảnh có độ phân giải thấp hơn.");
                    if (bands.Count > 0) RasterQuality.RequireSameGrid(bands[0], band);
                    bands.Add(band);
                }

                // An administrator may replace a raster while the series is loading.
                if (bands.Count != ids.Length) continue;
                var first = bands[0];

                var series = new RasterSeries(periods, bands.Select(b => b.Pixels).ToList(),
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
                await using var transaction = await db.Database.BeginTransactionAsync(ct);
                await db.ForecastResults.Where(r => r.ModelKey == modelKey && r.Signature != sig).ExecuteDeleteAsync(ct);
                await db.SaveChangesAsync(ct);
                await transaction.CommitAsync(ct);

                return (await db.ForecastResults.AsNoTracking()
                    .FirstAsync(r => r.ModelKey == modelKey && r.Signature == sig && r.Step == step, ct)).Data;
            }
            finally
            {
                forecastGate.Release();
            }
        }

        return null;

        Task<byte[]?> ReadCacheAsync(string signature) => db.ForecastResults.AsNoTracking()
            .Where(r => r.ModelKey == modelKey && r.Signature == signature && r.Step == step)
            .Select(r => r.Data).FirstOrDefaultAsync(ct);
    }

    private static string Signature(IEnumerable<Guid> ids, string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("grid-v2:" + key + string.Join('|', ids))));
}
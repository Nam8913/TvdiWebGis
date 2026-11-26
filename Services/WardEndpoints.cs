using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.EntityFrameworkCore;
using TvdiWebGis.Data;
using TvdiWebGis.Services;

namespace TvdiWebGis.Endpoints;

public static class WardEndpoints
{
    public static void MapWardEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/wards").RequireAuthorization().RequireRateLimiting("map-api");

        // Danh sách phường/xã
        g.MapGet("/", (WardZonalService z) => z.Wards);

        g.MapGet("/trend/{code}", async (string code, TvdiAnalysisService analysis, CancellationToken ct) =>
        {
            try { return Results.Ok(await analysis.TrendAsync(code, ct)); }
            catch (InvalidDataException ex) { return Results.NotFound(ex.Message); }
        });

        // TVDI trung bình từng phường/xã
        //   ?source=obs:{guid}            → ảnh quan sát
        //   ?source=fc:{bước}[&model=..]  → ảnh dự báo (user thường luôn dùng model mặc định)
        g.MapGet("/stats", async (string source, string? model, ClaimsPrincipal user,
            IDbContextFactory<ApplicationDbContext> factory, ForecastService fc,
            WardZonalService z, IMemoryCache cache, CancellationToken ct) =>
        {
            byte[]? data;
            if (source.StartsWith("obs:") && Guid.TryParse(source[4..], out var id))
            {
                await using var db = await factory.CreateDbContextAsync(ct);
                data = await db.RasterFiles.Where(r => r.Id == id).Select(r => r.Data).FirstOrDefaultAsync(ct);
            }
            else if (source.StartsWith("fc:") && int.TryParse(source[3..], out var step))
            {
                var key = await fc.ResolveModelKeyAsync(user.IsInRole("Admin"), model);
                try { data = await fc.GetForecastAsync(key, step, ct); }
                catch (InvalidDataException ex) { return Results.Text(ex.Message, statusCode: 422); }
            }
            else return Results.BadRequest("source không hợp lệ (dùng obs:{id} hoặc fc:{bước}).");

            if (data is null) return Results.NotFound("Không có dữ liệu cho nguồn này.");

            var cacheKey = "ward-stats:" + Convert.ToHexString(SHA256.HashData(data));
            try
            {
                return Results.Ok(cache.GetOrCreate(cacheKey, entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5);
                    return z.Compute(data);
                }));
            }
            catch (InvalidDataException ex) { return Results.Text(ex.Message, statusCode: 422); }
        });
    }
}

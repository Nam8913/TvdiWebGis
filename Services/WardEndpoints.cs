using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TvdiWebGis.Data;
using TvdiWebGis.Services;

namespace TvdiWebGis.Endpoints;

public static class WardEndpoints
{
    public static void MapWardEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api/wards").RequireAuthorization();

        // Danh sách phường/xã
        g.MapGet("/", (WardZonalService z) => z.Wards);

        // TVDI trung bình từng phường/xã
        //   ?source=obs:{guid}            → ảnh quan sát
        //   ?source=fc:{bước}[&model=..]  → ảnh dự báo (user thường luôn dùng model mặc định)
        g.MapGet("/stats", async (string source, string? model, ClaimsPrincipal user,
            IDbContextFactory<ApplicationDbContext> factory, ForecastService fc,
            WardZonalService z, CancellationToken ct) =>
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
                data = await fc.GetForecastAsync(key, step, ct);
            }
            else return Results.BadRequest("source không hợp lệ (dùng obs:{id} hoặc fc:{bước}).");

            return data is null
                ? Results.NotFound("Không có dữ liệu cho nguồn này.")
                : Results.Ok(z.Compute(data));
        });
    }
}
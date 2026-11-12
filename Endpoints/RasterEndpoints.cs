using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TvdiWebGis.Data;
using TvdiWebGis.Services;

namespace TvdiWebGis.Endpoints;

public static class RasterEndpoints
{
    public static void MapRasterEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization();   // bỏ nếu muốn cho xem ẩn danh

        // Ảnh TVDI quan sát
        g.MapGet("/rasters/{id:guid}", async (Guid id, IDbContextFactory<ApplicationDbContext> f) =>
        {
            await using var db = await f.CreateDbContextAsync();
            var data = await db.RasterFiles.Where(r => r.Id == id).Select(r => r.Data).FirstOrDefaultAsync();
            return data is null ? Results.NotFound() : Results.File(data, "image/tiff");
        });

        // Ảnh dự báo: admin được truyền ?model=..., user thường luôn dùng model mặc định
        g.MapGet("/forecast/{step:int}", async (int step, string? model, ClaimsPrincipal user,
            ForecastService fc, CancellationToken ct) =>
        {
            var key = await fc.ResolveModelKeyAsync(user.IsInRole("Admin"), model);
            var data = await fc.GetForecastAsync(key, step, ct);
            return data is null ? Results.NotFound("Chưa đủ dữ liệu cho model này.") : Results.File(data, "image/tiff");
        });
    }
}
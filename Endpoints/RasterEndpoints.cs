using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using TvdiWebGis.Data;
using TvdiWebGis.Services;

namespace TvdiWebGis.Endpoints;

public static class RasterEndpoints
{
    public static void MapRasterEndpoints(this WebApplication app)
    {
        var g = app.MapGroup("/api").RequireAuthorization().RequireRateLimiting("map-api");   // bỏ nếu muốn cho xem ẩn danh

        // Ảnh TVDI quan sát
        g.MapGet("/rasters/{id:guid}", async (Guid id, bool? preview, IDbContextFactory<ApplicationDbContext> f, GeoTiffService tiff, CancellationToken ct) =>
        {
            await using var db = await f.CreateDbContextAsync(ct);
            var data = await db.RasterFiles.Where(r => r.Id == id).Select(r => r.Data).FirstOrDefaultAsync(ct);
            return RasterResponse(data, preview, tiff);
        });

        // Ảnh dự báo: admin được truyền ?model=..., user thường luôn dùng model mặc định
        g.MapGet("/forecast/{step:int}", async (int step, string? model, bool? preview, ClaimsPrincipal user,
            ForecastService fc, GeoTiffService tiff, CancellationToken ct) =>
        {
            var key = await fc.ResolveModelKeyAsync(user.IsInRole("Admin"), model);
            byte[]? data;
            try { data = await fc.GetForecastAsync(key, step, ct); }
            catch (InvalidDataException ex) { return Results.Text(ex.Message, statusCode: 422); }
            return RasterResponse(data, preview, tiff);
        });
    }
    private static IResult RasterResponse(byte[]? data, bool? preview, GeoTiffService tiff)
    {
        if (data is null) return Results.NotFound("Chưa có ảnh TVDI cho kỳ hoặc mô hình đã chọn.");
        if (preview != true) return Results.File(data, "image/tiff");

        try { return Results.Ok(RasterPreviewService.Create(tiff.Read(data))); }
        catch (InvalidDataException ex) { return Results.Text(ex.Message, statusCode: 422); }
    }
}

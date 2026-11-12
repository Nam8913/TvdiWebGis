using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TvdiWebGis.Data;
using TvdiWebGis.Data.Entities;

namespace TvdiWebGis.Services;

public class RasterFileService(IDbContextFactory<ApplicationDbContext> factory, GeoTiffService tiff)
{
    // Tên file dạng TVDI_TPHCM_2025_03.tif → kỳ 2025-03
    private static readonly Regex PeriodRx = new(@"(?<y>20\d{2})[_\-](?<m>\d{1,2})", RegexOptions.Compiled);

    public async Task<(bool ok, string msg)> AddAsync(string fileName, byte[] data)
    {
        var m = PeriodRx.Match(fileName);
        if (!m.Success) return (false, $"{fileName}: không đọc được kỳ (cần dạng *_YYYY_MM.tif)");
        var period = new DateOnly(int.Parse(m.Groups["y"].Value), int.Parse(m.Groups["m"].Value), 1);

        BandData band;
        try { band = tiff.Read(data); }
        catch (Exception ex) { return (false, $"{fileName}: không phải GeoTIFF hợp lệ ({ex.Message})"); }

        await using var db = await factory.CreateDbContextAsync();
        // 1 kỳ = 1 ảnh: nạp lại thì thay thế
        var old = await db.RasterFiles.Where(r => r.Period == period).ToListAsync();
        db.RasterFiles.RemoveRange(old);
        db.RasterFiles.Add(new RasterFile { FileName = fileName, Period = period, Width = band.Width, Height = band.Height, Data = data });
        await db.SaveChangesAsync();
        return (true, $"{fileName}: OK → kỳ {period:yyyy-MM}");
    }

    public async Task<List<RasterFile>> ListAsync()
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.RasterFiles.AsNoTracking().OrderBy(r => r.Period)
            .Select(r => new RasterFile { Id = r.Id, FileName = r.FileName, Period = r.Period, Width = r.Width, Height = r.Height, UploadedAt = r.UploadedAt })
            .ToListAsync();
    }

    public async Task DeleteAsync(Guid id)
    {
        await using var db = await factory.CreateDbContextAsync();
        await db.RasterFiles.Where(r => r.Id == id).ExecuteDeleteAsync();
    }
}
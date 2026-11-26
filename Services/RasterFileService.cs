using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using TvdiWebGis.Data;
using TvdiWebGis.Data.Entities;

namespace TvdiWebGis.Services;

public class RasterFileService(IDbContextFactory<ApplicationDbContext> factory, GeoTiffService tiff)
{
    public const int MaxUploadBytes = 50 * 1024 * 1024;

    // Tên file dạng TVDI_TPHCM_2025_03.tif → kỳ 2025-03
    private static readonly Regex PeriodRx = new(@"(?<y>20\d{2})[_\-](?<m>\d{1,2})", RegexOptions.Compiled);

    public async Task<(bool ok, string msg)> AddAsync(string fileName, byte[] data)
    {
        fileName = Path.GetFileName(fileName);
        var extension = Path.GetExtension(fileName);
        if (!extension.Equals(".tif", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".tiff", StringComparison.OrdinalIgnoreCase))
            return (false, "Chỉ chấp nhận tệp .tif hoặc .tiff.");
        if (data.Length == 0 || data.Length > MaxUploadBytes)
            return (false, "Dung lượng tệp phải lớn hơn 0 và tối đa 50 MiB.");

        var m = PeriodRx.Match(fileName);
        if (!m.Success) return (false, $"{fileName}: không đọc được kỳ (cần dạng *_YYYY_MM.tif)");
        var month = int.Parse(m.Groups["m"].Value);
        if (month is < 1 or > 12) return (false, "Tháng phải từ 1 đến 12.");
        var period = new DateOnly(int.Parse(m.Groups["y"].Value), month, 1);

        BandData band;
        int valid;
        try
        {
            band = tiff.Read(data);
            valid = RasterQuality.CountValid(band);
        }
        catch (Exception ex) { return (false, $"{fileName}: không phải GeoTIFF hợp lệ ({ex.Message})"); }

        await using var db = await factory.CreateDbContextAsync();
        // 1 kỳ = 1 ảnh: nạp lại thì thay thế
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var reference = await db.RasterFiles.Where(r => r.Period != period)
            .OrderBy(r => r.Period).Select(r => r.Data).FirstOrDefaultAsync();
        if (reference is not null)
        {
            try { RasterQuality.RequireSameGrid(tiff.Read(reference), band); }
            catch (InvalidDataException ex) { return (false, ex.Message); }
        }

        await db.RasterFiles.Where(r => r.Period == period).ExecuteDeleteAsync();
        db.RasterFiles.Add(new RasterFile { FileName = fileName, Period = period, Width = band.Width, Height = band.Height, Data = data });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return (true, $"{fileName}: {period:MM/yyyy} · {(double)valid / band.Pixels.Length:P1} pixel hợp lệ.");
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
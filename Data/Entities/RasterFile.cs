namespace TvdiWebGis.Data.Entities;

public class RasterFile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string FileName { get; set; } = "";
    public DateOnly Period { get; set; }          // kỳ (tháng) lấy từ tên file
    public int Width { get; set; }
    public int Height { get; set; }
    public byte[] Data { get; set; } = [];        // VARBINARY(MAX)
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}
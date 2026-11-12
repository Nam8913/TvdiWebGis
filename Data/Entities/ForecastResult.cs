namespace TvdiWebGis.Data.Entities;

public class ForecastResult
{
    public int Id { get; set; }
    public string ModelKey { get; set; } = "";
    public string Signature { get; set; } = "";   // hash chuỗi ảnh đầu vào + horizon
    public int Step { get; set; }                 // +1, +2, ...
    public DateOnly Period { get; set; }          // tháng được dự báo
    public byte[] Data { get; set; } = [];
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
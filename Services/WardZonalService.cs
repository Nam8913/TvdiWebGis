using System.Globalization;
using System.Text.Json;
using NetTopologySuite.Algorithm.Locate;
using NetTopologySuite.Geometries;

namespace TvdiWebGis.Services;

public record WardInfo(string Code, string Name, string Type);

public record WardStat(
    string Code, string Name, string Type,
    double? Mean, double? Min, double? Max,
    int ValidPixels, int TotalPixels, bool Estimated);

/// Ghép pixel raster vào từng phường/xã và tính TVDI trung bình.
public class WardZonalService
{
    private sealed record WardGeom(WardInfo Info, Geometry Geom, IndexedPointInAreaLocator Locator);
    private sealed record GridIndex(int[][] Pixels, bool[] Estimated);

    private static readonly GeometryFactory Gf = new();
    private readonly GeoTiffService _tiff;
    private readonly Lazy<List<WardGeom>> _wards;
    private readonly Dictionary<string, GridIndex> _grids = new();
    private readonly object _lock = new();

    public WardZonalService(IWebHostEnvironment env, GeoTiffService tiff)
    {
        _tiff = tiff;
        var path = Path.Combine(env.WebRootPath, "data", "tphcm-wards.json");
        _wards = new Lazy<List<WardGeom>>(() => LoadWards(path));
    }

    /// Danh sách phường/xã (không kèm giá trị)
    public IReadOnlyList<WardInfo> Wards => _wards.Value.Select(w => w.Info).ToList();

    public List<WardStat> Compute(byte[] tifBytes)
    {
        var band = _tiff.Read(tifBytes);
        var gt = band.GeoTransform;
        if (Math.Abs(gt[0]) > 180 || Math.Abs(gt[3]) > 90)
            throw new InvalidOperationException(
                "Raster không ở hệ toạ độ kinh/vĩ độ (EPSG:4326) nên không ghép được với ranh giới phường/xã.");

        GridIndex grid;
        lock (_lock)    // IndexedPointInAreaLocator không an toàn khi nhiều luồng dùng lần đầu
        {
            var key = $"{band.Width}x{band.Height}|" +
                      string.Join(',', gt.Select(x => x.ToString("R", CultureInfo.InvariantCulture)));
            if (!_grids.TryGetValue(key, out grid!))
                _grids[key] = grid = BuildGrid(band);
        }

        var wards = _wards.Value;
        var result = new List<WardStat>(wards.Count);
        for (int i = 0; i < wards.Count; i++)
        {
            double sum = 0, min = double.MaxValue, max = double.MinValue;
            int n = 0;
            foreach (var idx in grid.Pixels[i])
            {
                var v = band.Pixels[idx];
                if (float.IsNaN(v)) continue;
                sum += v; n++;
                if (v < min) min = v;
                if (v > max) max = v;
            }
            var info = wards[i].Info;
            result.Add(n == 0
                ? new WardStat(info.Code, info.Name, info.Type, null, null, null, 0, grid.Pixels[i].Length, false)
                : new WardStat(info.Code, info.Name, info.Type, sum / n, min, max, n, grid.Pixels[i].Length, grid.Estimated[i]));
        }
        return result;
    }

    // ---- Ghép lưới pixel với từng phường (chỉ làm 1 lần cho mỗi lưới ảnh) ----
    private GridIndex BuildGrid(BandData b)
    {
        var gt = b.GeoTransform;
        int w = b.Width, h = b.Height;
        var wards = _wards.Value;
        var pixels = new int[wards.Count][];
        var estimated = new bool[wards.Count];

        for (int i = 0; i < wards.Count; i++)
        {
            var env = wards[i].Geom.EnvelopeInternal;
            double ca = (env.MinX - gt[0]) / gt[1], cb = (env.MaxX - gt[0]) / gt[1];
            double ra = (env.MinY - gt[3]) / gt[5], rb = (env.MaxY - gt[3]) / gt[5];
            int c0 = Math.Max(0, (int)Math.Floor(Math.Min(ca, cb))), c1 = Math.Min(w - 1, (int)Math.Ceiling(Math.Max(ca, cb)));
            int r0 = Math.Max(0, (int)Math.Floor(Math.Min(ra, rb))), r1 = Math.Min(h - 1, (int)Math.Ceiling(Math.Max(ra, rb)));

            var list = new List<int>();
            for (int r = r0; r <= r1; r++)
                for (int c = c0; c <= c1; c++)
                {
                    var p = new Coordinate(gt[0] + (c + 0.5) * gt[1], gt[3] + (r + 0.5) * gt[5]);   // tâm pixel
                    if (wards[i].Locator.Locate(p) != Location.Exterior) list.Add(r * w + c);
                }

            if (list.Count == 0)   // phường nhỏ hơn 1 pixel → lấy pixel chứa điểm trong của phường
            {
                var ip = wards[i].Geom.InteriorPoint.Coordinate;
                int c = (int)Math.Floor((ip.X - gt[0]) / gt[1]);
                int r = (int)Math.Floor((ip.Y - gt[3]) / gt[5]);
                if (c >= 0 && c < w && r >= 0 && r < h) { list.Add(r * w + c); estimated[i] = true; }
            }
            pixels[i] = list.ToArray();
        }
        return new GridIndex(pixels, estimated);
    }

    // ---- Đọc GeoJSON ----
    private static List<WardGeom> LoadWards(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var list = new List<WardGeom>();
        foreach (var f in doc.RootElement.GetProperty("features").EnumerateArray())
        {
            var p = f.GetProperty("properties");
            var name = p.GetProperty("name").GetString() ?? "";
            var info = new WardInfo(p.GetProperty("code").GetString() ?? "", name, WardType(name));
            var g = ParseGeometry(f.GetProperty("geometry"));
            if (!g.IsValid) g = g.Buffer(0);
            list.Add(new WardGeom(info, g, new IndexedPointInAreaLocator(g)));
        }
        return list;
    }

    private static string WardType(string n) =>
        n.StartsWith("Phường") ? "Phường" : n.StartsWith("Xã") ? "Xã" : n.StartsWith("Đặc khu") ? "Đặc khu" : "";

    private static Geometry ParseGeometry(JsonElement g)
    {
        var coords = g.GetProperty("coordinates");
        return g.GetProperty("type").GetString() == "Polygon"
            ? ParsePolygon(coords)
            : Gf.CreateMultiPolygon(coords.EnumerateArray().Select(ParsePolygon).ToArray());
    }

    private static Polygon ParsePolygon(JsonElement rings)
    {
        var rs = rings.EnumerateArray()
            .Select(r => Gf.CreateLinearRing(
                r.EnumerateArray().Select(pt => new Coordinate(pt[0].GetDouble(), pt[1].GetDouble())).ToArray()))
            .ToList();
        return Gf.CreatePolygon(rs[0], rs.Skip(1).ToArray());
    }
}
using MaxRev.Gdal.Core;
using OSGeo.GDAL;

namespace TvdiWebGis.Services;

public record BandData(float[] Pixels, int Width, int Height, double[] GeoTransform, string Projection);

public class GeoTiffService
{
    private const float OutNoData = -9999f;
    static GeoTiffService() => GdalBase.ConfigureAll();

    public BandData Read(byte[] data)
    {
        if (data.Length < 8 ||
            !((data[0] == 0x49 && data[1] == 0x49 && data[2] is 42 or 43 && data[3] == 0) ||
              (data[0] == 0x4d && data[1] == 0x4d && data[2] == 0 && data[3] is 42 or 43)))
            throw new InvalidDataException("Tệp không có chữ ký TIFF hợp lệ.");

        var vsi = $"/vsimem/{Guid.NewGuid():N}.tif";
        Gdal.FileFromMemBuffer(vsi, data);
        try
        {
            using var ds = Gdal.Open(vsi, Access.GA_ReadOnly);
            if (ds is null || ds.RasterCount < 1)
                throw new InvalidDataException("Không đọc được băng ảnh.");
            using var driver = ds.GetDriver();
            if (driver.ShortName != "GTiff")
                throw new InvalidDataException("Chỉ hỗ trợ định dạng GeoTIFF.");

            int w = ds.RasterXSize, h = ds.RasterYSize;
            if (w <= 0 || h <= 0 || (long)w * h > 16_000_000)
                throw new InvalidDataException("Ảnh vượt giới hạn 16 triệu pixel.");
            var gt = new double[6]; ds.GetGeoTransform(gt);
            using var band = ds.GetRasterBand(1);
            var px = new float[w * h];
            if (band.ReadRaster(0, 0, w, h, px, w, h, 0, 0) != CPLErr.CE_None)
                throw new InvalidDataException("Không đọc được dữ liệu pixel.");

            band.GetNoDataValue(out double nd, out int hasNd);
            for (int i = 0; i < px.Length; i++)
                if (float.IsNaN(px[i]) || float.IsInfinity(px[i]) || (hasNd == 1 && Math.Abs(px[i] - nd) < 1e-6))
                    px[i] = float.NaN;

            return new BandData(px, w, h, gt, ds.GetProjection());
        }
        finally { Gdal.Unlink(vsi); }
    }

    public byte[] Write(float[] pixels, int w, int h, double[] gt, string projection)
    {
        var tmp = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.tif");
        try
        {
            using (var drv = Gdal.GetDriverByName("GTiff"))
            using (var ds = drv.Create(tmp, w, h, 1, DataType.GDT_Float32, ["COMPRESS=DEFLATE"]))
            {
                ds.SetGeoTransform(gt);
                ds.SetProjection(projection);
                var data = pixels.Select(v => float.IsNaN(v) ? OutNoData : v).ToArray();
                using var band = ds.GetRasterBand(1);
                band.SetNoDataValue(OutNoData);
                band.WriteRaster(0, 0, w, h, data, w, h, 0, 0);
                ds.FlushCache();
            }
            return File.ReadAllBytes(tmp);
        }
        finally { if (File.Exists(tmp)) File.Delete(tmp); }
    }
}
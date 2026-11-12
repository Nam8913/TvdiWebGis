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
        var vsi = $"/vsimem/{Guid.NewGuid():N}.tif";
        Gdal.FileFromMemBuffer(vsi, data);
        try
        {
            using var ds = Gdal.Open(vsi, Access.GA_ReadOnly);
            int w = ds.RasterXSize, h = ds.RasterYSize;
            var gt = new double[6]; ds.GetGeoTransform(gt);
            using var band = ds.GetRasterBand(1);
            var px = new float[w * h];
            band.ReadRaster(0, 0, w, h, px, w, h, 0, 0);

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
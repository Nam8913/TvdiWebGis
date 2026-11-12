namespace TvdiWebGis.Forecasting;

public class LinearRegressionModel : IForecastModel
{
    public string Key => "linear";
    public string DisplayName => "Hồi quy tuyến tính";
    public string Description => "OLS theo trục thời gian cho từng pixel: TVDI = a + b·t";
    public int MinPeriods => 3;

    public Task<IReadOnlyList<float[]>> ForecastAsync(RasterSeries s, int horizon, CancellationToken ct = default)
    {
        int n = s.Frames.Count, size = s.Width * s.Height;
        var result = Enumerable.Range(0, horizon).Select(_ => new float[size]).ToList();

        Parallel.For(0, size, new ParallelOptions { CancellationToken = ct }, i =>
        {
            double sx = 0, sy = 0, sxy = 0, sxx = 0; int m = 0;
            for (int t = 0; t < n; t++)
            {
                float y = s.Frames[t][i];
                if (float.IsNaN(y)) continue;
                sx += t; sy += y; sxy += (double)t * y; sxx += (double)t * t; m++;
            }
            double den = m * sxx - sx * sx;
            if (m < 3 || Math.Abs(den) < 1e-12)
            {
                for (int k = 0; k < horizon; k++) result[k][i] = float.NaN;
                return;
            }
            double b = (m * sxy - sx * sy) / den;
            double a = (sy - b * sx) / m;
            for (int k = 0; k < horizon; k++)
                result[k][i] = (float)Math.Clamp(a + b * (n + k), 0, 1);   // TVDI ∈ [0,1]
        });

        return Task.FromResult<IReadOnlyList<float[]>>(result);
    }
}
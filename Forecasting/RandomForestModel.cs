using Microsoft.ML;
using Microsoft.ML.Data;

namespace TvdiWebGis.Forecasting;

public class RandomForestModel : IForecastModel
{
    public string Key => "rf";
    public string DisplayName => "Random Forest";
    public string Description => "ML.NET FastForest, đặc trưng: TVDI 3 kỳ trước + mùa vụ (sin/cos tháng)";
    public int MinPeriods => 12;

    class Row
    {
        public float Lag1, Lag2, Lag3, MonthSin, MonthCos, Label;
    }
    class Pred { [ColumnName("Score")] public float Score; }

    public Task<IReadOnlyList<float[]>> ForecastAsync(RasterSeries s, int horizon, CancellationToken ct = default)
    {
        var ml = new MLContext(seed: 42);
        int n = s.Frames.Count, size = s.Width * s.Height;
        var rnd = new Random(42);

        // 1) Lấy mẫu pixel để huấn luyện 1 mô hình toàn cục
        var rows = new List<Row>();
        int samplesPerFrame = Math.Min(size, 20_000);
        for (int t = 3; t < n; t++)
        {
            double ang = 2 * Math.PI * s.Periods[t].Month / 12;
            for (int j = 0; j < samplesPerFrame; j++)
            {
                int i = rnd.Next(size);
                float y = s.Frames[t][i], l1 = s.Frames[t - 1][i], l2 = s.Frames[t - 2][i], l3 = s.Frames[t - 3][i];
                if (float.IsNaN(y) || float.IsNaN(l1) || float.IsNaN(l2) || float.IsNaN(l3)) continue;
                rows.Add(new Row { Lag1 = l1, Lag2 = l2, Lag3 = l3, MonthSin = (float)Math.Sin(ang), MonthCos = (float)Math.Cos(ang), Label = y });
            }
        }
        if (rows.Count < 100) throw new InvalidDataException("Không đủ dữ liệu hợp lệ để huấn luyện Random Forest.");

        var pipe = ml.Transforms.Concatenate("Features", nameof(Row.Lag1), nameof(Row.Lag2), nameof(Row.Lag3), nameof(Row.MonthSin), nameof(Row.MonthCos))
            .Append(ml.Regression.Trainers.FastForest(labelColumnName: nameof(Row.Label), featureColumnName: "Features", numberOfTrees: 100, numberOfLeaves: 20));
        var model = pipe.Fit(ml.Data.LoadFromEnumerable(rows));
        ct.ThrowIfCancellationRequested();

        // 2) Dự báo đệ quy: kỳ k dùng kết quả kỳ k-1 làm lag
        var engine = ml.Model.CreatePredictionEngine<Row, Pred>(model);
        var h = s.Frames.TakeLast(3).Select(f => (float[])f.Clone()).ToList();   // t-3, t-2, t-1
        var outputs = new List<float[]>();
        var last = s.Periods[^1];

        for (int k = 0; k < horizon; k++)
        {
            var month = last.AddMonths(k + 1).Month;
            float ms = (float)Math.Sin(2 * Math.PI * month / 12), mc = (float)Math.Cos(2 * Math.PI * month / 12);
            var next = new float[size];
            for (int i = 0; i < size; i++)
            {
                if (float.IsNaN(h[2][i]) || float.IsNaN(h[1][i]) || float.IsNaN(h[0][i])) { next[i] = float.NaN; continue; }
                var p = engine.Predict(new Row { Lag1 = h[2][i], Lag2 = h[1][i], Lag3 = h[0][i], MonthSin = ms, MonthCos = mc });
                next[i] = Math.Clamp(p.Score, 0, 1);
            }
            outputs.Add(next);
            h = [h[1], h[2], next];
            ct.ThrowIfCancellationRequested();
        }
        return Task.FromResult<IReadOnlyList<float[]>>(outputs);
    }
}
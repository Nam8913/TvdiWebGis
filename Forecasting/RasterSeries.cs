namespace TvdiWebGis.Forecasting;

public record RasterSeries(
    IReadOnlyList<DateOnly> Periods,
    IReadOnlyList<float[]> Frames,   // NaN = NoData
    int Width, int Height,
    double[] GeoTransform, string Projection);

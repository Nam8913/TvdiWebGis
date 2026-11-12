namespace TvdiWebGis.Forecasting;

public interface IForecastModel
{
    string Key { get; }
    string DisplayName { get; }
    string Description { get; }
    int MinPeriods { get; }
    Task<IReadOnlyList<float[]>> ForecastAsync(RasterSeries series, int horizon, CancellationToken ct = default);
}
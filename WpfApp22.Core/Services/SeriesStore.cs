using WpfApp22.Core.Buffers;
using WpfApp22.Core.Models;

namespace WpfApp22.Core.Services;

/// <summary>
/// 单个点位的运行时数据容器：环形缓冲 + 最新值 + 统计计数 + 供图表复用的读取数组。
/// 读取数组随 Store 一起分配，图表刷新时反复复用，避免每帧分配大数组。
/// </summary>
public sealed class SeriesStore
{
    private const int UiArraySize = 8192;

    /// <summary>屏幕只有千余像素宽，超过这个点数后继续增加只会拖慢渲染。</summary>
    private const int ChartPointLimit = 1600;

    private readonly double[] _uiTimes = new double[UiArraySize];
    private readonly double[] _uiValues = new double[UiArraySize];
    private readonly double[] _tmpTimes = new double[UiArraySize];
    private readonly double[] _tmpValues = new double[UiArraySize];

    public SeriesStore(Guid channelId, string channelName, PointDefinition definition, int capacity)
    {
        ChannelId = channelId;
        ChannelName = channelName;
        Definition = definition;
        Buffer = new TimeSeriesBuffer(capacity);
    }

    public Guid ChannelId { get; }

    public string ChannelName { get; }

    public PointDefinition Definition { get; }

    public TimeSeriesBuffer Buffer { get; }

    public long LastTimestampMs { get; private set; }

    public double LastValue { get; private set; } = double.NaN;

    public SampleQuality LastQuality { get; private set; } = SampleQuality.Stale;

    public long GoodCount { get; private set; }

    public long BadCount { get; private set; }

    public void Append(Sample sample)
    {
        Buffer.Append(sample.TimestampMs, sample.Value);
        LastTimestampMs = sample.TimestampMs;
        LastValue = sample.Value;
        LastQuality = sample.Quality;

        if (sample.IsGood) GoodCount++;
        else BadCount++;
    }

    /// <summary>
    /// 为图表刷新准备数据：填充内部复用数组并返回点数。
    /// 调用方必须在同一个刷新周期内用完，下一次调用会覆盖。
    /// </summary>
    public int FillForChart(int maxPoints)
    {
        var count = Buffer.CopyLatest(Math.Min(maxPoints, UiArraySize), _uiTimes, _uiValues);
        count = RemoveBadPoints(count);

        if (count <= ChartPointLimit) return count;

        // 点数超出屏幕可分辨范围时做峰值保留降采样，保证尖峰不被抽掉
        Array.Copy(_uiTimes, _tmpTimes, count);
        Array.Copy(_uiValues, _tmpValues, count);
        return Downsampler.PeakDecimate(_tmpTimes, _tmpValues, count, ChartPointLimit, _uiTimes, _uiValues);
    }

    /// <summary>剔除通讯失败产生的 NaN 点，让曲线保持连续而不是画成一团乱线。</summary>
    private int RemoveBadPoints(int count)
    {
        var write = 0;
        for (var i = 0; i < count; i++)
        {
            if (double.IsNaN(_uiValues[i])) continue;
            _uiTimes[write] = _uiTimes[i];
            _uiValues[write] = _uiValues[i];
            write++;
        }

        return write;
    }

    public double[] ChartTimes => _uiTimes;

    public double[] ChartValues => _uiValues;

    /// <summary>拷贝指定时间窗的数据用于导出，数组由调用方提供。</summary>
    public int FillForExport(long fromMs, long toMs, double[] times, double[] values)
    {
        return Buffer.CopyRange(fromMs, toMs, times, values);
    }

    public (long First, long Last)? TimeRange => Buffer.TimeRange();
}

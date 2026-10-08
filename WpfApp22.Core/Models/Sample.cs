namespace WpfApp22.Core.Models;

/// <summary>
/// 单个采样点。使用只读结构体，避免在高频采集路径上产生大量堆分配。
/// 时间戳统一使用 UTC 毫秒时间戳（long），比 DateTime 更省内存也更快比较。
/// </summary>
public readonly record struct Sample(long TimestampMs, double Value, SampleQuality Quality = SampleQuality.Good)
{
    public static Sample Bad(long timestampMs) => new(timestampMs, double.NaN, SampleQuality.Bad);

    public bool IsGood => Quality == SampleQuality.Good;

    public DateTimeOffset ToDateTime() => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs);
}

/// <summary>
/// 采集管道中流动的信封：把采样点带上它属于哪个通道、哪个点位。
/// </summary>
public readonly record struct SampleEnvelope(Guid ChannelId, Guid PointId, Sample Sample);

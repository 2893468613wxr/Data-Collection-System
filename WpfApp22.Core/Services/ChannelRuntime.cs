using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Models;

namespace WpfApp22.Core.Services;

/// <summary>
/// 单个通道的运行时状态。
///
/// 刻意不实现 INotifyPropertyChanged：通道状态每 100~500ms 才变化一次，
/// 而数值每秒变化几十次，若用数据绑定会让 WPF 的绑定引擎成为热点路径。
/// 这里改为 UI 侧定时器轮询拉取（状态 1Hz、曲线 30fps），吞吐可控且实现更简单。
/// </summary>
public sealed class ChannelRuntime
{
    private long _goodSamples;
    private long _badSamples;
    private long _lastRateTick;
    private long _lastRateCount;
    private double _lastRate;

    public ChannelRuntime(ChannelConfig config, IDeviceDriver driver)
    {
        Config = config;
        Driver = driver;
        _lastRateTick = Environment.TickCount64;
    }

    public ChannelConfig Config { get; }

    public IDeviceDriver Driver { get; }

    public ChannelState State { get; set; } = ChannelState.Idle;

    public string? LastError { get; set; }

    public DateTimeOffset? StartedAt { get; set; }

    public DateTimeOffset LastSampleAt { get; set; }

    public int ConsecutiveFailures { get; set; }

    /// <summary>是否已上报过通讯中断报警，避免断线期间反复刷同一条报警。</summary>
    public bool CommAlarmRaised { get; set; }

    /// <summary>
    /// 是否已上报过"设备已响应但读取失败"（功能码 / 地址不匹配）的提示性报警。
    /// 这类问题每轮都会复现，只提示一次，避免刷屏。
    /// </summary>
    public bool ConfigErrorRaised { get; set; }

    public long GoodSamples => Interlocked.Read(ref _goodSamples);

    public long BadSamples => Interlocked.Read(ref _badSamples);

    public long TotalSamples => GoodSamples + BadSamples;

    internal CancellationTokenSource? Cts { get; set; }

    internal Task? Worker { get; set; }

    public bool IsWorkerAlive => Worker is not null && !Worker.IsCompleted;

    public string StateText => State switch
    {
        ChannelState.Idle => "就绪",
        ChannelState.Connecting => "连接中",
        ChannelState.Running => "采集中",
        ChannelState.Reconnecting => "重连中",
        ChannelState.Faulted => "故障",
        ChannelState.Stopped => "已停止",
        _ => "未知"
    };

    public void MarkGood()
    {
        Interlocked.Increment(ref _goodSamples);
        LastSampleAt = DateTimeOffset.Now;
    }

    public void MarkBad()
    {
        Interlocked.Increment(ref _badSamples);
    }

    /// <summary>计算最近一段时间的实际采样率（点/秒）。</summary>
    public double CalculateRateHz()
    {
        var now = Environment.TickCount64;
        var count = TotalSamples;
        var dt = (now - _lastRateTick) / 1000.0;
        if (dt < 0.5) return _lastRate;

        _lastRate = (count - _lastRateCount) / dt;
        _lastRateTick = now;
        _lastRateCount = count;
        return _lastRate;
    }

    /// <summary>为通道下所有点位在数据总线上创建存储桶。</summary>
    public void EnsureStores(DataHub hub)
    {
        foreach (var point in Config.Points)
        {
            hub.GetOrCreate(Config.Id, Config.Name, point, Math.Max(1000, Config.BufferCapacity));
        }
    }
}

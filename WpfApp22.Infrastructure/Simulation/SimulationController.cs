namespace WpfApp22.Infrastructure.Simulation;

/// <summary>
/// 模拟器全局控制器。
///
/// 存在的意义：没有真实硬件时也能完整演示"报警触发 / 通讯中断 / 自动重连"，
/// 界面上可以一键注入故障率，用来验证系统的健壮性而不是靠嘴说。
/// </summary>
public sealed class SimulationController
{
    private double _faultRate;
    private bool _forceOffline;
    private double _noiseLevel = 1.0;

    private SimulationController() { }

    public static SimulationController Instance { get; } = new();

    /// <summary>随机通讯失败概率，0~1。</summary>
    public double FaultRate
    {
        get => _faultRate;
        set => _faultRate = Math.Clamp(value, 0, 1);
    }

    /// <summary>强制设备离线，用于演示断线报警与自动重连。</summary>
    public bool ForceOffline
    {
        get => _forceOffline;
        set => _forceOffline = value;
    }

    /// <summary>噪声强度倍率，0 表示完全平滑的正弦。</summary>
    public double NoiseLevel
    {
        get => _noiseLevel;
        set => _noiseLevel = Math.Max(0, value);
    }

    /// <summary>注入一次持续若干秒的故障，用于演示。</summary>
    public async Task InjectFaultAsync(double faultRate, int durationMs)
    {
        var previous = FaultRate;
        FaultRate = faultRate;
        try { await Task.Delay(durationMs).ConfigureAwait(false); }
        finally { FaultRate = previous; }
    }
}

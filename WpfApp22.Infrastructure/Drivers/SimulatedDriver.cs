using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Models;
using WpfApp22.Infrastructure.Simulation;

namespace WpfApp22.Infrastructure.Drivers;

/// <summary>
/// 内置模拟设备驱动。
///
/// 没有真实硬件时靠它把整套系统跑起来：多通道并发、曲线滚动、报警触发、
/// 故障注入与自动重连都能完整演示，答辩和演示不依赖现场设备。
/// 波形按寄存器地址派生相位与周期，保证每个点位曲线各不相同。
/// </summary>
public sealed class SimulatedDriver : IDeviceDriver
{
    private readonly Random _random = new(Environment.TickCount);
    private readonly DateTimeOffset _startedAt = DateTimeOffset.Now;

    private bool _connected;

    public ProtocolKind Protocol => ProtocolKind.Simulated;

    public bool IsConnected => _connected;

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        _connected = true;
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        _connected = false;
        return Task.CompletedTask;
    }

    public Task<PointReadResult> ReadPointAsync(
        ChannelConfig channel, PointDefinition point, CancellationToken cancellationToken = default)
    {
        var sim = SimulationController.Instance;

        if (!_connected)
            return Task.FromResult(PointReadResult.Fail("模拟器未启动"));

        if (sim.ForceOffline)
            return Task.FromResult(PointReadResult.Fail("模拟设备已离线"));

        if (_random.NextDouble() < sim.FaultRate)
            return Task.FromResult(PointReadResult.Fail("模拟通讯故障"));

        return Task.FromResult(PointReadResult.Ok(Generate(point)));
    }

    /// <summary>
    /// 生成一路仿真信号：正弦基线 + 噪声 + 偶发尖峰。
    /// 尖峰是有意保留的，用来演示报警引擎的去抖与恢复行为。
    /// </summary>
    private double Generate(PointDefinition point)
    {
        var t = (DateTimeOffset.Now - _startedAt).TotalSeconds;
        var seed = point.RegisterAddress;

        var period = 10.0 + seed % 7 * 4;
        var phase = seed * 0.9;
        var baseline = 25.0 + seed % 5 * 18;
        var amplitude = 8.0 + seed % 4 * 6;

        var noise = (_random.NextDouble() - 0.5) * 2 * SimulationController.Instance.NoiseLevel;
        var value = baseline + amplitude * Math.Sin(2 * Math.PI * t / period + phase) + noise;

        if (_random.NextDouble() < 0.0015)
        {
            value += 40 + _random.NextDouble() * 40;
        }

        return value;
    }

    public ValueTask DisposeAsync()
    {
        _connected = false;
        return ValueTask.CompletedTask;
    }
}

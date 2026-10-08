using WpfApp22.Core.Models;

namespace WpfApp22.Core.Abstractions;

/// <summary>一次点位读取的结果。</summary>
/// <param name="DeviceResponded">
/// 设备是否"回了话"。从站回了异常帧（非法功能码、非法地址…）时它也是 true ——
/// 这表示链路是通的、问题出在配置上。上层据此区分"链路故障（该重连）"与"配置不匹配（不该重连）"。
/// </param>
public readonly record struct PointReadResult(bool Success, double RawValue, string? Error, bool DeviceResponded = false)
{
    public static PointReadResult Ok(double raw) => new(true, raw, null);

    public static PointReadResult Fail(string error, bool deviceResponded = false) =>
        new(false, double.NaN, error, deviceResponded);
}

/// <summary>
/// 设备驱动：组合"传输 + 协议"，对外只暴露"读一个点位"。
/// 新增一种协议 = 新增一个编解码器，新增一种链路 = 新增一个传输实现，驱动本身不用改。
/// </summary>
public interface IDeviceDriver : IAsyncDisposable
{
    ProtocolKind Protocol { get; }

    bool IsConnected { get; }

    Task OpenAsync(CancellationToken cancellationToken = default);

    Task CloseAsync();

    /// <summary>读取单个点位的原始值（未经工程量变换）。</summary>
    Task<PointReadResult> ReadPointAsync(ChannelConfig channel, PointDefinition point, CancellationToken cancellationToken = default);
}

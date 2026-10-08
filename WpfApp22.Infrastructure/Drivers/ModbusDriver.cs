using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Models;

namespace WpfApp22.Infrastructure.Drivers;

/// <summary>
/// 通用设备驱动：把"传输 + 协议"组合成"读一个点位"。
///
/// 关键点：
/// 1. 用 SemaphoreSlim 保证同一条链路上的请求串行化 —— 串口和单连接 TCP 都不支持并发问答；
/// 2. 每次请求前清掉链路残留字节，避免上一次的迟到响应污染本次解析；
/// 3. 读取失败不抛异常，而是返回带错误描述的失败结果，由上层统一做重试和报警，
///    这样单台设备掉线不会影响其它通道。
/// </summary>
public sealed class ModbusDriver : IDeviceDriver
{
    private const int RxBufferSize = 512;

    private readonly ITransport _transport;
    private readonly IProtocolCodec _codec;
    private readonly byte[] _rx = new byte[RxBufferSize];
    private readonly SemaphoreSlim _gate = new(1, 1);

    private int _rxLength;

    public ModbusDriver(ITransport transport, IProtocolCodec codec)
    {
        _transport = transport;
        _codec = codec;
    }

    public ProtocolKind Protocol => _codec.Kind;

    public bool IsConnected => _transport.IsConnected;

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        await _transport.OpenAsync(cancellationToken).ConfigureAwait(false);
        ResetRx();
    }

    public async Task CloseAsync()
    {
        await _transport.CloseAsync().ConfigureAwait(false);
        ResetRx();
    }

    public async Task<PointReadResult> ReadPointAsync(ChannelConfig channel, PointDefinition point, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _transport.DiscardInputBuffer();
            ResetRx();

            var request = _codec.BuildReadRequest(
                channel.SlaveId, point.RegisterAddress, point.RegisterCount, point.RegisterKind);
            await _transport.WriteAsync(request, cancellationToken).ConfigureAwait(false);

            var frame = await ReadFrameAsync(cancellationToken).ConfigureAwait(false);
            if (frame is null)
                return PointReadResult.Fail("等待设备响应超时");

            if (frame.IsError)
                // 设备"回了话"但拒绝了这个请求（功能码不支持、地址越界…）。
                // 打上 deviceResponded 标记：上层据此知道链路是通的，不该去断线重连。
                return PointReadResult.Fail(frame.ErrorMessage ?? "设备返回异常", deviceResponded: true);

            if (channel.SlaveId != 0 && frame.SlaveId != channel.SlaveId)
                return PointReadResult.Fail($"从站地址不匹配（期望 {channel.SlaveId}，实际 {frame.SlaveId}）");

            var expectedFunction = point.RegisterKind.FunctionCode();
            if (frame.FunctionCode != expectedFunction)
                return PointReadResult.Fail(
                    $"响应功能码不匹配（请求 0x{expectedFunction:X2}，响应 0x{frame.FunctionCode:X2}）",
                    deviceResponded: true);

            double raw;
            if (frame.ScalarValue is not null)
            {
                raw = frame.ScalarValue.Value;
            }
            else if (point.IsBitPoint)
            {
                // 0x01 / 0x02：位值映射成 1 / 0，照样能走工程量变换、报警和曲线
                if (frame.Bits.Length == 0)
                    return PointReadResult.Fail("从站未返回位数据（0x01/0x02 响应为空）", deviceResponded: true);

                raw = frame.Bits[0] ? 1.0 : 0.0;
            }
            else
            {
                raw = RegisterDecoder.Decode(frame.Registers, point.DataType, point.WordOrder);
            }

            return double.IsNaN(raw)
                ? PointReadResult.Fail($"寄存器数量不足以解析该数据类型（需要 {point.RegisterCount} 个）", deviceResponded: true)
                : PointReadResult.Ok(raw);
        }
        catch (OperationCanceledException)
        {
            return PointReadResult.Fail($"读取超时（{channel.TimeoutMs}ms）");
        }
        catch (IOException ex)
        {
            return PointReadResult.Fail($"通讯异常：{ex.Message}");
        }
        catch (Exception ex)
        {
            return PointReadResult.Fail(ex.Message);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ProtocolFrame?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            if (_codec.TryExtractFrame(_rx, _rxLength, out var frame, out var consumed))
            {
                Consume(consumed);
                return frame;
            }

            // 协议层要求丢弃这一段（例如收到不支持的功能码），继续读下一帧
            if (consumed > 0)
            {
                Consume(consumed);
                continue;
            }

            if (_rxLength >= _rx.Length)
            {
                // 缓冲已满仍解析不出帧，说明链路已失步，整体丢弃重新同步
                ResetRx();
            }

            var read = await _transport.ReadAsync(_rx.AsMemory(_rxLength), cancellationToken).ConfigureAwait(false);
            if (read <= 0) return null;
            _rxLength += read;
        }

        return null;
    }

    private void Consume(int count)
    {
        if (count <= 0) return;
        Array.Copy(_rx, count, _rx, 0, _rxLength - count);
        _rxLength -= count;
    }

    private void ResetRx()
    {
        _rxLength = 0;
        Array.Clear(_rx);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        await _transport.DisposeAsync().ConfigureAwait(false);
        _gate.Dispose();
    }
}

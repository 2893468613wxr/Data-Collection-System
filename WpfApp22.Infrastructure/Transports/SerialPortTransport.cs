using System.Buffers.Binary;
using System.IO.Ports;
using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Models;

namespace WpfApp22.Infrastructure.Transports;

/// <summary>
/// 串口传输。把 Core 层与平台无关的串口参数枚举，映射到 System.IO.Ports 的具体类型。
/// 这样 Core 层始终不需要引用硬件相关的包。
/// </summary>
public sealed class SerialPortTransport : ITransport
{
    private readonly string _portName;
    private readonly int _baudRate;
    private readonly int _dataBits;
    private readonly SerialParity _parity;
    private readonly SerialStopBits _stopBits;
    private readonly int _readTimeoutMs;

    private SerialPort? _port;

    public SerialPortTransport(
        string portName,
        int baudRate,
        int dataBits,
        SerialParity parity,
        SerialStopBits stopBits,
        int readTimeoutMs = 1000)
    {
        _portName = portName;
        _baudRate = baudRate;
        _dataBits = dataBits;
        _parity = parity;
        _stopBits = stopBits;
        _readTimeoutMs = readTimeoutMs;
    }

    public bool IsConnected => _port is { IsOpen: true };

    public string DisplayName => _portName;

    public Task OpenAsync(CancellationToken cancellationToken = default)
    {
        var port = new SerialPort(_portName, _baudRate, MapParity(_parity), _dataBits, MapStopBits(_stopBits))
        {
            ReadTimeout = _readTimeoutMs,
            WriteTimeout = _readTimeoutMs,
            // 工业现场的 USB 转串口常常靠 DTR/RTS 给设备供电或唤醒
            DtrEnable = true,
            RtsEnable = true
        };

        port.Open();
        _port = port;
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        if (_port is null) return Task.CompletedTask;
        try { if (_port.IsOpen) _port.Close(); } catch { /* 关闭异常忽略 */ }
        _port.Dispose();
        _port = null;
        return Task.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var port = _port ?? throw new InvalidOperationException("串口未打开");
        return port.BaseStream.ReadAsync(buffer, cancellationToken);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var port = _port ?? throw new InvalidOperationException("串口未打开");
        return port.BaseStream.WriteAsync(data, cancellationToken);
    }

    public void DiscardInputBuffer()
    {
        try { _port?.DiscardInBuffer(); } catch { /* 忽略 */ }
    }

    public ValueTask DisposeAsync() => new(CloseAsync());

    private static Parity MapParity(SerialParity parity) => parity switch
    {
        SerialParity.Odd => Parity.Odd,
        SerialParity.Even => Parity.Even,
        SerialParity.Mark => Parity.Mark,
        SerialParity.Space => Parity.Space,
        _ => Parity.None
    };

    private static StopBits MapStopBits(SerialStopBits stopBits) => stopBits switch
    {
        SerialStopBits.OnePointFive => StopBits.OnePointFive,
        SerialStopBits.Two => StopBits.Two,
        _ => StopBits.One
    };
}

using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Models;
using WpfApp22.Infrastructure.Protocols;
using WpfApp22.Infrastructure.Transports;

namespace WpfApp22.Infrastructure.Drivers;

/// <summary>
/// 驱动工厂：把"传输方式 × 协议"映射成具体驱动。
/// 新增一种协议只需在这里加一个分支，上层采集服务完全不用改。
/// </summary>
public sealed class DeviceDriverFactory : IDeviceDriverFactory
{
    public IDeviceDriver Create(ChannelConfig config)
    {
        return config.Transport switch
        {
            TransportKind.SerialPort => CreateSerialDriver(config),
            TransportKind.Tcp => CreateTcpDriver(config),
            _ => new SimulatedDriver()
        };
    }

    private static IDeviceDriver CreateSerialDriver(ChannelConfig config)
    {
        var transport = new SerialPortTransport(
            config.PortName,
            config.BaudRate,
            config.DataBits,
            config.Parity,
            config.StopBits,
            config.TimeoutMs);

        IProtocolCodec codec = config.Protocol == ProtocolKind.AsciiLine
            ? new AsciiLineCodec()
            : new ModbusRtuCodec();

        return new ModbusDriver(transport, codec);
    }

    private static IDeviceDriver CreateTcpDriver(ChannelConfig config)
    {
        var transport = new TcpTransport(config.Host, config.Port, config.TimeoutMs);

        IProtocolCodec codec = config.Protocol switch
        {
            ProtocolKind.AsciiLine => new AsciiLineCodec(),
            ProtocolKind.ModbusRtu => new ModbusRtuCodec(),
            _ => new ModbusTcpCodec()
        };

        return new ModbusDriver(transport, codec);
    }
}

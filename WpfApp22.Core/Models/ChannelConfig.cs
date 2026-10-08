namespace WpfApp22.Core.Models;

/// <summary>
/// 一个"通道"对应一台物理设备（或一个 TCP 连接）。
/// 通道下挂多个点位（PointDefinition），采集时按点轮询。
/// </summary>
public sealed class ChannelConfig
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "新通道";

    public bool Enabled { get; set; } = true;

    public TransportKind Transport { get; set; } = TransportKind.Simulated;

    public ProtocolKind Protocol { get; set; } = ProtocolKind.Simulated;

    // ---- 串口参数 ----

    public string PortName { get; set; } = "COM1";

    public int BaudRate { get; set; } = 9600;

    public int DataBits { get; set; } = 8;

    public SerialParity Parity { get; set; } = SerialParity.None;

    public SerialStopBits StopBits { get; set; } = SerialStopBits.One;

    // ---- TCP 参数 ----

    public string Host { get; set; } = "127.0.0.1";

    public int Port { get; set; } = 502;

    // ---- 链路通用参数 ----

    /// <summary>从站地址（Modbus slave id）。</summary>
    public byte SlaveId { get; set; } = 1;

    /// <summary>轮询周期（毫秒）。这是采集频率的直接来源。</summary>
    public int PollIntervalMs { get; set; } = 200;

    /// <summary>单次读写超时（毫秒）。</summary>
    public int TimeoutMs { get; set; } = 1000;

    /// <summary>失败重试次数，超过则判定为通讯故障。</summary>
    public int RetryCount { get; set; } = 3;

    /// <summary>断线后是否自动重连，重连间隔采用指数退避。</summary>
    public bool AutoReconnect { get; set; } = true;

    /// <summary>单通道缓冲的历史点数上限（环形缓冲容量）。</summary>
    public int BufferCapacity { get; set; } = 200_000;

    public List<PointDefinition> Points { get; set; } = new();

    /// <summary>返回可读的连接描述，用于界面和日志。</summary>
    public string DescribeEndpoint() => Transport switch
    {
        TransportKind.SerialPort => $"{PortName} {BaudRate}/{DataBits}{(Parity == SerialParity.None ? 'N' : Parity.ToString()[0])}{(int)StopBits}",
        TransportKind.Tcp => $"{Host}:{Port}",
        _ => "内置模拟器"
    };
}

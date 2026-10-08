namespace WpfApp22.Core.Models;

/// <summary>物理传输方式。Core 层刻意不引用 System.IO.Ports，串口参数用自有枚举描述。</summary>
public enum TransportKind
{
    SerialPort,
    Tcp,
    Simulated
}

/// <summary>链路层之上的通讯协议。</summary>
public enum ProtocolKind
{
    ModbusRtu,
    ModbusTcp,
    AsciiLine,
    Simulated
}

/// <summary>
/// Modbus 数据区，同时决定"用哪个功能码去读"。
/// 枚举值刻意等于 Modbus 功能码，方便直接 (byte)kind 取值。
/// </summary>
public enum RegisterKind
{
    /// <summary>0x01 读线圈（可读可写的开关量，如继电器输出）。地址是位地址。</summary>
    Coil = 0x01,

    /// <summary>0x02 读离散输入（只读开关量，如限位开关）。地址是位地址。</summary>
    DiscreteInput = 0x02,

    /// <summary>0x03 读保持寄存器（最常用，可读可写的 16 位寄存器）。</summary>
    HoldingRegister = 0x03,

    /// <summary>0x04 读输入寄存器（只读模拟量，如变送器输入）。</summary>
    InputRegister = 0x04
}

/// <summary>寄存器原始数据的解释方式。</summary>
public enum ValueDataType
{
    UInt16,
    Int16,
    UInt32,
    Int32,
    Float32
}

/// <summary>多寄存器数据的高低字顺序（解决厂家字节序不一致的经典坑）。</summary>
public enum WordOrder
{
    /// <summary>高字在前，即 ABCD。</summary>
    HighWordFirst,
    /// <summary>低字在前，即 CDAB。</summary>
    LowWordFirst
}

/// <summary>串口校验位。Infrastructure 层负责映射到 System.IO.Ports.Parity。</summary>
public enum SerialParity
{
    None,
    Odd,
    Even,
    Mark,
    Space
}

public enum SerialStopBits
{
    One,
    OnePointFive,
    Two
}

/// <summary>通道生命周期状态。</summary>
public enum ChannelState
{
    Idle,
    Connecting,
    Running,
    Reconnecting,
    Faulted,
    Stopped
}

/// <summary>单个采样点的质量戳。工业上位机里"读失败"必须区别于"读到 0"。</summary>
public enum SampleQuality
{
    Good,
    Stale,
    Bad
}

/// <summary>报警严重度。</summary>
public enum AlarmSeverity
{
    Info,
    Warning,
    Critical
}

/// <summary>报警判定算子。</summary>
public enum AlarmOperator
{
    GreaterThan,
    LessThan,
    OutsideRange,
    InsideRange,
    /// <summary>变化速率超限，用于检测突变。</summary>
    RateOfChange
}

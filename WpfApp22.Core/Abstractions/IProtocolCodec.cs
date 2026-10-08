using WpfApp22.Core.Models;

namespace WpfApp22.Core.Abstractions;

/// <summary>一帧协议报文解析后的结果。</summary>
public sealed class ProtocolFrame
{
    public byte SlaveId { get; init; }

    public byte FunctionCode { get; init; }

    public bool IsError { get; init; }

    public string? ErrorMessage { get; init; }

    /// <summary>解析出的 16 位寄存器数组（0x03 / 0x04 响应）。</summary>
    public ushort[] Registers { get; init; } = Array.Empty<ushort>();

    /// <summary>
    /// 解析出的开关量数组（0x01 / 0x02 响应），按位序排列 —— Bits[0] 就是起始地址那一位。
    /// 与 Registers 二选一有值：读位时是 Bits，读寄存器时是 Registers。
    /// </summary>
    public bool[] Bits { get; init; } = Array.Empty<bool>();

    /// <summary>
    /// ASCII 类协议直接返回工程量数值时使用（例如 "TEMP=25.3"）。
    /// 有值时驱动直接使用它，无需再走寄存器解码。
    /// </summary>
    public double? ScalarValue { get; init; }
}

/// <summary>
/// 协议编解码器：负责"组请求报文"和"从字节流里切出一帧并校验"。
/// 传输层给字节流，协议层给语义，两者互不耦合 —— 这是支持多协议同时采集的关键。
/// </summary>
public interface IProtocolCodec
{
    ProtocolKind Kind { get; }

    /// <summary>构造一次读寄存器请求。</summary>
    byte[] BuildReadRequest(byte slaveId, ushort startAddress, ushort registerCount, RegisterKind kind);

    /// <summary>
    /// 从缓冲区中尝试提取一帧。
    /// </summary>
    /// <param name="buffer">累积的接收缓冲。</param>
    /// <param name="count">缓冲中的有效字节数。</param>
    /// <param name="frame">解析成功时输出报文。</param>
    /// <param name="consumed">解析成功后应从缓冲中移除的字节数。</param>
    /// <returns>缓冲中是否已包含一帧完整且校验通过的数据。</returns>
    bool TryExtractFrame(byte[] buffer, int count, out ProtocolFrame? frame, out int consumed);
}

/// <summary>把寄存器原始字数组按数据类型和字序解释成 double。</summary>
public static class RegisterDecoder
{
    public static double Decode(ushort[] registers, ValueDataType type, WordOrder order)
    {
        if (registers.Length == 0) return double.NaN;

        if (type is ValueDataType.UInt16)
            return registers[0];

        if (type is ValueDataType.Int16)
            return unchecked((short)registers[0]);

        if (registers.Length < 2)
            return double.NaN;

        // 统一成"高字在前"的排列，再按大端解释
        var hi = order == WordOrder.HighWordFirst ? registers[0] : registers[1];
        var lo = order == WordOrder.HighWordFirst ? registers[1] : registers[0];

        return type switch
        {
            ValueDataType.UInt32 => ((uint)hi << 16) | lo,
            ValueDataType.Int32 => unchecked((int)(((uint)hi << 16) | lo)),
            ValueDataType.Float32 => BitConverter.Int32BitsToSingle(unchecked((int)(((uint)hi << 16) | lo))),
            _ => double.NaN
        };
    }
}

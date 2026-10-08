using System.Buffers.Binary;
using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Models;

namespace WpfApp22.Infrastructure.Protocols;

/// <summary>
/// Modbus TCP 编解码。
///
/// 与 RTU 的区别在于多了 7 字节 MBAP 头（事务标识 + 协议标识 + 长度 + 单元标识），
/// 并且没有 CRC 校验（可靠性交给 TCP）。长度字段让帧边界判定变得非常干脆。
/// </summary>
public sealed class ModbusTcpCodec : IProtocolCodec
{
    private int _transactionId;

    public ProtocolKind Kind => ProtocolKind.ModbusTcp;

    public byte[] BuildReadRequest(byte slaveId, ushort startAddress, ushort registerCount, RegisterKind kind)
    {
        // 功能码由"数据区"决定：0x01 线圈 / 0x02 离散输入 / 0x03 保持寄存器 / 0x04 输入寄存器
        var func = kind.FunctionCode();
        var request = new byte[12];

        var tid = (ushort)Interlocked.Increment(ref _transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(0), tid);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), 0);      // 协议标识，固定 0
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), 6);     // 后续字节数：单元标识 1 + PDU 5
        request[6] = slaveId;                                            // 单元标识
        request[7] = func;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(8), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(10), registerCount);

        return request;
    }

    public bool TryExtractFrame(byte[] buffer, int count, out ProtocolFrame? frame, out int consumed)
    {
        frame = null;
        consumed = 0;
        if (count < 9) return false;

        // MBAP 头：事务标识(2) 协议标识(2) 长度(2) 单元标识(1)
        var protocolId = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2));
        var length = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4));
        if (protocolId != 0 || length is < 3 or > 260) return false;

        var total = 6 + length;
        if (count < total) return false;

        var slaveId = buffer[6];
        var func = buffer[7];

        if ((func & 0x80) != 0)
        {
            frame = new ProtocolFrame
            {
                SlaveId = slaveId,
                FunctionCode = func,
                IsError = true,
                ErrorMessage = DescribeException(buffer[8])
            };
            consumed = total;
            return true;
        }

        if (func is not (0x01 or 0x02 or 0x03 or 0x04))
        {
            // 不是我们期望的读响应，整段丢弃重新同步
            consumed = total;
            return false;
        }

        var byteCount = buffer[8];
        // 数据区真实长度由 MBAP 长度字段反推：total(6+len) - MBAP(6) - 单元标识(1) - 功能码(1) - 字节数(1)
        var dataLength = Math.Clamp(total - 9, 0, 255);
        if (byteCount > dataLength) byteCount = (byte)dataLength;

        // 0x01 / 0x02 回的是位图，展开成 bool[]；0x03 / 0x04 回的是寄存器字
        if (func is 0x01 or 0x02)
        {
            frame = new ProtocolFrame
            {
                SlaveId = slaveId,
                FunctionCode = func,
                Bits = ModbusBits.Unpack(buffer, 9, byteCount)
            };
            consumed = total;
            return true;
        }

        var registers = new ushort[byteCount / 2];
        for (var i = 0; i < registers.Length; i++)
        {
            registers[i] = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(9 + i * 2));
        }

        frame = new ProtocolFrame
        {
            SlaveId = slaveId,
            FunctionCode = func,
            Registers = registers
        };
        consumed = total;
        return true;
    }

    private static string DescribeException(byte code) => code switch
    {
        0x01 => "非法功能码（从站不支持该功能码，请把点位的功能码改成从站实际使用的 0x01/0x02/0x03/0x04）",
        0x02 => "非法数据地址（从站没有这个地址，注意 Modbus 地址是 0-based）",
        0x03 => "非法数据值",
        0x04 => "从站设备故障",
        0x05 => "确认（设备忙，需重试）",
        0x06 => "从站设备忙",
        0x0A => "网关路径不可用",
        0x0B => "网关目标设备无响应",
        _ => $"未知异常码 0x{code:X2}"
    };
}

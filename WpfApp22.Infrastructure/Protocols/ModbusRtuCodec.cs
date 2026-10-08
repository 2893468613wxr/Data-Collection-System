using System.Buffers.Binary;
using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Models;

namespace WpfApp22.Infrastructure.Protocols;

/// <summary>
/// Modbus RTU 编解码。
///
/// RTU 帧没有长度字段，帧边界靠 3.5 个字符的静默间隔判定，
/// 但在 PC 上读串口时这个间隔往往不可靠，因此这里采用更稳的做法：
/// 先按功能码推断出应有的帧长，再用 CRC16 校验确认，
/// 校验不通过就滑动一个字节继续找 —— 现场遇到粘包、残留字节都能自动重新同步。
/// </summary>
public sealed class ModbusRtuCodec : IProtocolCodec
{
    public ProtocolKind Kind => ProtocolKind.ModbusRtu;

    public byte[] BuildReadRequest(byte slaveId, ushort startAddress, ushort registerCount, RegisterKind kind)
    {
        // 功能码由"数据区"决定：0x01 线圈 / 0x02 离散输入 / 0x03 保持寄存器 / 0x04 输入寄存器
        var func = kind.FunctionCode();
        var request = new byte[8];
        request[0] = slaveId;
        request[1] = func;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(2), startAddress);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), registerCount);

        var crc = Crc16(request, 0, 6);
        request[6] = (byte)(crc & 0xFF);
        request[7] = (byte)(crc >> 8);
        return request;
    }

    public bool TryExtractFrame(byte[] buffer, int count, out ProtocolFrame? frame, out int consumed)
    {
        frame = null;
        consumed = 0;
        if (count < 5) return false;

        for (var start = 0; start + 5 <= count; start++)
        {
            var func = buffer[start + 1];
            int frameLength;

            if ((func & 0x80) != 0)
            {
                // 异常响应：地址 + 功能码 + 异常码 + CRC
                frameLength = 5;
            }
            else if (func is 0x01 or 0x02 or 0x03 or 0x04)
            {
                // 正常响应：地址 + 功能码 + 字节数 + 数据 + CRC
                // 0x01/0x02 的"字节数"是位数据的字节数，帧结构完全一样，所以长度公式通用
                if (start + 3 > count) return false;
                frameLength = 3 + buffer[start + 2] + 2;
            }
            else
            {
                continue;
            }

            if (start + frameLength > count) return false;

            if (Crc16(buffer, start, frameLength) != 0) continue;

            var slaveId = buffer[start];

            if ((func & 0x80) != 0)
            {
                frame = new ProtocolFrame
                {
                    SlaveId = slaveId,
                    FunctionCode = func,
                    IsError = true,
                    ErrorMessage = DescribeException(buffer[start + 2])
                };
                consumed = start + frameLength;
                return true;
            }

            var byteCount = buffer[start + 2];

            // 0x01 / 0x02 回的是位图（LSB 在前），展开成 bool[]
            if (func is 0x01 or 0x02)
            {
                frame = new ProtocolFrame
                {
                    SlaveId = slaveId,
                    FunctionCode = func,
                    Bits = ModbusBits.Unpack(buffer, start + 3, byteCount)
                };
                consumed = start + frameLength;
                return true;
            }

            var registers = new ushort[byteCount / 2];
            for (var i = 0; i < registers.Length; i++)
            {
                registers[i] = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(start + 3 + i * 2));
            }

            frame = new ProtocolFrame
            {
                SlaveId = slaveId,
                FunctionCode = func,
                Registers = registers
            };
            consumed = start + frameLength;
            return true;
        }

        return false;
    }

    /// <summary>Modbus CRC16，多项式 0xA001，初值 0xFFFF。</summary>
    public static ushort Crc16(byte[] data, int offset, int length)
    {
        ushort crc = 0xFFFF;
        for (var i = 0; i < length; i++)
        {
            crc ^= data[offset + i];
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? (ushort)((crc >> 1) ^ 0xA001) : (ushort)(crc >> 1);
            }
        }

        return crc;
    }

    private static string DescribeException(byte code) => code switch
    {
        0x01 => "非法功能码（从站不支持该功能码，请把点位的功能码改成从站实际使用的 0x01/0x02/0x03/0x04）",
        0x02 => "非法数据地址（从站没有这个地址，注意 Modbus 地址是 0-based）",
        0x03 => "非法数据值",
        0x04 => "从站设备故障",
        0x05 => "确认（设备忙，需重试）",
        0x06 => "从站设备忙",
        _ => $"未知异常码 0x{code:X2}"
    };
}

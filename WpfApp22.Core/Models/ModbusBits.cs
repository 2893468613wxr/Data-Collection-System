namespace WpfApp22.Core.Models;

/// <summary>
/// 0x01 / 0x02 响应里的位数据拆包。
///
/// Modbus 规定：读 N 个位时，从站返回 ceil(N/8) 个字节，
/// 第 0 个字节的最低位（LSB）对应起始地址那一位，之后依次往上排。
/// 这里统一展开成 bool[]，调用方按"第 i 位"取用，不用再关心字节位移。
/// </summary>
public static class ModbusBits
{
    public static bool[] Unpack(byte[] buffer, int offset, int byteCount)
    {
        var bits = new bool[byteCount * 8];
        for (var i = 0; i < bits.Length; i++)
        {
            bits[i] = (buffer[offset + (i >> 3)] & (1 << (i & 7))) != 0;
        }

        return bits;
    }

    /// <summary>反向打包，给自检程序里的迷你从站造响应用。</summary>
    public static byte[] Pack(bool[] bits)
    {
        var bytes = new byte[(bits.Length + 7) / 8];
        for (var i = 0; i < bits.Length; i++)
        {
            if (bits[i]) bytes[i >> 3] |= (byte)(1 << (i & 7));
        }

        return bytes;
    }
}

using System.Text;
using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Models;

namespace WpfApp22.Infrastructure.Protocols;

/// <summary>
/// 自定义 ASCII 行协议。很多国产仪表不用 Modbus，而是走简单的文本行，
/// 例如上位机发 "R100\r\n"，仪表回 "R100=25.37\r\n"。
/// 这里实现一个宽松的解析器：以换行分帧，取等号或逗号后的第一个可解析数值。
/// </summary>
public sealed class AsciiLineCodec : IProtocolCodec
{
    private const byte LineFeed = (byte)'\n';

    public ProtocolKind Kind => ProtocolKind.AsciiLine;

    public byte[] BuildReadRequest(byte slaveId, ushort startAddress, ushort registerCount, RegisterKind kind)
    {
        return Encoding.ASCII.GetBytes($"R{startAddress}\r\n");
    }

    public bool TryExtractFrame(byte[] buffer, int count, out ProtocolFrame? frame, out int consumed)
    {
        frame = null;
        consumed = 0;

        for (var i = 0; i < count; i++)
        {
            if (buffer[i] != LineFeed) continue;

            consumed = i + 1;
            var line = Encoding.ASCII.GetString(buffer, 0, i).Trim();

            var separatorIndex = line.IndexOfAny(new[] { '=', ',' });
            var payload = separatorIndex >= 0 ? line[(separatorIndex + 1)..] : line;

            if (double.TryParse(payload.Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var value))
            {
                frame = new ProtocolFrame
                {
                    FunctionCode = 0,
                    ScalarValue = value
                };
            }
            else
            {
                // 收到一行但解析不出数值：当作无效帧返回 false，同时把这一行消费掉
                frame = null;
            }

            return frame is not null;
        }

        return false;
    }
}

namespace WpfApp22.Core.Models;

/// <summary>
/// 一个"点位"对应设备上的一个寄存器（或协议里的一个字段）。
/// 点位是采集、绘图、报警、导出的最小单元。
/// </summary>
public sealed class PointDefinition
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; set; } = "新点位";

    /// <summary>工程量单位，例如 ℃ / MPa / rpm。</summary>
    public string Unit { get; set; } = string.Empty;

    /// <summary>寄存器起始地址。</summary>
    public ushort RegisterAddress { get; set; }

    /// <summary>该数据类型占用的寄存器数量。</summary>
    public ValueDataType DataType { get; set; } = ValueDataType.UInt16;

    public RegisterKind RegisterKind { get; set; } = RegisterKind.HoldingRegister;

    public WordOrder WordOrder { get; set; } = WordOrder.HighWordFirst;

    /// <summary>工程量线性变换：工程值 = 原始值 × Scale + Offset。</summary>
    public double Scale { get; set; } = 1.0;

    public double Offset { get; set; }

    public int DecimalPlaces { get; set; } = 2;

    /// <summary>曲线颜色（十六进制，如 #FF4FC3F7）。</summary>
    public string Color { get; set; } = "#FF4FC3F7";

    /// <summary>是否显示在实时曲线看板上。</summary>
    public bool VisibleOnChart { get; set; } = true;

    public AlarmRule? Alarm { get; set; }

    /// <summary>该点位在报文中请求的数据个数：位类型请求 1 个位，寄存器类型按数据类型占 1~2 个寄存器。</summary>
    public ushort RegisterCount =>
        RegisterKind.IsBitAccess()
            ? (ushort)1
            : (ushort)(DataType switch
            {
                ValueDataType.UInt16 or ValueDataType.Int16 => 1,
                ValueDataType.UInt32 or ValueDataType.Int32 or ValueDataType.Float32 => 2,
                _ => 1
            });

    /// <summary>是否是开关量点位（走 0x01 / 0x02）。</summary>
    public bool IsBitPoint => RegisterKind.IsBitAccess();

    /// <summary>实际使用的 Modbus 功能码文本，如 0x03。界面上用来确认"这个点位到底怎么读"。</summary>
    public string FunctionCodeText => RegisterKind.ShortCode();

    /// <summary>把协议解析出的原始值换算成工程量，并四舍五入到配置精度。</summary>
    public double ApplyTransform(double rawValue)
    {
        var v = rawValue * Scale + Offset;
        return DecimalPlaces >= 0 ? Math.Round(v, DecimalPlaces, MidpointRounding.AwayFromZero) : v;
    }

    public string FormatValue(double value) => value.ToString($"F{DecimalPlaces}");
}

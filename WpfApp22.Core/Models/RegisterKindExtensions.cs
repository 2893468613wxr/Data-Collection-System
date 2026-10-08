namespace WpfApp22.Core.Models;

/// <summary>
/// RegisterKind（数据区）与 Modbus 功能码之间的映射。
///
/// 为什么单独抽出来：组帧（编解码器）、解码（驱动）、界面显示（WPF）三处都要用同一套映射，
/// 各写一份 switch 迟早会出现"界面显示 0x04、实际发 0x03"这种最难查的错。
/// </summary>
public static class RegisterKindExtensions
{
    /// <summary>该数据区对应的 Modbus 读功能码。未知值兜底为 0x03，避免旧配置里出现 0 时组出非法帧。</summary>
    public static byte FunctionCode(this RegisterKind kind) => kind switch
    {
        RegisterKind.Coil => 0x01,
        RegisterKind.DiscreteInput => 0x02,
        RegisterKind.InputRegister => 0x04,
        _ => 0x03
    };

    /// <summary>是否是"位访问"（线圈 / 离散输入）。位访问按位编址，一次只读 1 位。</summary>
    public static bool IsBitAccess(this RegisterKind kind) =>
        kind is RegisterKind.Coil or RegisterKind.DiscreteInput;

    /// <summary>短功能码文本，如 0x03。用于主界面点位表这种窄列。</summary>
    public static string ShortCode(this RegisterKind kind) => $"0x{kind.FunctionCode():X2}";

    /// <summary>带中文说明的完整名称，用于配置界面的下拉框。</summary>
    public static string DisplayName(this RegisterKind kind) => kind switch
    {
        RegisterKind.Coil => "0x01 线圈 Coil（读写开关量）",
        RegisterKind.DiscreteInput => "0x02 离散输入 Input Status（只读开关量）",
        RegisterKind.HoldingRegister => "0x03 保持寄存器 Holding（读写）",
        RegisterKind.InputRegister => "0x04 输入寄存器 Input（只读）",
        _ => $"0x{kind.FunctionCode():X2} 未知数据区"
    };

    /// <summary>位数据的单位，位访问时是"位"，寄存器访问时是"字"。</summary>
    public static string UnitName(this RegisterKind kind) => kind.IsBitAccess() ? "位" : "寄存器";
}

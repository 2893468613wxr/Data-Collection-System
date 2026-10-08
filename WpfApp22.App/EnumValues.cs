using WpfApp22.Core.Models;

namespace WpfApp22.App;

/// <summary>
/// 供 XAML 直接以 x:Static 引用的枚举集合。
/// DataGridComboBoxColumn 不在可视化树中，拿不到窗口的 DataContext，
/// 因此枚举选项必须以静态属性的形式提供。
/// </summary>
public static class EnumValues
{
    public static Array DataTypes { get; } = Enum.GetValues<ValueDataType>();

    public static Array WordOrders { get; } = Enum.GetValues<WordOrder>();

    /// <summary>
    /// 功能码下拉选项。
    /// 直接用枚举名的话界面上只能看到 Coil / DiscreteInput 这种英文，
    /// 现场调试时真正要对的是"从站软件里选的 01/02/03/04"，所以这里带上功能码和中文说明。
    /// </summary>
    public static IReadOnlyList<RegisterKindOption> RegisterKindOptions { get; } =
    [
        new(RegisterKind.Coil, RegisterKind.Coil.DisplayName()),
        new(RegisterKind.DiscreteInput, RegisterKind.DiscreteInput.DisplayName()),
        new(RegisterKind.HoldingRegister, RegisterKind.HoldingRegister.DisplayName()),
        new(RegisterKind.InputRegister, RegisterKind.InputRegister.DisplayName())
    ];

    public static Array AlarmOperators { get; } = Enum.GetValues<AlarmOperator>();

    public static Array AlarmSeverities { get; } = Enum.GetValues<AlarmSeverity>();
}

/// <summary>功能码下拉框的一项：显示文本 + 对应的枚举值。</summary>
public sealed record RegisterKindOption(RegisterKind Value, string Text);

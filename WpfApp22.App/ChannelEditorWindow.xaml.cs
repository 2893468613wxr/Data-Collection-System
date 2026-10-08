using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using WpfApp22.Core.Models;

namespace WpfApp22.App;

/// <summary>
/// 通道与点位配置窗口。
/// 直接编辑领域模型对象（ChannelConfig / PointDefinition），编辑完成才写回，
/// 取消则不影响正在运行的采集。
/// </summary>
public partial class ChannelEditorWindow : Window
{
    private static readonly string[] Palette =
    [
        "#FF4FC3F7", "#FF3FB950", "#FFD29922", "#FFBC8F8F",
        "#FF79C0FF", "#FFFFA657", "#FFA5D6FF", "#FFF85149"
    ];

    public ChannelEditorWindow(ChannelConfig config)
    {
        InitializeComponent();

        Config = config;
        Points = new ObservableCollection<PointDefinition>(config.Points);

        // 报警规则为 null 时界面上的报警列无法绑定，这里补齐一个默认关闭的规则
        foreach (var point in Points)
        {
            point.Alarm ??= new AlarmRule();
        }

        // 工具栏上的"默认功能码"取当前点位里用得最多的那个：
        // 新建点位和"应用到全部点位"都以它为准，省得一排点位逐个改
        DefaultRegisterKind = Points.Count == 0
            ? RegisterKind.HoldingRegister
            : Points.GroupBy(p => p.RegisterKind).MaxBy(g => g.Count())!.Key;

        AddPointCommand = new RelayCommand(AddPoint);
        RemovePointCommand = new RelayCommand(RemovePoint);
        ApplyKindToAllCommand = new RelayCommand(ApplyKindToAll);

        DataContext = this;
    }

    public ChannelConfig Config { get; }

    public ObservableCollection<PointDefinition> Points { get; }

    public PointDefinition? SelectedPoint { get; set; }

    /// <summary>工具栏上的默认功能码：新建点位继承它，"应用到全部点位"也用它。</summary>
    public RegisterKind DefaultRegisterKind { get; set; }

    public IRelayCommand AddPointCommand { get; }

    public IRelayCommand RemovePointCommand { get; }

    public IRelayCommand ApplyKindToAllCommand { get; }

    public Array TransportKinds { get; } = Enum.GetValues<TransportKind>();

    public Array ProtocolKinds { get; } = Enum.GetValues<ProtocolKind>();

    public Array SerialParities { get; } = Enum.GetValues<SerialParity>();

    public Array SerialStopBitsValues { get; } = Enum.GetValues<SerialStopBits>();

    private void AddPoint()
    {
        var nextAddress = Points.Count == 0 ? 0 : Points.Max(p => p.RegisterAddress) + 1;
        Points.Add(new PointDefinition
        {
            Name = $"点位{Points.Count + 1}",
            RegisterAddress = (ushort)nextAddress,
            RegisterKind = DefaultRegisterKind,
            Color = Palette[Points.Count % Palette.Length],
            Alarm = new AlarmRule()
        });
    }

    private void RemovePoint()
    {
        if (SelectedPoint is null) return;
        Points.Remove(SelectedPoint);
    }

    /// <summary>
    /// 把工具栏选中的功能码套到本通道所有点位上。
    /// Modbus Slave 这类从站模拟器一个窗口只有一种功能码，批量切换是最常用的操作。
    /// </summary>
    private void ApplyKindToAll()
    {
        if (Points.Count == 0) return;

        foreach (var point in Points)
        {
            point.RegisterKind = DefaultRegisterKind;
        }

        // PointDefinition 是领域模型，刻意不实现 INotifyPropertyChanged（采集热路径上不该有通知开销），
        // 所以这里靠重挂 ItemsSource 强制整表重绘，让下拉列立刻显示新值。
        PointGrid.ItemsSource = null;
        PointGrid.ItemsSource = Points;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        Config.Points = [.. Points];
        DialogResult = true;
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}

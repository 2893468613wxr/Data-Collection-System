using CommunityToolkit.Mvvm.ComponentModel;
using WpfApp22.Core.Models;
using WpfApp22.Core.Services;

namespace WpfApp22.App.ViewModels;

/// <summary>
/// 通道在界面上的一行。
/// Core 层的 ChannelRuntime 不带属性通知（避免高频通知开销），
/// 由这一层包装后按固定节拍批量刷新 —— 每秒一次对 WPF 来说毫无压力。
/// </summary>
public partial class ChannelRow : ObservableObject
{
    public ChannelRow(ChannelRuntime runtime)
    {
        Runtime = runtime;
    }

    public ChannelRuntime Runtime { get; }

    public Guid Id => Runtime.Config.Id;

    public string Name => Runtime.Config.Name;

    public string Endpoint => Runtime.Config.DescribeEndpoint();

    public string ProtocolText => Runtime.Config.Transport switch
    {
        TransportKind.SerialPort => $"串口 · {Runtime.Config.Protocol}",
        TransportKind.Tcp => $"TCP · {Runtime.Config.Protocol}",
        _ => "内置模拟器"
    };

    public ChannelState State => Runtime.State;

    public string StateText => Runtime.StateText;

    public double RateHz => Runtime.CalculateRateHz();

    public long TotalSamples => Runtime.TotalSamples;

    public long BadSamples => Runtime.BadSamples;

    public string ToggleText => Runtime.State == ChannelState.Running ? "停止" : "启动";

    public string Detail => Runtime.LastError is { } error
        ? $"最近错误：{error}"
        : $"{RateHz:F1} 点/秒 · 累计 {TotalSamples} 点";

    /// <summary>通知界面该行所有显示项已变化。</summary>
    public void Refresh() => OnPropertyChanged(string.Empty);
}

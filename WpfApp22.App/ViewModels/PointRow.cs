using CommunityToolkit.Mvvm.ComponentModel;
using WpfApp22.Core.Models;
using WpfApp22.Core.Services;

namespace WpfApp22.App.ViewModels;

/// <summary>点位实时值表格的一行。</summary>
public partial class PointRow : ObservableObject
{
    public PointRow(SeriesStore store)
    {
        Store = store;
    }

    public SeriesStore Store { get; }

    /// <summary>所属通道（设备）Id，用于左侧设备列表筛选。</summary>
    public Guid ChannelId => Store.ChannelId;

    public string ChannelName => Store.ChannelName;

    public string Name => Store.Definition.Name;

    public string Unit => Store.Definition.Unit;

    /// <summary>「0x03 · 12」这样的访问方式：一眼能看出这个点位是用哪个功能码、从哪个地址读的。</summary>
    public string AccessText => $"{Store.Definition.FunctionCodeText} · {Store.Definition.RegisterAddress}";

    public SampleQuality Quality => Store.LastQuality;

    public string StateText => Store.LastQuality switch
    {
        SampleQuality.Good => "正常",
        SampleQuality.Stale => "陈旧",
        _ => "通讯失败"
    };

    public string ValueText => double.IsNaN(Store.LastValue)
        ? "--"
        : Store.Definition.FormatValue(Store.LastValue);

    public string LimitText => Store.Definition.Alarm is { Enabled: true } rule
        ? rule.DescribeLimit()
        : "—";

    public void Refresh() => OnPropertyChanged(string.Empty);
}

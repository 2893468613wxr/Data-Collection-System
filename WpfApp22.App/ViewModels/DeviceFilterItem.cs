using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using WpfApp22.Core.Models;

namespace WpfApp22.App.ViewModels;

/// <summary>
/// 「点位实时值」左侧设备列表里的一项。
///
/// Channel 为 null 时代表"全部设备"，这样列表永远有一项可回退的选项，
/// 操作员不会因为选中了某个设备后该设备被删除而无处可去。
/// </summary>
public partial class DeviceFilterItem : ObservableObject
{
    public DeviceFilterItem(ChannelRow? channel)
    {
        Channel = channel;
    }

    public ChannelRow? Channel { get; }

    public Guid? ChannelId => Channel?.Id;

    public bool IsAll => Channel is null;

    public string Name => Channel?.Name ?? "全部设备";

    public ChannelState State => Channel?.State ?? ChannelState.Stopped;

    /// <summary>
    /// "全部设备"没有自己的运行状态，圆点必须藏起来 ——
    /// 否则它会显示成一个灰色停止灯，让人误以为所有设备都停了。
    /// </summary>
    public bool HasState => !IsAll;

    public string ToolTip => Channel is null
        ? $"显示所有设备的点位\n共 {PointCount} 个点位"
        : $"{Channel.Name}\n{Channel.Endpoint}\n状态：{Channel.StateText}\n点位：{PointCount} 个"
          + (AlarmCount > 0 ? $"\n活动报警：{AlarmCount} 条" : string.Empty);

    /// <summary>该设备的点位总数，随配置变化刷新。</summary>
    [ObservableProperty] private int _pointCount;

    /// <summary>该设备当前处于活动状态的报警条数，0 时不显示角标。</summary>
    [ObservableProperty] private int _alarmCount;

    public bool HasAlarm => AlarmCount > 0;

    public Visibility AlarmBadgeVisibility => HasAlarm ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// 每秒一次的批量刷新。状态、名称、数量都会变，
    /// 但刻意不用 OnPropertyChanged(string.Empty)：那会连带刷新 ToolTip 绑定，
    /// 鼠标悬停时每秒重算一次字符串没必要。
    /// </summary>
    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(ToolTip));
        OnPropertyChanged(nameof(HasAlarm));
        OnPropertyChanged(nameof(AlarmBadgeVisibility));
    }
}

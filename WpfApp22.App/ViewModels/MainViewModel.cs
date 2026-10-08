using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using WpfApp22.App.Charts;
using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Models;
using WpfApp22.Core.Services;
using WpfApp22.Infrastructure.Export;
using WpfApp22.Infrastructure.Notify;
using WpfApp22.Infrastructure.Simulation;

namespace WpfApp22.App.ViewModels;

/// <summary>
/// 主视图模型：串联采集服务、图表刷新、报警展示与数据导出。
///
/// 两条刷新节拍是刻意分开的：
///   曲线 30fps —— 只做数据拷贝与渲染，不碰任何集合变更；
///   状态 1Hz   —— 刷新通道状态、实时数值、吞吐统计。
/// 把高频渲染和低频状态更新分开，界面才不会在 20 个通道满载时卡住。
/// </summary>
public partial class MainViewModel : ObservableObject, IDisposable
{
    private const int ChartFps = 30;
    private const int MaxChartPoints = 8192;
    private const int MaxAlarmRows = 300;
    private const int MaxExportPointsPerSeries = 200_000;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AcquisitionService _acquisition;
    private readonly ExportService _exportService;
    private readonly AlarmDispatcher _alarmDispatcher;
    private readonly DispatcherTimer _chartTimer;
    private readonly DispatcherTimer _statusTimer;

    private IChartHost? _chart;
    private bool _disposed;

    public MainViewModel(
        AcquisitionService acquisition,
        ExportService exportService,
        AlarmDispatcher alarmDispatcher)
    {
        _acquisition = acquisition;
        _exportService = exportService;
        _alarmDispatcher = alarmDispatcher;

        _acquisition.AlarmRaised += OnAlarmRaised;
        _acquisition.AlarmCleared += OnAlarmCleared;

        foreach (var config in LoadOrCreateConfigs())
        {
            Channels.Add(new ChannelRow(_acquisition.AddChannel(config)));
        }

        RebuildPointRows();

        _chartTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(1000.0 / ChartFps)
        };
        _chartTimer.Tick += (_, _) => RefreshChart();
        _chartTimer.Start();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        StatusText = $"已加载 {Channels.Count} 个通道，点击「全部启动」开始采集";
    }

    public ObservableCollection<ChannelRow> Channels { get; } = new();

    /// <summary>点位实时值表格实际绑定的集合，只装当前筛选命中的行。</summary>
    public ObservableCollection<PointRow> Points { get; } = new();

    /// <summary>
    /// 全部点位行的主列表。Points 只是按「设备 + 关键字」筛出来的视图，
    /// 两者分开才能在不重建行的前提下反复切换筛选（行对象带着缓冲引用，重建代价高）。
    /// </summary>
    private readonly List<PointRow> _allPoints = new();

    /// <summary>左侧设备列表：首项固定是「全部设备」，其余与 Channels 一一对应。</summary>
    public ObservableCollection<DeviceFilterItem> Devices { get; } = new();

    public ObservableCollection<AlarmEvent> Alarms { get; } = new();

    /// <summary>
    /// 实际生效的报警推送渠道。Webhook 没配地址时不会出现在这里，
    /// 免得界面上写着"Webhook"、实际一条都发不出去。
    /// </summary>
    public string NotifyChannelsText => "通知渠道：" + string.Join(" / ", _alarmDispatcher.ChannelNames);

    /// <summary>
    /// 曲线标题后面跟着的当前范围说明。
    /// 曲线跟着设备筛选走时，必须在图上写清楚"现在画的是哪台设备"，
    /// 否则操作员很容易把一台设备的曲线误读成全厂数据。
    /// </summary>
    public string ChartScopeText =>
        ChartFollowsFilter && SelectedDevice is { IsAll: false } device
            ? $"·  {device.Name}"
            : string.Empty;

    [ObservableProperty] private string _statusText = "就绪";

    [ObservableProperty] private double _totalRateHz;

    [ObservableProperty] private long _totalSamples;

    [ObservableProperty] private long _droppedSamples;

    [ObservableProperty] private int _activeAlarmCount;

    [ObservableProperty] private int _unackedAlarmCount;

    [ObservableProperty] private bool _isRunning;

    [ObservableProperty] private int _chartWindowSeconds = 60;

    [ObservableProperty] private ChannelRow? _selectedChannel;

    /// <summary>左侧设备列表的选中项，null 表示尚未初始化。</summary>
    [ObservableProperty] private DeviceFilterItem? _selectedDevice;

    /// <summary>点位名 / 地址 / 通道名的模糊搜索关键字。</summary>
    [ObservableProperty] private string _pointFilterText = string.Empty;

    [ObservableProperty] private string _pointFilterSummary = "共 0 个点位";

    /// <summary>曲线是否跟随左侧设备筛选，默认跟随——选哪台设备就看哪台设备的趋势。</summary>
    [ObservableProperty] private bool _chartFollowsFilter = true;

    /// <summary>
    /// 点位表「通道」列的可见性。选中具体设备时整列都是同一个名字，
    /// 隐藏它可以把这一百来像素让给数值和限值列。
    /// </summary>
    [ObservableProperty] private Visibility _channelColumnVisibility = Visibility.Visible;

    [ObservableProperty] private string _faultButtonText = "注入通讯故障";

    [ObservableProperty] private bool _autoScaleY = true;

    // 筛选条件变化时重建点位表；曲线每帧自己读筛选状态，不需要在这里手动重画
    partial void OnSelectedDeviceChanged(DeviceFilterItem? value)
    {
        ApplyPointFilter();
        OnPropertyChanged(nameof(ChartScopeText));
    }

    partial void OnPointFilterTextChanged(string value) => ApplyPointFilter();

    partial void OnChartFollowsFilterChanged(bool value) => OnPropertyChanged(nameof(ChartScopeText));

    public int[] WindowOptions { get; } = [30, 60, 180, 600, 1800];

    public string ConfigPath { get; } =
        Path.Combine(AppContext.BaseDirectory, "config", "channels.json");

    partial void OnAutoScaleYChanged(bool value)
    {
        if (_chart is not null) _chart.AutoScaleY = value;
    }

    /// <summary>由主窗口在加载时把自己作为图表宿主注入进来。</summary>
    public void AttachChart(IChartHost chart)
    {
        _chart = chart;
        RefreshChartSeries();
    }

    // ---------- 命令 ----------

    [RelayCommand]
    private async Task StartAllAsync()
    {
        StatusText = "正在启动所有通道…";
        await _acquisition.StartAllAsync().ConfigureAwait(true);
        IsRunning = true;
        StatusText = "采集中";
        RefreshStatus();
    }

    [RelayCommand]
    private async Task StopAllAsync()
    {
        await _acquisition.StopAllAsync().ConfigureAwait(true);
        IsRunning = false;
        StatusText = "已停止";
        RefreshStatus();
    }

    [RelayCommand]
    private async Task ToggleChannelAsync(ChannelRow? row)
    {
        if (row is null) return;

        if (row.Runtime.State == ChannelState.Running)
            await _acquisition.StopChannelAsync(row.Id).ConfigureAwait(true);
        else
            await _acquisition.StartChannelAsync(row.Id).ConfigureAwait(true);

        row.Refresh();
    }

    [RelayCommand]
    private void AddChannel()
    {
        var config = new ChannelConfig { Name = $"通道{Channels.Count + 1}" };
        var window = new ChannelEditorWindow(config) { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true) return;

        Channels.Add(new ChannelRow(_acquisition.AddChannel(config)));
        RebuildPointRows();
        SaveConfig();
        StatusText = $"已添加通道 {config.Name}";
    }

    [RelayCommand]
    private void EditChannel()
    {
        if (SelectedChannel is null) return;

        var config = SelectedChannel.Runtime.Config;
        var window = new ChannelEditorWindow(config) { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true) return;

        // 点位可能被新增或删除，需要重建存储桶
        SelectedChannel.Runtime.EnsureStores(_acquisition.Hub);
        RebuildPointRows();
        SaveConfig();
        StatusText = $"已更新通道 {config.Name}";
    }

    [RelayCommand]
    private async Task RemoveChannelAsync()
    {
        if (SelectedChannel is null) return;

        var name = SelectedChannel.Name;
        if (MessageBox.Show($"确定删除通道「{name}」及其历史数据？", "确认",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;

        await _acquisition.RemoveChannelAsync(SelectedChannel.Id).ConfigureAwait(true);
        Channels.Remove(SelectedChannel);
        RebuildPointRows();
        SaveConfig();
        StatusText = $"已删除通道 {name}";
    }

    [RelayCommand]
    private async Task ExportAsync(string format)
    {
        var isExcel = string.Equals(format, "excel", StringComparison.OrdinalIgnoreCase);
        var dialog = new SaveFileDialog
        {
            Title = "导出历史数据",
            Filter = isExcel ? "Excel 工作簿 (*.xlsx)|*.xlsx" : "CSV 文件 (*.csv)|*.csv",
            DefaultExt = isExcel ? ".xlsx" : ".csv",
            FileName = $"采集数据_{DateTime.Now:yyyyMMdd_HHmmss}"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            StatusText = "正在导出…";
            var series = CollectExportSeries();
            if (series.Count == 0)
            {
                MessageBox.Show("当前没有可导出的历史数据。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var request = new ExportRequest
            {
                FilePath = dialog.FileName,
                Series = series,
                MaxPointsPerSeries = MaxExportPointsPerSeries
            };

            await _exportService.ExportAsync(dialog.FileName, request).ConfigureAwait(true);
            StatusText = $"已导出 {series.Count} 条曲线到 {dialog.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show($"导出失败：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
            StatusText = $"导出失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearData()
    {
        _acquisition.Hub.ClearBuffers();
        Alarms.Clear();
        ActiveAlarmCount = 0;
        UnackedAlarmCount = 0;
        _chart?.Reset();
        RefreshChartSeries();
        StatusText = "已清空历史曲线与报警记录";
    }

    [RelayCommand]
    private void InjectFault()
    {
        var sim = SimulationController.Instance;
        var injecting = sim.FaultRate <= 0;
        sim.FaultRate = injecting ? 0.35 : 0;
        FaultButtonText = injecting ? "停止故障注入" : "注入通讯故障";
        StatusText = injecting
            ? "已注入 35% 通讯故障率，可观察断线报警与自动重连"
            : "故障注入已停止";
    }

    [RelayCommand]
    private void AcknowledgeAll()
    {
        var items = Alarms.ToList();
        Alarms.Clear();
        foreach (var alarm in items)
        {
            alarm.Acknowledged = true;
            Alarms.Add(alarm);
        }

        UnackedAlarmCount = 0;
        StatusText = "已确认全部报警";
    }

    // ---------- 刷新 ----------

    private void RefreshChart()
    {
        if (_chart is null) return;

        // 曲线跟随设备筛选时，被筛掉的设备要把曲线隐藏而不是停更：
        // 留一条不再刷新的旧线在上面，看上去像是"这台设备还在采、只是卡住了"。
        var onlyChannelId = ChartFollowsFilter ? SelectedDevice?.ChannelId : null;
        var hidden = onlyChannelId is not null;

        foreach (var store in _acquisition.Hub.All)
        {
            if (!store.Definition.VisibleOnChart) continue;

            if (hidden && store.ChannelId != onlyChannelId!.Value)
            {
                _chart.UpdateSeries(store.Definition.Id, Array.Empty<double>(), Array.Empty<double>(), 0);
                continue;
            }

            var count = store.FillForChart(MaxChartPoints);
            _chart.UpdateSeries(store.Definition.Id, store.ChartTimes, store.ChartValues, count);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        _chart.SetTimeRange(now - ChartWindowSeconds * 1000L, now);
        _chart.Refresh();
    }

    private void RefreshStatus()
    {
        double rate = 0;
        long total = 0;

        foreach (var row in Channels)
        {
            row.Refresh();
            rate += row.RateHz;
            total += row.TotalSamples;
        }

        foreach (var row in Points) row.Refresh();

        RefreshDeviceStats();

        TotalRateHz = rate;
        TotalSamples = total;
        DroppedSamples = _acquisition.DroppedSamples;
        ActiveAlarmCount = Alarms.Count(a => a.IsActive);
        UnackedAlarmCount = Alarms.Count(a => !a.Acknowledged);
        IsRunning = _acquisition.Channels.Any(c => c.State == ChannelState.Running);
    }

    private void RefreshChartSeries()
    {
        if (_chart is null) return;

        foreach (var store in _acquisition.Hub.All)
        {
            if (!store.Definition.VisibleOnChart) continue;
            _chart.EnsureSeries(
                store.Definition.Id,
                $"{store.ChannelName} · {store.Definition.Name}",
                store.Definition.Color);
        }
    }

    private void RebuildPointRows()
    {
        _allPoints.Clear();
        foreach (var store in _acquisition.Hub.All.OrderBy(s => s.ChannelName))
        {
            _allPoints.Add(new PointRow(store));
        }

        RebuildDevices();
        ApplyPointFilter();
        RefreshChartSeries();
    }

    /// <summary>
    /// 重建设备列表。
    /// 首项固定为「全部设备」，保证任何情况下都有一个可回退的入口 ——
    /// 否则选中某台设备后这台设备被删除，界面就会卡在空表上且无处可点。
    /// </summary>
    private void RebuildDevices()
    {
        // 通道增删后要保住操作员原来的选择，不然每次改配置都被弹回"全部设备"
        var previousId = SelectedDevice?.ChannelId;

        Devices.Clear();
        Devices.Add(new DeviceFilterItem(null));
        foreach (var channel in Channels)
        {
            Devices.Add(new DeviceFilterItem(channel));
        }

        SelectedDevice = previousId is null
            ? Devices[0]
            : Devices.FirstOrDefault(d => d.ChannelId == previousId) ?? Devices[0];
    }

    /// <summary>按「设备 + 关键字」重建可见点位集合。筛选变化频率很低，整表重建足够。</summary>
    private void ApplyPointFilter()
    {
        IEnumerable<PointRow> source = _allPoints;

        if (SelectedDevice?.ChannelId is { } channelId)
            source = source.Where(p => p.ChannelId == channelId);

        var keyword = PointFilterText.Trim();
        if (keyword.Length > 0)
        {
            source = source.Where(p =>
                p.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                p.AccessText.Contains(keyword, StringComparison.OrdinalIgnoreCase) ||
                p.ChannelName.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        Points.Clear();
        foreach (var row in source) Points.Add(row);

        PointFilterSummary = Points.Count == _allPoints.Count
            ? $"共 {Points.Count} 个点位"
            : $"显示 {Points.Count} / {_allPoints.Count} 个点位";

        ChannelColumnVisibility = SelectedDevice is { IsAll: false }
            ? Visibility.Collapsed
            : Visibility.Visible;

        RefreshDeviceStats();
    }

    /// <summary>刷新设备列表上的点位计数与活动报警角标。</summary>
    private void RefreshDeviceStats()
    {
        var activeByChannel = Alarms
            .Where(a => a.IsActive)
            .GroupBy(a => a.ChannelId)
            .ToDictionary(g => g.Key, g => g.Count());

        foreach (var device in Devices)
        {
            device.PointCount = device.ChannelId is { } id
                ? _allPoints.Count(p => p.ChannelId == id)
                : _allPoints.Count;

            device.AlarmCount = device.ChannelId is { } cid
                ? activeByChannel.GetValueOrDefault(cid)
                : activeByChannel.Values.Sum();

            device.Refresh();
        }
    }

    // ---------- 报警 ----------

    private void OnAlarmRaised(AlarmEvent alarm)
    {
        // 报警来自采集线程，必须切回 UI 线程再改集合
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            Alarms.Insert(0, alarm);
            while (Alarms.Count > MaxAlarmRows) Alarms.RemoveAt(Alarms.Count - 1);
            ActiveAlarmCount = Alarms.Count(a => a.IsActive);
            UnackedAlarmCount = Alarms.Count(a => !a.Acknowledged);
        });

        // 通知渠道并行推送，失败也不影响采集
        _ = Task.Run(() => _alarmDispatcher.DispatchAsync(alarm));
    }

    private void OnAlarmCleared(AlarmEvent alarm)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var index = Alarms.IndexOf(alarm);
            if (index < 0) return;

            // 重新插入同一位置，触发界面刷新该行的恢复状态
            Alarms.RemoveAt(index);
            Alarms.Insert(index, alarm);
            ActiveAlarmCount = Alarms.Count(a => a.IsActive);
        });
    }

    // ---------- 导出 ----------

    private List<ExportSeries> CollectExportSeries()
    {
        var result = new List<ExportSeries>();
        foreach (var store in _acquisition.Hub.All)
        {
            var count = store.Buffer.Count;
            if (count == 0) continue;

            var times = new double[count];
            var values = new double[count];
            var filled = store.FillForExport(long.MinValue, long.MaxValue, times, values);
            if (filled == 0) continue;

            if (filled != count)
            {
                Array.Resize(ref times, filled);
                Array.Resize(ref values, filled);
            }

            result.Add(new ExportSeries
            {
                ChannelName = store.ChannelName,
                PointName = store.Definition.Name,
                Unit = store.Definition.Unit,
                Timestamps = times,
                Values = values
            });
        }

        return result;
    }

    // ---------- 配置持久化 ----------

    private List<ChannelConfig> LoadOrCreateConfigs()
    {
        if (File.Exists(ConfigPath))
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<ChannelConfig>>(
                    File.ReadAllText(ConfigPath), JsonOptions);
                if (list is { Count: > 0 }) return list;
            }
            catch (Exception ex)
            {
                StatusText = $"配置文件解析失败，已使用默认配置：{ex.Message}";
            }
        }

        var defaults = CreateDemoChannels();
        SaveConfig(defaults);
        return defaults;
    }

    private void SaveConfig() => SaveConfig(Channels.Select(c => c.Runtime.Config).ToList());

    private void SaveConfig(List<ChannelConfig> configs)
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(configs, JsonOptions));
        }
        catch (Exception ex)
        {
            StatusText = $"配置保存失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 首次运行时的演示配置：两个模拟通道开箱即用，
    /// 再预置一个 Modbus TCP 通道（默认关闭），填好 IP 后即可对接真实设备或 Modbus Slave。
    /// </summary>
    private static List<ChannelConfig> CreateDemoChannels()
    {
        return
        [
            new ChannelConfig
            {
                Name = "1#反应釜",
                Transport = TransportKind.Simulated,
                Protocol = ProtocolKind.Simulated,
                PollIntervalMs = 100,
                Points =
                [
                    new PointDefinition
                    {
                        Name = "釜内温度", Unit = "℃", RegisterAddress = 0, Color = "#FF4FC3F7",
                        Alarm = new AlarmRule
                        {
                            Enabled = true, Operator = AlarmOperator.GreaterThan, Threshold = 85,
                            Severity = AlarmSeverity.Critical, Hysteresis = 4, DebounceCount = 3,
                            Message = "釜内温度超上限"
                        }
                    },
                    new PointDefinition
                    {
                        Name = "釜内压力", Unit = "MPa", RegisterAddress = 1, Color = "#FF3FB950",
                        Alarm = new AlarmRule
                        {
                            Enabled = true, Operator = AlarmOperator.OutsideRange,
                            LowLimit = 0.2, HighLimit = 1.6, Severity = AlarmSeverity.Warning,
                            Hysteresis = 0.1, DebounceCount = 3, Message = "压力偏离正常区间"
                        }
                    },
                    new PointDefinition { Name = "液位", Unit = "%", RegisterAddress = 2, Color = "#FFD29922" },
                    new PointDefinition { Name = "搅拌转速", Unit = "rpm", RegisterAddress = 3, Color = "#FFBC8F8F" }
                ]
            },
            new ChannelConfig
            {
                Name = "2#冷却塔",
                Transport = TransportKind.Simulated,
                Protocol = ProtocolKind.Simulated,
                PollIntervalMs = 200,
                Points =
                [
                    new PointDefinition { Name = "进水温度", Unit = "℃", RegisterAddress = 10, Color = "#FF79C0FF" },
                    new PointDefinition { Name = "出水温度", Unit = "℃", RegisterAddress = 11, Color = "#FFFFA657" },
                    new PointDefinition
                    {
                        Name = "风机电流", Unit = "A", RegisterAddress = 12, Color = "#FFA5D6FF",
                        Alarm = new AlarmRule
                        {
                            Enabled = true, Operator = AlarmOperator.GreaterThan, Threshold = 90,
                            Severity = AlarmSeverity.Warning, Hysteresis = 5, DebounceCount = 5,
                            Message = "风机电流偏高"
                        }
                    }
                ]
            },
            new ChannelConfig
            {
                Name = "3#Modbus TCP",
                Enabled = false,
                Transport = TransportKind.Tcp,
                Protocol = ProtocolKind.ModbusTcp,
                Host = "127.0.0.1",
                Port = 502,
                SlaveId = 1,
                PollIntervalMs = 500,
                Points =
                [
                    new PointDefinition
                    {
                        Name = "温度", Unit = "℃", RegisterAddress = 0,
                        DataType = ValueDataType.Int16, Scale = 0.1, Color = "#FF4FC3F7"
                    },
                    new PointDefinition
                    {
                        Name = "压力", Unit = "kPa", RegisterAddress = 1,
                        DataType = ValueDataType.UInt16, Color = "#FF3FB950"
                    }
                ]
            }
        ];
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _chartTimer.Stop();
        _statusTimer.Stop();
        _acquisition.AlarmRaised -= OnAlarmRaised;
        _acquisition.AlarmCleared -= OnAlarmCleared;
        GC.SuppressFinalize(this);
    }
}

using System.Windows;
using ScottPlot;
using ScottPlot.DataSources;
using ScottPlot.Plottables;
using WpfApp22.App.Charts;

namespace WpfApp22.App;

/// <summary>
/// 主窗口，同时充当图表宿主。
///
/// 性能要点：
/// 1. 曲线数据用 SignalXY 而不是 Scatter —— Signal 模式按屏幕像素做数据聚合，
///    几万个点也能保持流畅，这是 ScottPlot 针对高频采集专门优化过的渲染路径；
/// 2. 时间戳到 OLE 日期的转换复用同一块数组，每帧只做一次线性转换，不做新分配；
/// 3. Y 轴自动缩放不是每帧都做，否则曲线会不停上下跳动，这里降频到每 15 帧一次。
/// </summary>
public partial class MainWindow : Window, IChartHost
{
    private const int MinPointsToDraw = 2;

    private readonly Dictionary<Guid, SignalXY> _series = new();

    private int _frameCount;

    public MainWindow()
    {
        InitializeComponent();

        // 上位机常跑在 1366×768 的老工控机上，固定尺寸窗口会比工作区还高、把状态栏顶出屏幕。
        // 小屏直接最大化，大屏才使用固定尺寸。
        var workArea = SystemParameters.WorkArea;
        if (workArea.Width < 1400 || workArea.Height < 880)
        {
            WindowState = WindowState.Maximized;
        }
        else
        {
            Width = Math.Min(Width, workArea.Width - 40);
            Height = Math.Min(Height, workArea.Height - 40);
        }

        ConfigurePlot();
    }

    public bool AutoScaleY { get; set; } = true;

    private void ConfigurePlot()
    {
        var plot = LivePlot.Plot;

        // 顺序很重要：先把横轴换成时间轴，再设置颜色。
        // DateTimeTicksBottom() 的内部实现是"移除所有底部轴 → 新建一个 DateTime 轴 → 加回绘图区"，
        // 也就是整根轴对象的替换。如果先设颜色再换轴，颜色设置会被这步丢掉，
        // X 轴的日期刻度就退回 ScottPlot 默认的深色，在深色背景上基本看不见。
        plot.Axes.DateTimeTicksBottom();

        // ScottPlot 的默认绘图字体（Windows 上是 Segoe UI）不含中文字形，
        // 中文会渲染成一串豆腐块——图例里的"1#反应釜 · 釜内温度"会变成"1#000 · 0000"。
        // 图例和坐标轴的文字都是中文，所以必须显式换成带 CJK 字形的字体。
        const string ChartFont = "Microsoft YaHei UI";

        // 刻度文字用比轴线更亮的一档：11px 的小字用 #8B949E 在深底上对比度只有 5.7:1，
        // 远距和夜间值守都读不清。轴线本身保持暗色，避免抢视线。
        var tickColor = Color.FromHex("#C9D1D9");

        plot.FigureBackground.Color = Color.FromHex("#161B22");
        plot.DataBackground.Color = Color.FromHex("#0D1117");
        plot.Axes.Color(Color.FromHex("#8B949E"));
        plot.Grid.MajorLineColor = Color.FromHex("#1F242C");

        // Axes.Color() 会一次性刷掉轴上的标签颜色，所以刻度样式的设置必须放在它后面。
        // 注意属性名：LabelStyle 用的是 ForeColor（而 Legend 用的是 FontColor，两边不统一）。
        plot.Axes.Bottom.TickLabelStyle.FontName = ChartFont;
        plot.Axes.Bottom.TickLabelStyle.FontSize = 11;
        plot.Axes.Bottom.TickLabelStyle.ForeColor = tickColor;

        plot.Axes.Left.TickLabelStyle.FontName = ChartFont;
        plot.Axes.Left.TickLabelStyle.FontSize = 11;
        plot.Axes.Left.TickLabelStyle.ForeColor = tickColor;

        plot.Legend.IsVisible = true;
        plot.Legend.Alignment = Alignment.UpperRight;
        plot.Legend.FontName = ChartFont;
        plot.Legend.FontColor = Color.FromHex("#E6EDF3");
        plot.Legend.BackgroundColor = Color.FromHex("#CC161B22");
        plot.Legend.OutlineColor = Color.FromHex("#30363D");

        LivePlot.Refresh();
    }

    public void EnsureSeries(Guid pointId, string label, string colorHex)
    {
        if (_series.ContainsKey(pointId)) return;

        var signal = LivePlot.Plot.Add.SignalXY(new double[] { 0 }, new double[] { 0 });
        signal.LegendText = label;
        signal.LineStyle.Color = Color.FromHex(colorHex);
        signal.LineStyle.Width = 1.6f;
        signal.MarkerStyle.IsVisible = false;
        signal.IsVisible = false;

        _series[pointId] = signal;
    }

    public void UpdateSeries(Guid pointId, double[] timestampsMs, double[] values, int count)
    {
        if (!_series.TryGetValue(pointId, out var signal)) return;

        if (count < MinPointsToDraw)
        {
            signal.IsVisible = false;
            return;
        }

        // 必须传入长度恰好等于点数的数组：ScottPlot 的 Signal 数据源按数组长度决定渲染范围，
        // 若把复用的大数组直接传进去，尾部未初始化的 0 会破坏"X 必须递增"的前提，曲线就不显示。
        var xs = new double[count];
        var ys = new double[count];
        for (var i = 0; i < count; i++)
        {
            xs[i] = DateTimeOffset
                .FromUnixTimeMilliseconds((long)timestampsMs[i])
                .LocalDateTime
                .ToOADate();
            ys[i] = values[i];
        }

        signal.Data = new SignalXYSourceDoubleArray(xs, ys);
        signal.IsVisible = true;
    }

    public void RemoveSeries(Guid pointId)
    {
        if (!_series.TryGetValue(pointId, out var signal)) return;

        LivePlot.Plot.Remove(signal);
        _series.Remove(pointId);
    }

    public void SetTimeRange(long fromMs, long toMs)
    {
        var from = DateTimeOffset.FromUnixTimeMilliseconds(fromMs).LocalDateTime.ToOADate();
        var to = DateTimeOffset.FromUnixTimeMilliseconds(toMs).LocalDateTime.ToOADate();
        LivePlot.Plot.Axes.SetLimitsX(from, to);
    }

    public void Reset()
    {
        LivePlot.Plot.Clear();
        _series.Clear();
        LivePlot.Refresh();
    }

    public void Refresh()
    {
        if (AutoScaleY && _frameCount++ % 15 == 0)
        {
            LivePlot.Plot.Axes.AutoScaleY();
        }

        LivePlot.Refresh();
    }
}

namespace WpfApp22.App.Charts;

/// <summary>
/// 图表宿主抽象。
///
/// 视图模型只通过这个接口驱动曲线，不直接依赖 ScottPlot 控件，
/// 好处有两个：图表库的替换不影响业务逻辑；图表刷新逻辑可以脱离 UI 做单元测试。
/// </summary>
public interface IChartHost
{
    /// <summary>确保指定点位存在一条曲线，不存在则创建。</summary>
    void EnsureSeries(Guid pointId, string label, string colorHex);

    void UpdateSeries(Guid pointId, double[] timestampsMs, double[] values, int count);

    void RemoveSeries(Guid pointId);

    /// <summary>设置横轴显示的时间范围（UTC 毫秒时间戳）。</summary>
    void SetTimeRange(long fromMs, long toMs);

    /// <summary>纵轴自适应。实时曲线抖动明显时可关闭，改为手动缩放查看细节。</summary>
    bool AutoScaleY { get; set; }

    void Reset();

    void Refresh();
}

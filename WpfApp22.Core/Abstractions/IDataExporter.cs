namespace WpfApp22.Core.Abstractions;

/// <summary>待导出的一条曲线数据。</summary>
public sealed class ExportSeries
{
    public string ChannelName { get; init; } = string.Empty;

    public string PointName { get; init; } = string.Empty;

    public string Unit { get; init; } = string.Empty;

    /// <summary>时间列（UTC 毫秒时间戳）。</summary>
    public double[] Timestamps { get; init; } = Array.Empty<double>();

    /// <summary>数值列。</summary>
    public double[] Values { get; init; } = Array.Empty<double>();
}

public sealed record ExportRequest
{
    public string FilePath { get; init; } = string.Empty;

    public IReadOnlyList<ExportSeries> Series { get; init; } = Array.Empty<ExportSeries>();

    /// <summary>是否把时间戳写成可读的本地时间字符串而非 Unix 毫秒。</summary>
    public bool UseReadableTime { get; init; } = true;

    /// <summary>超过该点数时自动降采样，防止导出文件过大。0 表示不限制。</summary>
    public int MaxPointsPerSeries { get; init; }
}

/// <summary>数据导出器抽象。CSV 与 Excel 是两种实现，互不依赖。</summary>
public interface IDataExporter
{
    string FormatName { get; }

    string FileExtension { get; }

    Task ExportAsync(ExportRequest request, CancellationToken cancellationToken = default);
}

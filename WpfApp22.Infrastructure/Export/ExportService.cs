using WpfApp22.Core.Abstractions;

namespace WpfApp22.Infrastructure.Export;

/// <summary>
/// 导出服务：按文件扩展名路由到具体导出器。
/// 以后要支持 JSON、Parquet，只需新增一个 IDataExporter 实现并注册进 DI。
/// </summary>
public sealed class ExportService
{
    private readonly Dictionary<string, IDataExporter> _exporters = new(StringComparer.OrdinalIgnoreCase);

    public ExportService(IEnumerable<IDataExporter> exporters)
    {
        foreach (var exporter in exporters)
        {
            _exporters[exporter.FileExtension] = exporter;
        }
    }

    public IReadOnlyCollection<string> SupportedExtensions => _exporters.Keys.ToArray();

    public IDataExporter Resolve(string extensionOrFormatName)
    {
        if (_exporters.TryGetValue(extensionOrFormatName, out var byExtension)) return byExtension;

        var byName = _exporters.Values
            .FirstOrDefault(e => string.Equals(e.FormatName, extensionOrFormatName, StringComparison.OrdinalIgnoreCase));

        return byName ?? throw new NotSupportedException($"不支持的导出格式：{extensionOrFormatName}");
    }

    public Task ExportAsync(string filePath, ExportRequest request, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(filePath);
        var exporter = Resolve(extension);
        return exporter.ExportAsync(request with { FilePath = filePath }, cancellationToken);
    }
}

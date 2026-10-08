using ClosedXML.Excel;
using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Buffers;

namespace WpfApp22.Infrastructure.Export;

/// <summary>
/// Excel 导出（基于 ClosedXML，不需要安装 Office）。
/// 每个点位单独一个工作表，另附一个总览表，方便直接发给工艺或质量部门。
/// </summary>
public sealed class ExcelExporter : IDataExporter
{
    private const int ExcelSheetNameMaxLength = 31;

    public string FormatName => "Excel";

    public string FileExtension => ".xlsx";

    public Task ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(request.FilePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(request.FilePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        using var workbook = new XLWorkbook();

        WriteOverview(workbook, request);

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var bufferTimes = new double[0];
        var bufferValues = new double[0];

        foreach (var series in request.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var count = series.Timestamps.Length;
            var srcTimes = series.Timestamps;
            var srcValues = series.Values;

            if (request.MaxPointsPerSeries > 0 && count > request.MaxPointsPerSeries)
            {
                if (bufferTimes.Length < request.MaxPointsPerSeries)
                {
                    bufferTimes = new double[request.MaxPointsPerSeries];
                    bufferValues = new double[request.MaxPointsPerSeries];
                }

                count = Downsampler.Uniform(series.Timestamps, series.Values, count,
                    request.MaxPointsPerSeries, bufferTimes, bufferValues);
                srcTimes = bufferTimes;
                srcValues = bufferValues;
            }

            var sheet = workbook.Worksheets.Add(MakeUniqueSheetName(series.ChannelName, series.PointName, usedNames));
            sheet.Cell(1, 1).Value = "时间";
            sheet.Cell(1, 2).Value = string.IsNullOrEmpty(series.Unit) ? "数值" : $"数值（{series.Unit}）";
            sheet.Row(1).Style.Font.Bold = true;

            for (var i = 0; i < count; i++)
            {
                var ts = DateTimeOffset.FromUnixTimeMilliseconds((long)srcTimes[i]).ToLocalTime();
                sheet.Cell(i + 2, 1).Value = ts.DateTime;
                sheet.Cell(i + 2, 1).Style.DateFormat.Format = "yyyy-MM-dd HH:mm:ss.fff";
                sheet.Cell(i + 2, 2).Value = srcValues[i];
            }

            sheet.Column(1).Width = 22;
            sheet.Column(2).Width = 14;
        }

        workbook.SaveAs(request.FilePath);
        return Task.CompletedTask;
    }

    private static void WriteOverview(XLWorkbook workbook, ExportRequest request)
    {
        var overview = workbook.Worksheets.Add("总览");
        overview.Cell(1, 1).Value = "通道";
        overview.Cell(1, 2).Value = "点位";
        overview.Cell(1, 3).Value = "单位";
        overview.Cell(1, 4).Value = "点数";
        overview.Cell(1, 5).Value = "起始时间";
        overview.Cell(1, 6).Value = "结束时间";
        overview.Row(1).Style.Font.Bold = true;

        for (var i = 0; i < request.Series.Count; i++)
        {
            var s = request.Series[i];
            var row = i + 2;
            overview.Cell(row, 1).Value = s.ChannelName;
            overview.Cell(row, 2).Value = s.PointName;
            overview.Cell(row, 3).Value = s.Unit;
            overview.Cell(row, 4).Value = s.Timestamps.Length;

            if (s.Timestamps.Length > 0)
            {
                overview.Cell(row, 5).Value = DateTimeOffset.FromUnixTimeMilliseconds((long)s.Timestamps[0]).ToLocalTime().DateTime;
                overview.Cell(row, 6).Value = DateTimeOffset.FromUnixTimeMilliseconds((long)s.Timestamps[^1]).ToLocalTime().DateTime;
            }
        }

        overview.Columns().AdjustToContents();
    }

    private static string MakeUniqueSheetName(string channel, string point, HashSet<string> used)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var raw = $"{channel}-{point}";
        var cleaned = new string(raw.Where(c => !invalid.Contains(c)).ToArray());

        if (cleaned.Length > ExcelSheetNameMaxLength)
            cleaned = cleaned[..ExcelSheetNameMaxLength];

        var name = cleaned;
        var index = 2;
        while (!used.Add(name))
        {
            var suffix = $"_{index++}";
            name = cleaned[..Math.Min(cleaned.Length, ExcelSheetNameMaxLength - suffix.Length)] + suffix;
        }

        return name;
    }
}

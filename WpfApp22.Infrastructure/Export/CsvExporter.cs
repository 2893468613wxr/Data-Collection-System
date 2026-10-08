using System.Globalization;
using System.Text;
using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Buffers;

namespace WpfApp22.Infrastructure.Export;

/// <summary>
/// CSV 导出。采用"长表"格式（时间 / 通道 / 点位 / 数值 / 单位），
/// 而不是每个点位一列的宽表 —— 因为多通道采集时各点位的时间戳并不对齐，
/// 宽表要么留大量空单元格，要么需要做时间对齐插值，
/// 长表则可以直接导入 InfluxDB、TDengine 以及 Pandas 做时序分析。
/// 使用流式写入，20 万行也不会把内存撑爆。
/// </summary>
public sealed class CsvExporter : IDataExporter
{
    public string FormatName => "CSV";

    public string FileExtension => ".csv";

    public async Task ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(request.FilePath);

        var directory = Path.GetDirectoryName(Path.GetFullPath(request.FilePath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        // 带 BOM 的 UTF-8，保证 Excel 双击打开时中文不乱码
        using var stream = new FileStream(request.FilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var writer = new StreamWriter(stream, new UTF8Encoding(true));

        await writer.WriteLineAsync("Timestamp,Channel,Point,Value,Unit").ConfigureAwait(false);

        var times = new double[0];
        var values = new double[0];

        foreach (var series in request.Series)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var srcTimes = series.Timestamps;
            var srcValues = series.Values;
            var count = Math.Min(srcTimes.Length, srcValues.Length);

            // 超过上限时等间隔抽样，控制文件体积
            if (request.MaxPointsPerSeries > 0 && count > request.MaxPointsPerSeries)
            {
                if (times.Length < request.MaxPointsPerSeries)
                {
                    times = new double[request.MaxPointsPerSeries];
                    values = new double[request.MaxPointsPerSeries];
                }

                count = Downsampler.Uniform(srcTimes, srcValues, count,
                    request.MaxPointsPerSeries, times, values);
                srcTimes = times;
                srcValues = values;
            }

            var sb = new StringBuilder(256);
            for (var i = 0; i < count; i++)
            {
                var time = request.UseReadableTime
                    ? DateTimeOffset.FromUnixTimeMilliseconds((long)srcTimes[i]).ToLocalTime()
                        .ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                    : ((long)srcTimes[i]).ToString(CultureInfo.InvariantCulture);

                sb.Append(time).Append(',');
                AppendCsvField(sb, series.ChannelName).Append(',');
                AppendCsvField(sb, series.PointName).Append(',');
                sb.Append(srcValues[i].ToString("G10", CultureInfo.InvariantCulture)).Append(',');
                AppendCsvField(sb, series.Unit);

                await writer.WriteLineAsync(sb.ToString()).ConfigureAwait(false);
                sb.Clear();
            }
        }

        await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static StringBuilder AppendCsvField(StringBuilder sb, string field)
    {
        if (field.AsSpan().IndexOfAny(',', '"', '\n') >= 0)
        {
            sb.Append('"').Append(field.Replace("\"", "\"\"")).Append('"');
        }
        else
        {
            sb.Append(field);
        }

        return sb;
    }
}

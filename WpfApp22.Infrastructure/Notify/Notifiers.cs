using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Models;

namespace WpfApp22.Infrastructure.Notify;

/// <summary>
/// 日志通知渠道：把报警落到按天切分的日志文件里，便于事后追溯。
/// 这是任何上位机都必须有的"兜底"渠道。
/// </summary>
public sealed class LogNotifier : IAlarmNotifier
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _directory;

    public LogNotifier(string? directory = null)
    {
        _directory = directory ?? Path.Combine(AppContext.BaseDirectory, "logs");
        Directory.CreateDirectory(_directory);
    }

    public string Name => "日志文件";

    public bool Enabled => true;

    public async Task<bool> NotifyAsync(AlarmEvent alarmEvent, CancellationToken cancellationToken = default)
    {
        var file = Path.Combine(_directory, $"alarm-{DateTime.Now:yyyyMMdd}.log");
        var line = string.Format(CultureInfo.InvariantCulture,
            "{0:yyyy-MM-dd HH:mm:ss} [{1}] {2} / {3} : {4} = {5:F2}{6}",
            alarmEvent.RaisedAt.ToLocalTime(),
            alarmEvent.SeverityText,
            alarmEvent.ChannelName,
            alarmEvent.PointName,
            alarmEvent.Message,
            alarmEvent.TriggerValue,
            alarmEvent.Unit);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(file, line + Environment.NewLine, Encoding.UTF8, cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>
/// 声音报警。严重报警用高频蜂鸣，普通警告用低频，现场操作员不用盯屏幕也能察觉。
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class SoundNotifier : IAlarmNotifier
{
    public string Name => "声音";

    public bool Enabled { get; set; } = true;

    /// <summary>只对严重及以上报警发声，避免频繁打扰。</summary>
    public bool OnlyForCritical { get; set; } = true;

    public Task<bool> NotifyAsync(AlarmEvent alarmEvent, CancellationToken cancellationToken = default)
    {
        if (OnlyForCritical && alarmEvent.Severity != AlarmSeverity.Critical) return Task.FromResult(true);

        try
        {
            var frequency = alarmEvent.Severity == AlarmSeverity.Critical ? 1200 : 800;
            Task.Run(() =>
            {
                for (var i = 0; i < 2; i++) Console.Beep(frequency, 180);
            }, CancellationToken.None);

            return Task.FromResult(true);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }
}

/// <summary>
/// Webhook 通知：把报警以 JSON POST 出去，可直接对接企业微信机器人、钉钉机器人或自建服务。
/// 未配置 Url 时自动禁用，避免产生大量无意义的请求。
/// </summary>
public sealed class WebhookNotifier : IAlarmNotifier
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public string Name => "Webhook";

    public bool Enabled => !string.IsNullOrWhiteSpace(Url);

    /// <summary>机器人地址。留空则不启用。</summary>
    public string? Url { get; set; }

    public async Task<bool> NotifyAsync(AlarmEvent alarmEvent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(Url)) return false;

        var payload = new
        {
            msgtype = "text",
            text = new
            {
                content = $"[报警·{alarmEvent.SeverityText}] {alarmEvent.ChannelName} / {alarmEvent.PointName}\n" +
                          $"{alarmEvent.Message}\n触发值：{alarmEvent.TriggerValue:F2}{alarmEvent.Unit}\n" +
                          $"时间：{alarmEvent.RaisedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}"
            }
        };

        try
        {
            var response = await Http.PostAsJsonAsync(Url, payload, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

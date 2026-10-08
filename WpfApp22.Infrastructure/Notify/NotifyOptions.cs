using System.Text.Json;

namespace WpfApp22.Infrastructure.Notify;

/// <summary>
/// 报警推送配置。
///
/// 默认从 config/notify.json 读取；首次运行会自动生成一份带注释字段的样例行，
/// 现场部署时也可以用环境变量 WPFAPP22_WEBHOOK_URL 覆盖 —— 改地址不用重新发版。
/// </summary>
public sealed class NotifyOptions
{
    /// <summary>是否启用声音报警。</summary>
    public bool SoundEnabled { get; set; } = true;

    /// <summary>是否只在严重（Critical）级别才发声，避免频繁告警时一直响。</summary>
    public bool SoundOnlyForCritical { get; set; } = true;

    /// <summary>
    /// 机器人地址。留空则 Webhook 渠道自动禁用。
    /// 支持企业微信 / 钉钉群机器人的 text 消息格式。
    /// </summary>
    public string? WebhookUrl { get; set; } = string.Empty;

    public static NotifyOptions Load(string? path = null)
    {
        path ??= Path.Combine(AppContext.BaseDirectory, "config", "notify.json");

        var options = new NotifyOptions();
        try
        {
            if (File.Exists(path))
            {
                options = JsonSerializer.Deserialize<NotifyOptions>(
                              File.ReadAllText(path),
                              new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                          ?? new NotifyOptions();
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(path, JsonSerializer.Serialize(
                    new NotifyOptions(), new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch
        {
            // 配置文件写坏 / 没权限时退回默认配置。
            // 通知配置读不出来绝不能影响采集主流程，更不能因此启动失败。
        }

        var fromEnv = Environment.GetEnvironmentVariable("WPFAPP22_WEBHOOK_URL");
        if (!string.IsNullOrWhiteSpace(fromEnv)) options.WebhookUrl = fromEnv;

        return options;
    }
}

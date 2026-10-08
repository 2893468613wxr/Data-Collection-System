using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Models;

namespace WpfApp22.Infrastructure.Notify;

/// <summary>
/// 报警分发器：把一条报警并行推给所有启用的通知渠道。
/// 任一渠道失败（例如网络不通、Webhook 超时）只记录结果，不影响其它渠道，
/// 更不会打断采集主流程 —— 通知系统的失败绝不能反过来拖垮采集。
/// </summary>
public sealed class AlarmDispatcher
{
    private readonly IEnumerable<IAlarmNotifier> _notifiers;

    public AlarmDispatcher(IEnumerable<IAlarmNotifier> notifiers)
    {
        _notifiers = notifiers;
    }

    /// <summary>实际参与推送的渠道名称（未配置的渠道会被排除），用于界面提示。</summary>
    public IReadOnlyList<string> ChannelNames =>
        _notifiers.Where(n => n.Enabled).Select(n => n.Name).ToArray();

    /// <summary>返回推送失败的渠道名称，供界面提示。</summary>
    public async Task<IReadOnlyList<string>> DispatchAsync(AlarmEvent alarmEvent, CancellationToken cancellationToken = default)
    {
        var failures = new List<string>();

        foreach (var notifier in _notifiers)
        {
            if (!notifier.Enabled) continue;
            try
            {
                var ok = await notifier.NotifyAsync(alarmEvent, cancellationToken).ConfigureAwait(false);
                if (!ok) failures.Add(notifier.Name);
            }
            catch (Exception)
            {
                failures.Add(notifier.Name);
            }
        }

        return failures;
    }
}

using WpfApp22.Core.Models;

namespace WpfApp22.Core.Abstractions;

/// <summary>
/// 报警通知渠道。日志、声音、弹窗、Webhook、短信都可以实现为独立渠道，
/// 由通知分发器并行推送，任一渠道失败不影响其它渠道。
/// </summary>
public interface IAlarmNotifier
{
    string Name { get; }

    bool Enabled { get; }

    /// <summary>
    /// 推送一条报警。返回 false 表示推送失败，由分发器记录但不中断整体流程。
    /// </summary>
    Task<bool> NotifyAsync(AlarmEvent alarmEvent, CancellationToken cancellationToken = default);
}

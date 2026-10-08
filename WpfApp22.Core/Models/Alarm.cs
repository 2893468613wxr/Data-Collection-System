namespace WpfApp22.Core.Models;

/// <summary>
/// 报警规则。包含三个工业现场必需的抑制机制：
/// 滞回（Hysteresis）防止在阈值附近来回抖动刷屏、
/// 去抖（DebounceCount）要求连续 N 次超限才确认、
/// 延时（DelayMs）要求超限持续一段时间才确认。
/// </summary>
public sealed class AlarmRule
{
    public bool Enabled { get; set; }

    public AlarmOperator Operator { get; set; } = AlarmOperator.GreaterThan;

    public AlarmSeverity Severity { get; set; } = AlarmSeverity.Warning;

    /// <summary>用于 GreaterThan / LessThan / RateOfChange 的阈值。</summary>
    public double Threshold { get; set; } = 100;

    /// <summary>用于 OutsideRange / InsideRange 的下限。</summary>
    public double LowLimit { get; set; } = 0;

    /// <summary>用于 OutsideRange / InsideRange 的上限。</summary>
    public double HighLimit { get; set; } = 100;

    /// <summary>滞回带宽。触发后需要回退超过该值才判定恢复。</summary>
    public double Hysteresis { get; set; }

    /// <summary>连续超限次数达到该值才确认报警。</summary>
    public int DebounceCount { get; set; } = 1;

    /// <summary>超限持续时长达到该毫秒数才确认报警。</summary>
    public int DelayMs { get; set; }

    public string Message { get; set; } = "数值越限";

    /// <summary>判断当前值是否处于"越限"状态（不做滞回修正，滞回由引擎处理）。</summary>
    public bool IsViolated(double value, double? previousValue, double? elapsedSeconds)
    {
        if (!Enabled || double.IsNaN(value)) return false;

        return Operator switch
        {
            AlarmOperator.GreaterThan => value > Threshold,
            AlarmOperator.LessThan => value < Threshold,
            AlarmOperator.OutsideRange => value < LowLimit || value > HighLimit,
            AlarmOperator.InsideRange => value > LowLimit && value < HighLimit,
            AlarmOperator.RateOfChange =>
                previousValue.HasValue && elapsedSeconds is > 0
                && Math.Abs(value - previousValue.Value) / elapsedSeconds.Value > Threshold,
            _ => false
        };
    }

    public string DescribeLimit()
    {
        return Operator switch
        {
            AlarmOperator.GreaterThan => $"> {Threshold}",
            AlarmOperator.LessThan => $"< {Threshold}",
            AlarmOperator.OutsideRange => $"[{LowLimit}, {HighLimit}] 之外",
            AlarmOperator.InsideRange => $"[{LowLimit}, {HighLimit}] 之内",
            AlarmOperator.RateOfChange => $"变化率 > {Threshold}/s",
            _ => "-"
        };
    }
}

/// <summary>报警事件实例，代表一次从触发到恢复的完整生命周期。</summary>
public sealed class AlarmEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public Guid ChannelId { get; init; }

    public Guid PointId { get; init; }

    public string ChannelName { get; set; } = string.Empty;

    public string PointName { get; set; } = string.Empty;

    public AlarmSeverity Severity { get; set; }

    public string Message { get; set; } = string.Empty;

    public double TriggerValue { get; set; }

    public string Unit { get; set; } = string.Empty;

    public DateTimeOffset RaisedAt { get; set; }

    public DateTimeOffset? ClearedAt { get; set; }

    public bool IsActive { get; set; } = true;

    public bool Acknowledged { get; set; }

    public string SeverityText => Severity switch
    {
        AlarmSeverity.Critical => "严重",
        AlarmSeverity.Warning => "警告",
        _ => "提示"
    };

    public string FormatTriggerValue() => $"{TriggerValue:F2}{Unit}";
}

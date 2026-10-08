using WpfApp22.Core.Models;

namespace WpfApp22.Core.Services;

/// <summary>
/// 报警引擎。
///
/// 工业现场最忌讳"报警刷屏"：一个在阈值附近抖动的测点能在一分钟内产生几百条报警，
/// 让操作员彻底忽视报警区。因此这里实现了三重抑制：
///   1. 去抖 DebounceCount —— 连续 N 次越限才认定；
///   2. 延时 DelayMs —— 越限持续 N 毫秒才认定；
///   3. 滞回 Hysteresis —— 触发后必须回退超过带宽才判定恢复，避免在阈值附近反复触发/恢复。
/// </summary>
public sealed class AlarmEngine
{
    private readonly Dictionary<Guid, PointState> _states = new();
    private readonly object _sync = new();

    /// <summary>新报警产生。</summary>
    public event Action<AlarmEvent>? AlarmRaised;

    /// <summary>报警恢复。</summary>
    public event Action<AlarmEvent>? AlarmCleared;

    public void Reset()
    {
        lock (_sync) _states.Clear();
    }

    public void Remove(Guid pointId)
    {
        lock (_sync) _states.Remove(pointId);
    }

    /// <summary>对一次采样做报警判定。注意：本方法只在汇聚单线程中被调用。</summary>
    public void Evaluate(Guid channelId, string channelName, PointDefinition point, Sample sample)
    {
        var rule = point.Alarm;
        if (rule is null || !rule.Enabled)
        {
            ClearIfActive(point);
            return;
        }

        if (!sample.IsGood)
        {
            // 通讯异常不参与数值报警判定，交由通讯故障报警单独处理
            return;
        }

        lock (_sync)
        {
            if (!_states.TryGetValue(point.Id, out var state))
            {
                state = new PointState();
                _states[point.Id] = state;
            }

            var nowMs = sample.TimestampMs;
            var elapsedSeconds = state.PreviousTimestampMs.HasValue
                ? (nowMs - state.PreviousTimestampMs.Value) / 1000.0
                : (double?)null;

            var stillViolated = IsViolated(rule, sample.Value, state.PreviousValue, elapsedSeconds, state.IsLatched);

            if (state.IsLatched)
            {
                if (!stillViolated)
                {
                    // 满足滞回条件，判定恢复
                    var active = state.ActiveEvent;
                    if (active is not null)
                    {
                        active.ClearedAt = DateTimeOffset.FromUnixTimeMilliseconds(nowMs);
                        active.IsActive = false;
                        AlarmCleared?.Invoke(active);
                    }

                    state.IsLatched = false;
                    state.ActiveEvent = null;
                    state.ViolationStreak = 0;
                    state.ViolationSinceMs = null;
                }
            }
            else if (stillViolated)
            {
                state.ViolationStreak++;
                state.ViolationSinceMs ??= nowMs;

                var countOk = state.ViolationStreak >= Math.Max(1, rule.DebounceCount);
                var delayOk = nowMs - state.ViolationSinceMs.Value >= rule.DelayMs;

                if (countOk && delayOk)
                {
                    var evt = new AlarmEvent
                    {
                        ChannelId = channelId,
                        PointId = point.Id,
                        ChannelName = channelName,
                        PointName = point.Name,
                        Severity = rule.Severity,
                        Message = $"{point.Name} {rule.Message}（{rule.DescribeLimit()}）",
                        TriggerValue = sample.Value,
                        Unit = point.Unit,
                        RaisedAt = DateTimeOffset.FromUnixTimeMilliseconds(nowMs)
                    };

                    state.IsLatched = true;
                    state.ActiveEvent = evt;
                    AlarmRaised?.Invoke(evt);
                }
            }
            else
            {
                state.ViolationStreak = 0;
                state.ViolationSinceMs = null;
            }

            state.PreviousValue = sample.Value;
            state.PreviousTimestampMs = nowMs;
        }
    }

    /// <summary>供采集服务上报通讯类故障（不符合任何数值规则的报警）。</summary>
    public void RaiseCustom(Guid channelId, string channelName, AlarmSeverity severity, string message)
    {
        var evt = new AlarmEvent
        {
            ChannelId = channelId,
            PointId = Guid.Empty,
            ChannelName = channelName,
            PointName = "通讯",
            Severity = severity,
            Message = message,
            RaisedAt = DateTimeOffset.Now
        };

        AlarmRaised?.Invoke(evt);
    }

    private void ClearIfActive(PointDefinition point)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(point.Id, out var state)) return;
            var active = state.ActiveEvent;
            if (active is not null && active.IsActive)
            {
                active.ClearedAt = DateTimeOffset.Now;
                active.IsActive = false;
                AlarmCleared?.Invoke(active);
            }

            _states.Remove(point.Id);
        }
    }

    /// <summary>
    /// 越限判定。已触发（latched）时减去滞回带宽，使恢复条件比触发条件更宽松。
    /// </summary>
    private static bool IsViolated(AlarmRule rule, double value, double? previousValue, double? elapsedSeconds, bool latched)
    {
        if (double.IsNaN(value)) return false;
        var h = latched ? Math.Abs(rule.Hysteresis) : 0.0;

        return rule.Operator switch
        {
            AlarmOperator.GreaterThan => value > rule.Threshold - h,
            AlarmOperator.LessThan => value < rule.Threshold + h,
            AlarmOperator.OutsideRange => value < rule.LowLimit + h || value > rule.HighLimit - h,
            AlarmOperator.InsideRange => value > rule.LowLimit + h && value < rule.HighLimit - h,
            AlarmOperator.RateOfChange =>
                previousValue.HasValue && elapsedSeconds is > 0
                && Math.Abs(value - previousValue.Value) / elapsedSeconds.Value > rule.Threshold - h,
            _ => false
        };
    }

    private sealed class PointState
    {
        public int ViolationStreak;
        public long? ViolationSinceMs;
        public double? PreviousValue;
        public long? PreviousTimestampMs;
        public bool IsLatched;
        public AlarmEvent? ActiveEvent;
    }
}

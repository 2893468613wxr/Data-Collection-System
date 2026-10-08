namespace WpfApp22.Core.Buffers;

/// <summary>
/// 定长时间序列环形缓冲。
///
/// 设计要点（这是本项目能扛住高频采集的关键）：
/// 1. 内部数组在构造时一次性分配，运行期 Append 不产生任何堆分配，避免 GC 抖动；
/// 2. 写入端约定为单线程（汇聚任务），读取端（UI 刷新、导出）通过锁做快照；
/// 3. 容量固定，写满后自动覆盖最老的数据 —— 长时间挂机内存不会增长。
/// </summary>
public sealed class TimeSeriesBuffer
{
    private readonly long[] _timestamps;
    private readonly double[] _values;
    private readonly object _sync = new();

    private int _head;
    private int _count;
    private long _sequence;

    public TimeSeriesBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 2);
        _timestamps = new long[capacity];
        _values = new double[capacity];
        Capacity = capacity;
    }

    public int Capacity { get; }

    /// <summary>当前已写入且未被覆盖的有效点数。</summary>
    public int Count
    {
        get { lock (_sync) return _count; }
    }

    /// <summary>累计写入点数（含被覆盖的），用于统计吞吐。</summary>
    public long TotalAppended => Interlocked.Read(ref _sequence);

    public void Append(long timestampMs, double value)
    {
        lock (_sync)
        {
            _timestamps[_head] = timestampMs;
            _values[_head] = value;
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
        }

        Interlocked.Increment(ref _sequence);
    }

    public void AppendRange(ReadOnlySpan<long> timestamps, ReadOnlySpan<double> values)
    {
        if (timestamps.Length != values.Length) throw new ArgumentException("时间戳与数值长度不一致");

        lock (_sync)
        {
            for (var i = 0; i < timestamps.Length; i++)
            {
                _timestamps[_head] = timestamps[i];
                _values[_head] = values[i];
                _head = (_head + 1) % Capacity;
                if (_count < Capacity) _count++;
            }
        }

        Interlocked.Add(ref _sequence, timestamps.Length);
    }

    public void Clear()
    {
        lock (_sync)
        {
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>取最新值，没有任何数据时返回 null。</summary>
    public (long TimestampMs, double Value)? Last()
    {
        lock (_sync)
        {
            if (_count == 0) return null;
            var idx = (_head - 1 + Capacity) % Capacity;
            return (_timestamps[idx], _values[idx]);
        }
    }

    public (long First, long Last)? TimeRange()
    {
        lock (_sync)
        {
            if (_count == 0) return null;
            var start = (_head - _count + Capacity) % Capacity;
            var lastIdx = (_head - 1 + Capacity) % Capacity;
            return (_timestamps[start], _timestamps[lastIdx]);
        }
    }

    /// <summary>
    /// 按时间升序拷贝最多 maxCount 个最新点到调用方提供的数组，返回实际拷贝点数。
    /// 目标数组由调用方复用，因此本方法同样不产生堆分配。
    /// </summary>
    public int CopyLatest(int maxCount, double[] destTimestamps, double[] destValues)
    {
        ArgumentNullException.ThrowIfNull(destTimestamps);
        ArgumentNullException.ThrowIfNull(destValues);
        var limit = Math.Min(maxCount, Math.Min(destTimestamps.Length, destValues.Length));
        if (limit <= 0) return 0;

        lock (_sync)
        {
            var n = Math.Min(limit, _count);
            if (n == 0) return 0;

            var start = (_head - n + Capacity) % Capacity;
            for (var i = 0; i < n; i++)
            {
                var idx = (start + i) % Capacity;
                destTimestamps[i] = _timestamps[idx];
                destValues[i] = _values[idx];
            }

            return n;
        }
    }

    /// <summary>拷贝指定时间窗口内的数据（闭区间），按时间升序写入目标数组。</summary>
    public int CopyRange(long fromMs, long toMs, double[] destTimestamps, double[] destValues)
    {
        ArgumentNullException.ThrowIfNull(destTimestamps);
        ArgumentNullException.ThrowIfNull(destValues);
        var limit = Math.Min(destTimestamps.Length, destValues.Length);
        lock (_sync)
        {
            var start = (_head - _count + Capacity) % Capacity;
            var n = 0;
            for (var i = 0; i < _count && n < limit; i++)
            {
                var idx = (start + i) % Capacity;
                var ts = _timestamps[idx];
                if (ts < fromMs) continue;
                if (ts > toMs) break;

                destTimestamps[n] = ts;
                destValues[n] = _values[idx];
                n++;
            }

            return n;
        }
    }
}

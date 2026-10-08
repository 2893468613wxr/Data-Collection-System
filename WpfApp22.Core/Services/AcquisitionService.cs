using System.Collections.Concurrent;
using System.Threading.Channels;
using WpfApp22.Core.Abstractions;
using WpfApp22.Core.Models;

namespace WpfApp22.Core.Services;

/// <summary>
/// 采集服务 —— 整个系统的并发中枢。
///
/// 并发模型（面试时最值得讲的部分）：
///   1. 每个通道一个独立的长跑 Task，互不阻塞；某台设备超时不会拖慢其它通道；
///   2. 采集 Task 不直接碰 UI 和缓冲区，只往有界管道 System.Threading.Channels 里写；
///   3. 管道容量有上限，写满时丢弃最老的数据（背压），保证内存可控且不阻塞采集线程；
///   4. 唯一的一个汇聚消费者批量取出数据，写入环形缓冲并做报警判定 —— 单写者，无需锁竞争；
///   5. UI 完全不参与采集路径，只在自己的节拍上读取缓冲区。
/// </summary>
public sealed class AcquisitionService : IAsyncDisposable
{
    private const int BatchSize = 512;

    private readonly IDeviceDriverFactory _driverFactory;
    private readonly ConcurrentDictionary<Guid, ChannelRuntime> _channels = new();
    private readonly Channel<SampleEnvelope> _pipeline;

    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;
    private long _droppedSamples;

    public AcquisitionService(IDeviceDriverFactory driverFactory, int pipelineCapacity = 100_000)
    {
        _driverFactory = driverFactory;
        _pipeline = Channel.CreateBounded<SampleEnvelope>(new BoundedChannelOptions(pipelineCapacity)
        {
            // 背压策略：宁可丢旧点，也不让采集线程被阻塞、更不能无限增长内存
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        Hub = new DataHub();
        Alarms = new AlarmEngine();
        Alarms.AlarmRaised += e => AlarmRaised?.Invoke(e);
        Alarms.AlarmCleared += e => AlarmCleared?.Invoke(e);
    }

    public DataHub Hub { get; }

    public AlarmEngine Alarms { get; }

    public IEnumerable<ChannelRuntime> Channels => _channels.Values;

    /// <summary>因管道满而丢弃的采样点数，是判断系统是否过载的重要指标。</summary>
    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    public bool IsPumpRunning => _pumpTask is not null && !_pumpTask.IsCompleted;

    public event Action<AlarmEvent>? AlarmRaised;

    public event Action<AlarmEvent>? AlarmCleared;

    public event Action<ChannelRuntime>? ChannelStateChanged;

    // ---------- 通道管理 ----------

    public ChannelRuntime AddChannel(ChannelConfig config)
    {
        var driver = _driverFactory.Create(config);
        var runtime = new ChannelRuntime(config, driver);
        _channels[config.Id] = runtime;
        runtime.EnsureStores(Hub);
        return runtime;
    }

    public ChannelRuntime? GetChannel(Guid id) => _channels.TryGetValue(id, out var rt) ? rt : null;

    public async Task RemoveChannelAsync(Guid id)
    {
        if (!_channels.TryRemove(id, out var rt)) return;
        await StopChannelAsync(id).ConfigureAwait(false);
        Hub.RemoveChannel(id);
        Alarms.Remove(Guid.Empty);
        await rt.Driver.DisposeAsync().ConfigureAwait(false);
    }

    // ---------- 生命周期 ----------

    public Task StartAsync()
    {
        if (IsPumpRunning) return Task.CompletedTask;
        _pumpCts = new CancellationTokenSource();
        _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StartAllAsync()
    {
        await StartAsync().ConfigureAwait(false);
        foreach (var rt in _channels.Values)
        {
            if (rt.Config.Enabled) await StartChannelAsync(rt.Config.Id).ConfigureAwait(false);
        }
    }

    public async Task StopAllAsync()
    {
        foreach (var rt in _channels.Values)
        {
            await StopChannelAsync(rt.Config.Id).ConfigureAwait(false);
        }

        if (_pumpCts is not null)
        {
            await _pumpCts.CancelAsync().ConfigureAwait(false);
            _pumpCts.Dispose();
            _pumpCts = null;
        }

        if (_pumpTask is not null)
        {
            try { await _pumpTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* 正常退出 */ }
            _pumpTask = null;
        }
    }

    public Task StartChannelAsync(Guid channelId)
    {
        if (!_channels.TryGetValue(channelId, out var rt)) return Task.CompletedTask;
        if (rt.IsWorkerAlive) return Task.CompletedTask;

        StartPumpIfNeeded();

        rt.Cts = new CancellationTokenSource();
        rt.ConsecutiveFailures = 0;
        rt.CommAlarmRaised = false;
        rt.ConfigErrorRaised = false;
        rt.LastError = null;
        rt.Worker = Task.Run(() => RunChannelAsync(rt, rt.Cts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopChannelAsync(Guid channelId)
    {
        if (!_channels.TryGetValue(channelId, out var rt)) return;

        if (rt.Cts is not null)
        {
            await rt.Cts.CancelAsync().ConfigureAwait(false);
            rt.Cts.Dispose();
            rt.Cts = null;
        }

        if (rt.Worker is not null)
        {
            try { await rt.Worker.ConfigureAwait(false); }
            catch (OperationCanceledException) { /* 正常退出 */ }
            rt.Worker = null;
        }

        try { await rt.Driver.CloseAsync().ConfigureAwait(false); }
        catch { /* 关闭失败无需抛出 */ }

        SetState(rt, ChannelState.Stopped);
    }

    // ---------- 采集主循环 ----------

    private async Task RunChannelAsync(ChannelRuntime rt, CancellationToken ct)
    {
        var config = rt.Config;

        SetState(rt, ChannelState.Connecting);
        try
        {
            await rt.Driver.OpenAsync(ct).ConfigureAwait(false);
            SetState(rt, ChannelState.Running);
            rt.StartedAt = DateTimeOffset.Now;
        }
        catch (Exception ex)
        {
            rt.LastError = ex.Message;
            SetState(rt, ChannelState.Faulted);
            Alarms.RaiseCustom(config.Id, config.Name, AlarmSeverity.Critical,
                $"{config.Name} 连接失败：{ex.Message}");
            return;
        }

        var period = Math.Max(10, config.PollIntervalMs);
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(period));

        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                var points = config.Points;
                if (points.Count == 0) continue;

                foreach (var point in points.ToArray())
                {
                    if (ct.IsCancellationRequested) return;

                    var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    var read = await ReadWithRetryAsync(rt, point, ct).ConfigureAwait(false);

                    if (read.Ok)
                    {
                        rt.ConsecutiveFailures = 0;
                        rt.ConfigErrorRaised = false;
                        if (rt.CommAlarmRaised)
                        {
                            rt.CommAlarmRaised = false;
                            Alarms.RaiseCustom(config.Id, config.Name, AlarmSeverity.Info,
                                $"{config.Name} 通讯已恢复");
                        }

                        var value = point.ApplyTransform(read.Raw);
                        var envelope = new SampleEnvelope(config.Id, point.Id, new Sample(ts, value));
                        if (!_pipeline.Writer.TryWrite(envelope))
                            Interlocked.Increment(ref _droppedSamples);

                        rt.MarkGood();
                    }
                    else
                    {
                        // 停止采集时，正在飞的那一次请求必然以"取消"收场，
                        // 底层会把它翻译成"等待响应超时"。这不是设备故障，不能算失败：
                        // 否则曲线上会留下一个假的 NaN 断点，ConsecutiveFailures 自增，
                        // 极端情况下还会在用户点"全部停止"的瞬间误报"通讯中断"。
                        if (ct.IsCancellationRequested) return;

                        rt.MarkBad();

                        // 失败也要留痕，曲线上表现为断点而不是"看起来一切正常"
                        var envelope = new SampleEnvelope(config.Id, point.Id, Sample.Bad(ts));
                        _pipeline.Writer.TryWrite(envelope);

                        // 关键区分：设备"回了异常帧"说明链路是通的，是配置不匹配
                        // （典型如点位功能码 0x03 而从站只支持 0x01）。
                        // 这种情况重连一百次也不会好，反而会让通道反复断开重建 —— 用户看到的就是"一换功能码就掉线"。
                        // 所以这里只报一次提示性报警，通道保持运行。
                        if (read.DeviceResponded)
                        {
                            rt.ConsecutiveFailures = 0;
                            rt.LastError = read.Error;

                            if (!rt.ConfigErrorRaised)
                            {
                                rt.ConfigErrorRaised = true;
                                Alarms.RaiseCustom(config.Id, config.Name, AlarmSeverity.Warning,
                                    $"{config.Name} · {point.Name} 读取失败：{read.Error}" +
                                    $"（设备已响应，链路正常，请检查该点位的功能码与地址配置）");
                            }

                            continue;
                        }

                        rt.ConsecutiveFailures++;
                        rt.LastError = read.Error;

                        if (rt.ConsecutiveFailures >= config.RetryCount + 2)
                        {
                            if (!rt.CommAlarmRaised)
                            {
                                rt.CommAlarmRaised = true;
                                Alarms.RaiseCustom(config.Id, config.Name, AlarmSeverity.Critical,
                                    $"{config.Name} 通讯中断：{read.Error}");
                            }

                            if (config.AutoReconnect)
                            {
                                var recovered = await TryReconnectAsync(rt, ct).ConfigureAwait(false);
                                if (recovered) break;
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { /* 正常停止 */ }
        catch (Exception ex)
        {
            rt.LastError = ex.Message;
            SetState(rt, ChannelState.Faulted);
        }
    }

    private async Task<ReadOutcome> ReadWithRetryAsync(
        ChannelRuntime rt, PointDefinition point, CancellationToken ct)
    {
        var config = rt.Config;
        string? lastError = null;

        for (var attempt = 0; attempt <= config.RetryCount; attempt++)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(config.TimeoutMs);

                var result = await rt.Driver.ReadPointAsync(config, point, timeoutCts.Token).ConfigureAwait(false);
                if (result.Success) return new ReadOutcome(true, result.RawValue, null, false);

                lastError = result.Error;

                // 设备明确回了异常帧：重试没有意义，直接把结论交给上层去提示配置问题
                if (result.DeviceResponded)
                    return new ReadOutcome(false, double.NaN, lastError, true);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = $"读取超时（{config.TimeoutMs}ms）";
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
            }

            if (attempt < config.RetryCount)
            {
                try { await Task.Delay(20, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }

        return new ReadOutcome(false, double.NaN, lastError ?? "未知错误", false);
    }

    /// <summary>一次点位读取（含重试）的最终结论。</summary>
    private readonly record struct ReadOutcome(bool Ok, double Raw, string? Error, bool DeviceResponded);


    /// <summary>指数退避重连，避免设备掉线时疯狂重试打满 CPU 和日志。</summary>
    private async Task<bool> TryReconnectAsync(ChannelRuntime rt, CancellationToken ct)
    {
        SetState(rt, ChannelState.Reconnecting);

        var backoff = 1000;
        for (var i = 0; i < 6 && !ct.IsCancellationRequested; i++)
        {
            try
            {
                await Task.Delay(backoff, ct).ConfigureAwait(false);
                await rt.Driver.CloseAsync().ConfigureAwait(false);
                await rt.Driver.OpenAsync(ct).ConfigureAwait(false);
                SetState(rt, ChannelState.Running);
                rt.ConsecutiveFailures = 0;
                return true;
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (Exception ex)
            {
                rt.LastError = ex.Message;
                backoff = Math.Min(backoff * 2, 30_000);
            }
        }

        SetState(rt, ChannelState.Faulted);
        return false;
    }

    // ---------- 汇聚消费者 ----------

    private async Task PumpAsync(CancellationToken ct)
    {
        var reader = _pipeline.Reader;
        var batch = new List<SampleEnvelope>(BatchSize);

        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (reader.TryRead(out var envelope) && batch.Count < BatchSize)
                {
                    batch.Add(envelope);
                }

                for (var i = 0; i < batch.Count; i++)
                {
                    var env = batch[i];
                    var store = Hub.Get(env.PointId);
                    if (store is null) continue;

                    store.Append(env.Sample);
                    Alarms.Evaluate(store.ChannelId, store.ChannelName, store.Definition, env.Sample);
                }

                batch.Clear();
            }
        }
        catch (OperationCanceledException) { /* 正常退出 */ }
    }

    private void StartPumpIfNeeded()
    {
        if (!IsPumpRunning)
        {
            _pumpCts ??= new CancellationTokenSource();
            _pumpTask = Task.Run(() => PumpAsync(_pumpCts.Token), CancellationToken.None);
        }
    }

    private void SetState(ChannelRuntime rt, ChannelState state)
    {
        if (rt.State == state) return;
        rt.State = state;
        ChannelStateChanged?.Invoke(rt);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAllAsync().ConfigureAwait(false);
        foreach (var rt in _channels.Values)
        {
            await rt.Driver.DisposeAsync().ConfigureAwait(false);
        }

        _pumpCts?.Dispose();
    }
}

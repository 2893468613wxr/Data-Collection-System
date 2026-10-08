using System.Collections.Concurrent;
using WpfApp22.Core.Models;

namespace WpfApp22.Core.Services;

/// <summary>
/// 数据总线：所有通道的采样最终汇聚到这里，按点位分桶存放。
/// 它是采集侧与展示侧之间唯一的共享状态，UI 只读不写。
/// </summary>
public sealed class DataHub
{
    private readonly ConcurrentDictionary<Guid, SeriesStore> _stores = new();

    public IEnumerable<SeriesStore> All => _stores.Values;

    public SeriesStore GetOrCreate(Guid channelId, string channelName, PointDefinition point, int capacity)
    {
        return _stores.GetOrAdd(point.Id, _ => new SeriesStore(channelId, channelName, point, capacity));
    }

    public SeriesStore? Get(Guid pointId) => _stores.TryGetValue(pointId, out var s) ? s : null;

    public bool TryRemove(Guid pointId) => _stores.TryRemove(pointId, out _);

    public void RemoveChannel(Guid channelId)
    {
        foreach (var kv in _stores)
        {
            if (kv.Value.ChannelId == channelId) _stores.TryRemove(kv.Key, out _);
        }
    }

    /// <summary>清空所有历史曲线，但保留点位定义（用于"清屏重新记录"）。</summary>
    public void ClearBuffers()
    {
        foreach (var store in _stores.Values)
        {
            store.Buffer.Clear();
        }
    }
}

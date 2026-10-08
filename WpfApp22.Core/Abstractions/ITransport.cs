namespace WpfApp22.Core.Abstractions;

/// <summary>
/// 字节流传输抽象。串口、TCP、模拟器都实现同一个接口，
/// 上层驱动只管"发出去、收回来"，不关心底下是 COM3 还是 Socket。
/// </summary>
public interface ITransport : IAsyncDisposable
{
    bool IsConnected { get; }

    string DisplayName { get; }

    Task OpenAsync(CancellationToken cancellationToken = default);

    Task CloseAsync();

    /// <summary>
    /// 读取数据到缓冲区，返回实际读取的字节数。
    /// 使用 Memory&lt;byte&gt; 而非 byte[]，方便底层直接对接 NetworkStream / SerialPort.BaseStream。
    /// </summary>
    ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default);

    ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default);

    /// <summary>丢弃接收缓冲中的残留数据，用于通讯出错后重新同步帧边界。</summary>
    void DiscardInputBuffer();
}

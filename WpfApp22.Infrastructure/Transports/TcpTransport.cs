using System.Net.Sockets;
using WpfApp22.Core.Abstractions;

namespace WpfApp22.Infrastructure.Transports;

/// <summary>
/// TCP 传输。使用 NetworkStream 原生的 Memory&lt;byte&gt; 异步读写，
/// 并且 ReceiveTimeout / SendTimeout 保证不会因为对端无响应而永久挂起。
/// </summary>
public sealed class TcpTransport : ITransport
{
    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;

    private TcpClient? _client;
    private NetworkStream? _stream;

    public TcpTransport(string host, int port, int timeoutMs = 1000)
    {
        _host = host;
        _port = port;
        _timeoutMs = timeoutMs;
    }

    public bool IsConnected => _client is { Connected: true } && _stream is not null;

    public string DisplayName => $"{_host}:{_port}";

    public async Task OpenAsync(CancellationToken cancellationToken = default)
    {
        var client = new TcpClient
        {
            ReceiveTimeout = _timeoutMs,
            SendTimeout = _timeoutMs,
            NoDelay = true
        };

        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        connectCts.CancelAfter(_timeoutMs);

        try
        {
            await client.ConnectAsync(_host, _port, connectCts.Token).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw new IOException($"无法连接到 {_host}:{_port}");
        }

        _client = client;
        _stream = client.GetStream();
    }

    public Task CloseAsync()
    {
        _stream?.Dispose();
        _stream = null;
        _client?.Dispose();
        _client = null;
        return Task.CompletedTask;
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("TCP 连接未建立");
        return stream.ReadAsync(buffer, cancellationToken);
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        var stream = _stream ?? throw new InvalidOperationException("TCP 连接未建立");
        return stream.WriteAsync(data, cancellationToken);
    }

    public void DiscardInputBuffer()
    {
        // NetworkStream 没有丢弃缓冲的 API，改为把残留字节读完
        if (_stream is null || !_stream.DataAvailable) return;
        var scratch = new byte[4096];
        try
        {
            while (_stream.DataAvailable)
            {
                if (_stream.Read(scratch, 0, scratch.Length) <= 0) break;
            }
        }
        catch { /* 忽略 */ }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }
}

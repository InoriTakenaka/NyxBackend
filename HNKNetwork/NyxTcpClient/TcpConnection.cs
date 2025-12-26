using System.Net;
using System.Net.Sockets;

namespace NyxTcpClient;

class TcpConnection : IDisposable
{
    public event Action<string> OnSend;
    public event Action<string> OnReceive;
    public event Action? OnStartConnect;
    public event Action? OnConnected;
    public event Action? OnDisconnected;
    public event Action<Exception>? OnConnectionFailed;

    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan HealthCheckTimeout { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);
    public int MaxRetryCount { get; set; } = 10;
    public bool IsConnected => _isConnected;

    private const byte StartMarker = 0x0F;
    private const byte EndMarker = 0xFF;
    private static readonly byte[] HealthCheckResponse = { 0x0F, 0x01, 0xFF };
    private const int BufferSize = 1024;
    private bool _isConnected = false;


    private CancellationTokenSource? _connectionCTS;
    private CancellationTokenSource? _healthCheckCTS;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly IPEndPoint _endPoint;
    private TcpClient? _client;

    public TcpConnection(IPAddress address, int port)
    {
        _endPoint = new IPEndPoint(address, port);
        OnSend += s => Console.WriteLine($"{DateTime.Now.ToShortTimeString}|SEND|{s}");
        OnReceive += s => Console.WriteLine($"{DateTime.Now.ToShortTimeString}|RECEIVE|{s}");
    }

    public async Task StartAsync(CancellationToken token = default)
    {
        _connectionCTS = CancellationTokenSource.CreateLinkedTokenSource(token);
        await BeginConnectAsync(_connectionCTS.Token);
    }

    public void Stop()
    {
        _connectionCTS?.Cancel();
        _connectionCTS = null;
        _healthCheckCTS?.Cancel();
        _healthCheckCTS = null;
        Disconnect();
    }

    private async Task BeginConnectAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _connectLock.WaitAsync(token);
                if (_isConnected) return;
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeoutCts.CancelAfter(ConnectTimeout);

                await ConnectAsync(timeoutCts.Token);
                OnConnected?.Invoke();

                _healthCheckCTS = new CancellationTokenSource();
                _ = Task.Run(() => HealthCheckAsync(_healthCheckCTS.Token), token);

                return;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                await Task.Delay(ReconnectDelay, token);
            }
            catch (Exception ex)
            {
                OnConnectionFailed?.Invoke(ex);
                await Task.Delay(ReconnectDelay, token);
            }
            finally
            {
                _connectLock.Release();
            }
        }
    }

    private async Task ConnectAsync(CancellationToken token)
    {
        Disconnect();

        var client = new TcpClient();
        var connectTask = client.ConnectAsync(_endPoint);
        var completeTask = await Task.WhenAny(connectTask, Task.Delay(Timeout.Infinite, token));
        if (completeTask != connectTask)
        {
            client.Dispose();
            token.ThrowIfCancellationRequested();
            throw new TimeoutException("Connection Timed Out");
        }
        await connectTask;
        _client = client;
        _isConnected = true;
    }

    private async Task HealthCheckAsync(CancellationToken token)
    {
        while (_isConnected && !token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, token);
                NetworkStream? stream = _client?.GetStream();

                if (stream == null || true == !_client?.Connected) break;
                var packet = new byte[] { StartMarker, 0x01, EndMarker };
                await stream.WriteAsync(packet.AsMemory(), token);

                using var responseCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                responseCts.CancelAfter(HealthCheckTimeout);

                var responseBuffer = new byte[3];
                int bytesRead = await stream.ReadAsync(responseBuffer, responseCts.Token).ConfigureAwait(false);
                if (bytesRead != 3 || !responseBuffer.SequenceEqual(HealthCheckResponse))
                {
                    throw new InvalidDataException("Health check failure due to response mismatch.");
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
                await DisconnectAsync();
                break;
            }
        }
    }

    public async Task<bool> SendAndReceiveAsync(byte[] data, Action<byte[]> onReceive)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(onReceive);
        if (!_isConnected || _client == null) return false;
        try
        {
            NetworkStream stream = _client.GetStream();
            if (stream == null || !stream.CanWrite || !stream.CanRead) return false;
            stream.WriteTimeout = 5000;
            await stream.WriteAsync(data).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);

            using var ms = new MemoryStream();
            byte[] buffer = new byte[BufferSize];
            int bytesRead;

            var receiveCTS = new CancellationTokenSource();
            receiveCTS.CancelAfter(TimeSpan.FromSeconds(5));
            bytesRead = await stream.ReadAsync(buffer, receiveCTS.Token).ConfigureAwait(false);
            if (bytesRead > 0)
            {
                await ms.WriteAsync(buffer.AsMemory(0, bytesRead)).ConfigureAwait(false);
                Console.WriteLine($"[CLIENT] Total received: {buffer.Length} bytes.");
            }


            if (ms.Length > 0)
            {
                onReceive(ms.ToArray());
                return true;
            }
        }
        catch (IOException ex) when (ex.InnerException is SocketException socketException)
        {
            Console.WriteLine($"Network error: {socketException.SocketErrorCode}");
        }
        catch (TimeoutException)
        {
            Console.WriteLine("Operation timed out.");
        }
        catch (ObjectDisposedException)
        {
            Console.WriteLine("Connection was closed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
        }

        return false;
    }

    private async Task DisconnectAsync()
    {
        if (Interlocked.Exchange(ref _isConnected, false))
        {
            _healthCheckCTS?.Cancel();
            try
            {
                _client?.Close();
            }
            finally
            {
                _client = null;
                OnDisconnected?.Invoke();
                if (false == _connectionCTS?.IsCancellationRequested)
                {
                    _ = Task.Run(() => BeginConnectAsync(_connectionCTS.Token));
                }
            }
        }
    }

    private void Disconnect()
    {
        _ = DisconnectAsync();
    }
    public void Dispose()
    {
        Stop();
        _connectLock.Dispose();
        GC.SuppressFinalize(this);
    }
}

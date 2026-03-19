using NyxNetwork;
using System.Buffers;
using System.Net;
using System.Net.Sockets;

namespace NyxNetwork.Tcp;

public class TcpConnection : IDisposable
{
    //public event Action<string> OnSend;
    //public event Action<string> OnReceive;
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
    public bool EnableHealthCheck {
        get => _enableHealthCheck;
        set {
            _enableHealthCheck = value;
        }
    }

    private const byte StartMarker = 0x0F;
    private const byte EndMarker = 0xFF;
    private static readonly byte[] HealthCheckResponse = { 0x0F, 0x01, 0xFF };
    private const int BufferSize = 1024;
    private bool _isConnected = false;
    private bool _enableHealthCheck = false;
    private readonly byte[] _sharedBuffer = ArrayPool<byte>.Shared.Rent(65535);
    private int _bufferCount = 0;
    private CancellationTokenSource? _connectionCTS;
    private CancellationTokenSource? _healthCheckCTS;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly SemaphoreSlim _streamLock = new(1, 1);
    private readonly IPEndPoint _endPoint;
    private TcpClient? _client;

    public TcpConnection(IPAddress address, int port)
    {
        _endPoint = new IPEndPoint(address, port);
        //OnSend += s => Console.WriteLine($"{DateTime.Now.ToShortTimeString}|SEND|{s}");
        //OnReceive += s => Console.WriteLine($"{DateTime.Now.ToShortTimeString}|RECEIVE|{s}");
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
                if (_enableHealthCheck)
                {
                    _healthCheckCTS = new CancellationTokenSource();
                    _ = Task.Run(() => HealthCheckAsync(_healthCheckCTS.Token), token);
                }

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
        try
        {
            Disconnect();

            var client = new TcpClient();
            //client.ReceiveTimeout = 5000;
            //client.SendTimeout = 5000;
            await client.ConnectAsync(_endPoint, token);

            _client = client;
            _isConnected = true;
        }
        catch (Exception ex)
        {
            _isConnected = false;
            throw new Exception($"Failed to connect to {_endPoint.Address}:{_endPoint.Port}. {ex.Message}", ex);
        }
    }

    private async Task HealthCheckAsync(CancellationToken token)
    {

    }

    public async Task<bool> SendAndReceiveAsync(ReadOnlyMemory<byte> data, Action<DevicePacket> onResponse, int timeoutMS = 5000)
    {
        if (!_isConnected || _client == null) return false;

        using var cts = new CancellationTokenSource(timeoutMS);
        var stream = _client.GetStream();

        await _streamLock.WaitAsync(cts.Token);
        try
        {
            await stream.WriteAsync(data).ConfigureAwait(false);

            while (!cts.IsCancellationRequested)
            {
                // read existing data in buffer
                var currentSpan = _sharedBuffer.AsSpan(0, _bufferCount);
                int consumed = DevicePacket.TryParse(currentSpan, out var packet);
                if (consumed > 0)
                {
                    int remaining = _bufferCount - consumed;
                    if (remaining > 0)
                    {
                        _sharedBuffer.AsSpan(consumed, remaining).CopyTo(_sharedBuffer);
                    }
                    _bufferCount = remaining;
                    if (!packet.Command.IsEmpty)
                    {
                        onResponse(packet);
                        return true;
                    }
                    if (_bufferCount > 0) continue; // try parse again if there's still data in buffer
                }

                // read data from network stream
                int spaceLeft = _sharedBuffer.Length - _bufferCount;
                if (spaceLeft <= 0)
                {
                    _bufferCount = 0; // reset buffer if overflow
                    throw new InvalidDataException("Buffer overflow: incoming data exceeds buffer size.");
                }

                int bytesRead = await stream.ReadAsync(_sharedBuffer.AsMemory(_bufferCount, spaceLeft), cts.Token).ConfigureAwait(false);
                if (bytesRead == 0) throw new IOException("Connection closed by remote host.");
                _bufferCount += bytesRead;
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
        finally
        {
            _streamLock.Release();
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

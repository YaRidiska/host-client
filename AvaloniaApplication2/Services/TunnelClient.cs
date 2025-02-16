using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Security.Cryptography;

using System.Text.Json;
using System.Runtime.Serialization.Formatters.Binary;
using AvaloniaApplication2.Models;

namespace AvaloniaApplication2.Services
{
    public class DataConnectionInfo
    {
        public Guid Id { get; } = Guid.NewGuid();

        private readonly string _localIPAddress;
        private readonly string _remoteIPAddress;

        public TcpClient TcpClient { get; }
        public NetworkStream? NetworkStream => TcpClient?.GetStream();
        public DateTime ConnectedAt { get; }
        public long BytesSent { get; set; }
        public long BytesReceived { get; set; }
        public bool IsClosed { get; set; } = false;

        public string LocalIPAddress => _localIPAddress;
        public string RemoteIPAddress => _remoteIPAddress;

        public DataConnectionInfo(TcpClient client)
        {
            TcpClient = client;
            ConnectedAt = DateTime.Now;

            var localEp = client.Client.LocalEndPoint;
            _localIPAddress = localEp?.ToString() ?? "Unknown";

            var remoteEp = client.Client.RemoteEndPoint;
            _remoteIPAddress = remoteEp?.ToString() ?? "Unknown";
        }
    }

    public class TunnelClient
    {
        private readonly string _serverIp;
        private readonly int _serverPort;
        private readonly int _localPort;
        private readonly string _clientId;
        private readonly string _subdomain;

        private TcpClient? _controlClient;
        private StreamReader? _reader;
        private StreamWriter? _writer;

        private bool _isRunning;

        public event EventHandler? TunnelStopped;

        public int RemotePort { get; private set; }


        private ConcurrentDictionary<Guid, DataConnectionInfo> _dataConnections = new ConcurrentDictionary<Guid, DataConnectionInfo>();

        public ConcurrentDictionary<Guid, DataConnectionInfo> DataConnections => _dataConnections;



        public Action<string>? LogAction { get; set; }

        public bool IsRunning => _isRunning;
        public string ServerIp => _serverIp;
        public int ServerPort => _serverPort;
        public int LocalPort => _localPort;


        public TunnelClient(string serverIp, int serverPort, int localPort, UserData userData)
        {
            _serverIp = serverIp;
            _serverPort = serverPort;
            _localPort = localPort;
            _clientId = userData.UserGuid;
            _subdomain = userData.Subdomain;
        }


        private async Task CreateDataConnection(int rp)
        {
            try
            {
                var tcp = new TcpClient();
                await tcp.ConnectAsync(_serverIp, _serverPort);

                var stream = tcp.GetStream();
                string cmd = $"DATA_CONNECTION {rp} {_clientId}\n";
                byte[] buf = Encoding.UTF8.GetBytes(cmd);
                await stream.WriteAsync(buf, 0, buf.Length);

                var connectionInfo = new DataConnectionInfo(tcp);
                _dataConnections.TryAdd(connectionInfo.Id, connectionInfo);
                LogAction?.Invoke($"[Client] New data connection for port {rp} created (clientId={_clientId})");

                _ = Task.Run(() => ProxyDataConnectionAsync(connectionInfo));
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"[Client] CreateDataConnection error: {ex.Message}");
            }
        }

        public async Task StartAsync()
        {
            if (_isRunning) return;

            try
            {
                _controlClient = new TcpClient();
                _controlClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
                await _controlClient.ConnectAsync(_serverIp, _serverPort);
                _reader = new StreamReader(_controlClient.GetStream(), Encoding.UTF8);
                _writer = new StreamWriter(_controlClient.GetStream(), new UTF8Encoding(false)) { AutoFlush = true };

                string cmd = $"REQUEST_TUNNEL {_localPort} {_clientId} {_subdomain}\n";
                await _writer.WriteLineAsync(cmd);

                var resp = await _reader.ReadLineAsync();
                if (resp == null || !resp.StartsWith("OK_TUNNEL_CREATED"))
                {
                    LogAction?.Invoke("[Client] Server error or no response");
                    Stop();
                    return;
                }

                var parts = resp.Split(' ');
                if (parts.Length < 2 || !int.TryParse(parts[1], out int rp))
                {
                    LogAction?.Invoke("[Client] Invalid server response");
                    Stop();
                    return;
                }

                RemotePort = rp;
                _isRunning = true;
                LogAction?.Invoke($"[Client] Tunnel created on port {rp}, clientId={_clientId}");

                _ = Task.Run(ReceiveLoop);
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"[Client] StartAsync error: {ex.Message}");
                Stop();
            }
        }



        private async Task ReceiveLoop()
        {
            LogAction?.Invoke("[Client] ReceiveLoop started");
            try
            {
                while (_isRunning && _reader != null)
                {
                    var line = await _reader.ReadLineAsync();
                    if (line == null)
                    {
                        LogAction?.Invoke("[Client] Server closed control connection");
                        break;
                    }
                    LogAction?.Invoke($"[Client] Control msg: " + line);

                    if (line.StartsWith("REQUEST_NEW_DATA_CONNECTION"))
                    {
                        var parts = line.Split(' ');
                        if (parts.Length == 3)
                        {
                            if (int.TryParse(parts[1], out int rp))
                            {
                                string serverClientId = parts[2];
                                if (serverClientId == _clientId)
                                {
                                    await CreateDataConnection(rp);
                                }
                                else
                                {
                                    LogAction?.Invoke($"[Client] Got REQUEST_NEW_DATA_CONNECTION for unknown clientId={serverClientId}");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogAction?.Invoke("[Client] ReceiveLoop error: " + ex.Message);
            }
            finally
            {
                LogAction?.Invoke("[Client] ReceiveLoop ended");
                Stop();
            }
        }

        /// <summary>
        /// Двустороннее проксирование
        /// </summary>
        private async Task ProxyDataConnectionAsync(DataConnectionInfo connectionInfo)
        {
            var dataStream = connectionInfo.NetworkStream;
            if (dataStream == null)
            {
                LogAction?.Invoke($"[Client] NetworkStream is null for connection {connectionInfo.Id}");
                return;
            }

            LogAction?.Invoke($"[Client] Starting ProxyDataConnectionAsync for {connectionInfo.Id}");

            TimeSpan idleTimeout = TimeSpan.FromSeconds(30);

            DateTime lastActivity = DateTime.UtcNow;

            using var cts = new CancellationTokenSource();

            try
            {
                using var localClient = new TcpClient();
                LogAction?.Invoke("[Client] Connecting to local Minecraft server...");
                await localClient.ConnectAsync("127.0.0.1", _localPort);
                using var localStream = localClient.GetStream();
                LogAction?.Invoke("[Client] Connected to local Minecraft server.");

                var monitorTask = Task.Run(async () =>
                {
                    while (!cts.Token.IsCancellationRequested)
                    {
                        if (DateTime.UtcNow - lastActivity > idleTimeout)
                        {
                            LogAction?.Invoke($"[Client] Idle timeout ({idleTimeout.TotalSeconds}s) reached. Closing data connection.");
                            cts.Cancel();
                            break;
                        }
                        await Task.Delay(1000, cts.Token);
                    }
                }, cts.Token);

                var t1 = CopyWithActivityAsync(dataStream, localStream, connectionInfo, "Server->Local",
                                               () => lastActivity = DateTime.UtcNow,
                                               cts.Token);

                var t2 = CopyWithActivityAsync(localStream, dataStream, connectionInfo, "Local->Server",
                                               () => lastActivity = DateTime.UtcNow,
                                               cts.Token);

                await Task.WhenAny(Task.WhenAll(t1, t2), monitorTask);

                if (!cts.Token.IsCancellationRequested)
                {
                    cts.Cancel();
                }

                LogAction?.Invoke("[Client] ProxyDataConnectionAsync copy ended normally.");
            }
            catch (OperationCanceledException)
            {
                LogAction?.Invoke("[Client] ProxyDataConnection was canceled (likely idle timeout).");
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"[Client] ProxyDataConnection error: {ex}");
            }
            finally
            {
                connectionInfo.IsClosed = true;
                _dataConnections.TryRemove(connectionInfo.Id, out _);
                LogAction?.Invoke($"[Client] Data connection {connectionInfo.Id} closed.");
            }
        }

        private async Task CopyWithActivityAsync(
            Stream input,
            Stream output,
            DataConnectionInfo connectionInfo,
            string direction,
            Action onDataTransfer,
            CancellationToken cancellationToken)
        {
            try
            {
                byte[] buffer = new byte[8192];
                while (true)
                {
                    int bytesRead = await input.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                    if (bytesRead == 0)
                    {
                        break;
                    }

                    onDataTransfer?.Invoke();

                    await output.WriteAsync(buffer, 0, bytesRead, cancellationToken);
                    await output.FlushAsync(cancellationToken);

                    if (direction == "Server->Local")
                    {
                        connectionInfo.BytesReceived += bytesRead;
                    }
                    else if (direction == "Local->Server")
                    {
                        connectionInfo.BytesSent += bytesRead;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                LogAction?.Invoke($"[Client] CopyWithActivityAsync {direction} canceled (idle or external).");
            }
            catch (IOException ioEx) when (ioEx.InnerException is SocketException)
            {
                LogAction?.Invoke($"[Client] CopyWithActivityAsync {direction} Socket error: {ioEx.Message}");
            }
            catch (Exception ex)
            {
                LogAction?.Invoke($"[Client] CopyWithActivityAsync {direction} error: {ex.Message}");
            }
        }


        public void Stop()
        {
            if (!_isRunning) return;
            _isRunning = false;

            try { _controlClient?.Close(); } catch { }

            foreach (var kvp in _dataConnections)
            {
                var conn = kvp.Value;
                try { conn.TcpClient.Close(); } catch { }
            }
            _dataConnections.Clear();

            LogAction?.Invoke("[Client] Stopped");

            TunnelStopped?.Invoke(this, EventArgs.Empty);
        }
    }
}

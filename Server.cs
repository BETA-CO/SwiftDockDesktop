using System;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace SwiftDock
{
    public class Server
    {
        private const int TcpPort = 19001;
        private const int UdpPort = 19002;

        private TcpListener? _tcpListener;
        private TcpClient? _activeClient;
        private NetworkStream? _activeStream;
        private CancellationTokenSource? _cts;

        public event Action<string>? ClientConnected;
        public event Action? ClientDisconnected;
        public event Action<string>? PinGenerated;
        public event Action? PairingSuccessful;
        public event Action<string>? ProfileChangeRequested;

        public string CurrentPin { get; private set; } = "";
        public bool IsClientConnected => _activeClient != null && _activeClient.Connected;
        public string ConnectedDeviceName { get; private set; } = "";
        public bool IsRunning { get; private set; } = false;

        private string _deviceName = Environment.MachineName;

        public void Start(string deviceName)
        {
            _deviceName = deviceName;
            Stop();

            _cts = new CancellationTokenSource();
            GeneratePin();
            IsRunning = true;

            // Start UDP Broadcaster
            Task.Run(() => RunUdpBroadcast(_cts.Token));

            // Start TCP Control Server
            Task.Run(() => RunTcpServer(_cts.Token));
        }

        public void Stop()
        {
            IsRunning = false;
            _cts?.Cancel();
            _tcpListener?.Stop();
            DisconnectActiveClient();
            _activeClient?.Close();
        }

        public void GeneratePin()
        {
            var random = new Random();
            CurrentPin = random.Next(1000, 10000).ToString();
            PinGenerated?.Invoke(CurrentPin);
        }

        public void SendTransitionGrid()
        {
            SendPacket(new { type = "TRANSITION_GRID" });
        }

        public void SyncButtons()
        {
            SendPacket(new
            {
                type = "SYNC_BUTTONS",
                buttons = ConfigManager.CurrentButtons
            });
        }

        public void SyncProfiles()
        {
            var profiles = ConfigManager.Current.Profiles.Select(p => new { id = p.Id, name = p.Name }).ToList();
            SendPacket(new
            {
                type = "SYNC_PROFILES",
                currentProfileId = ConfigManager.Current.CurrentProfileId,
                profiles = profiles
            });
        }
        
        public void SendPerformanceUpdate(int cpu, int gpu, int ram, int temp, string wifi)
        {
            SendPacket(new
            {
                type = "PERFORMANCE_UPDATE",
                cpu = cpu,
                gpu = gpu,
                ram = ram,
                temp = temp,
                wifi = wifi
            });
        }

        private void SendPacket(object packetObj)
        {
            if (_activeStream == null || !IsClientConnected) return;

            try
            {
                string json = JsonSerializer.Serialize(packetObj) + "\n";
                byte[] data = Encoding.UTF8.GetBytes(json);
                _activeStream.Write(data, 0, data.Length);
                _activeStream.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending TCP packet: {ex.Message}");
                DisconnectActiveClient();
            }
        }

        private async Task RunUdpBroadcast(CancellationToken ct)
        {
            LogToFile("Starting UDP Broadcast loop.");
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    string broadcastMsg = $"SwiftDock-Server:{TcpPort}:{_deviceName}";
                    byte[] data = Encoding.UTF8.GetBytes(broadcastMsg);
                    LogToFile($"Preparing broadcast message: {broadcastMsg}");

                    // 1. Send via default route (bind to OS choice)
                    try
                    {
                        using (var client = new UdpClient())
                        {
                            client.EnableBroadcast = true;
                            client.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, UdpPort));
                            LogToFile("Sent broadcast to 255.255.255.255 on default route.");
                        }
                    }
                    catch (Exception ex)
                    {
                        LogToFile($"Error broadcasting on default route: {ex.Message}");
                    }

                    // 2. Send via each active non-loopback IPv4 interface
                    try
                    {
                        var interfaces = NetworkInterface.GetAllNetworkInterfaces();
                        LogToFile($"Found {interfaces.Length} network interfaces.");
                        foreach (NetworkInterface ni in interfaces)
                        {
                            LogToFile($"Interface: Name={ni.Name}, Desc={ni.Description}, Status={ni.OperationalStatus}, Type={ni.NetworkInterfaceType}");
                            if (ni.OperationalStatus != OperationalStatus.Up || 
                                ni.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                            {
                                continue;
                            }

                            IPInterfaceProperties ipProps = ni.GetIPProperties();
                            foreach (UnicastIPAddressInformation ip in ipProps.UnicastAddresses)
                            {
                                if (ip.Address.AddressFamily == AddressFamily.InterNetwork)
                                {
                                    LogToFile($"  IP Address: {ip.Address}");
                                    // A. Try standard 255.255.255.255 broadcast bound to this interface IP
                                    try
                                    {
                                        using (var client = new UdpClient(new IPEndPoint(ip.Address, 0)))
                                        {
                                            client.EnableBroadcast = true;
                                            client.Send(data, data.Length, new IPEndPoint(IPAddress.Broadcast, UdpPort));
                                            LogToFile($"  Sent 255.255.255.255 broadcast from {ip.Address}");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        LogToFile($"  Error broadcasting 255.255.255.255 from {ip.Address}: {ex.Message}");
                                    }

                                    // B. Try subnet-directed broadcast bound to this interface IP
                                    try
                                    {
                                        IPAddress subnetBroadcast = GetBroadcastAddress(ip);
                                        LogToFile($"  Subnet broadcast address computed: {subnetBroadcast}");
                                        if (!subnetBroadcast.Equals(IPAddress.Broadcast))
                                        {
                                            using (var client = new UdpClient(new IPEndPoint(ip.Address, 0)))
                                            {
                                                client.EnableBroadcast = true;
                                                client.Send(data, data.Length, new IPEndPoint(subnetBroadcast, UdpPort));
                                                LogToFile($"  Sent subnet-directed broadcast to {subnetBroadcast} from {ip.Address}");
                                            }
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        LogToFile($"  Error subnet broadcasting from {ip.Address}: {ex.Message}");
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        LogToFile($"Error enumerating interfaces for broadcast: {ex.Message}");
                    }
                }
                catch (Exception ex)
                {
                    LogToFile($"UDP Broadcast outer loop error: {ex.Message}");
                }

                // Broadcast every 2 seconds
                await Task.Delay(2000, ct);
            }
            LogToFile("UDP Broadcast loop stopped.");
        }

        private static IPAddress GetBroadcastAddress(UnicastIPAddressInformation ipInfo)
        {
            try
            {
                if (ipInfo.IPv4Mask == null)
                {
                    return IPAddress.Broadcast;
                }
                
                byte[] ipBytes = ipInfo.Address.GetAddressBytes();
                byte[] maskBytes = ipInfo.IPv4Mask.GetAddressBytes();

                if (ipBytes.Length != maskBytes.Length)
                {
                    return IPAddress.Broadcast;
                }

                byte[] broadcastBytes = new byte[ipBytes.Length];
                for (int i = 0; i < ipBytes.Length; i++)
                {
                    broadcastBytes[i] = (byte)(ipBytes[i] | ~maskBytes[i]);
                }

                return new IPAddress(broadcastBytes);
            }
            catch
            {
                return IPAddress.Broadcast;
            }
        }

        private async Task RunTcpServer(CancellationToken ct)
        {
            try
            {
                _tcpListener = new TcpListener(IPAddress.Any, TcpPort);
                _tcpListener.Start();

                while (!ct.IsCancellationRequested)
                {
                    TcpClient client = await _tcpListener.AcceptTcpClientAsync(ct);
                    // Disconnect any existing client if a new one connects
                    DisconnectActiveClient();

                    _ = Task.Run(() => HandleClientConnection(client, ct), ct);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"TCP Server error: {ex.Message}");
            }
        }

        private void DisconnectActiveClient()
        {
            if (_activeClient != null)
            {
                try
                {
                    _activeStream?.Close();
                    _activeClient.Close();
                }
                catch { }
                _activeClient = null;
                _activeStream = null;
                ConnectedDeviceName = "";
                ClientDisconnected?.Invoke();
            }
        }

        private async Task HandleClientConnection(TcpClient client, CancellationToken ct)
        {
            NetworkStream stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8);
            using var connectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);

            try
            {
                // First read: Expect AUTH or RECONNECT message within a timeout
                client.ReceiveTimeout = 5000; // 5 seconds to authenticate
                string? authLine = await reader.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(authLine))
                {
                    client.Close();
                    return;
                }

                using var doc = JsonDocument.Parse(authLine);
                var root = doc.RootElement;
                string type = root.TryGetProperty("type", out var typeProp) ? (typeProp.GetString() ?? "") : "";

                if (type.Equals("DISCOVER", StringComparison.OrdinalIgnoreCase))
                {
                    string discoverJson = JsonSerializer.Serialize(new { type = "DISCOVER_RESPONSE", deviceName = _deviceName }) + "\n";
                    byte[] discoverData = Encoding.UTF8.GetBytes(discoverJson);
                    await stream.WriteAsync(discoverData, 0, discoverData.Length, ct);
                    client.Close();
                    return;
                }

                string deviceName = root.TryGetProperty("deviceName", out var devNameProp) ? (devNameProp.GetString() ?? "Unknown Mobile") : "Unknown Mobile";

                bool authenticated = false;
                string tokenToSend = "";

                if (type.Equals("AUTH", StringComparison.OrdinalIgnoreCase))
                {
                    string pin = root.TryGetProperty("pin", out var pinProp) ? (pinProp.GetString() ?? "") : "";
                    if (pin == CurrentPin)
                    {
                        authenticated = true;
                        tokenToSend = Guid.NewGuid().ToString();

                        if (ConfigManager.Current.PairedDevices == null)
                        {
                            ConfigManager.Current.PairedDevices = new List<PairedDevice>();
                        }
                        // Remove any old pairing for the same device name to avoid duplication
                        ConfigManager.Current.PairedDevices.RemoveAll(d => d.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase));
                        ConfigManager.Current.PairedDevices.Add(new PairedDevice
                        {
                            DeviceName = deviceName,
                            Token = tokenToSend
                        });

                        // Keep single pairing properties updated for legacy/UI compatibility
                        ConfigManager.Current.PairedToken = tokenToSend;
                        ConfigManager.Current.PairedDeviceName = deviceName;

                        ConfigManager.Save();
                        PairingSuccessful?.Invoke();
                    }
                }
                else if (type.Equals("RECONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    string token = root.TryGetProperty("token", out var tokenProp) ? (tokenProp.GetString() ?? "") : "";
                    if (!string.IsNullOrEmpty(token))
                    {
                        if (ConfigManager.Current.PairedDevices != null && 
                            ConfigManager.Current.PairedDevices.Exists(d => d.Token == token))
                        {
                            authenticated = true;
                            tokenToSend = token;
                        }
                        else if (token == ConfigManager.Current.PairedToken) // Legacy fallback
                        {
                            authenticated = true;
                            tokenToSend = token;
                        }
                    }
                }

                if (!authenticated)
                {
                    string failJson = JsonSerializer.Serialize(new { type = "AUTH_RESPONSE", status = "FAILURE", reason = "Unauthorized" }) + "\n";
                    byte[] failData = Encoding.UTF8.GetBytes(failJson);
                    await stream.WriteAsync(failData, 0, failData.Length, ct);
                    client.Close();
                    return;
                }

                // Authentication Successful
                client.ReceiveTimeout = 7000; // 7 seconds timeout for active session
                _activeClient = client;
                _activeStream = stream;
                ConnectedDeviceName = deviceName;

                // Send success response
                SendPacket(new { type = "AUTH_RESPONSE", status = "SUCCESS", token = tokenToSend });
                ClientConnected?.Invoke(deviceName);

                // Auto-sync buttons and profiles
                SyncButtons();
                SyncProfiles();

                // Start heartbeat loop task
                _ = Task.Run(() => RunHeartbeatLoop(connectionCts.Token), connectionCts.Token);

                // Listen for button presses
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    string? line = await reader.ReadLineAsync(ct);
                    if (line == null) break; // Disconnected

                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        ProcessCommand(line);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error handling client: {ex.Message}");
            }
            finally
            {
                connectionCts.Cancel();
                if (client == _activeClient)
                {
                    DisconnectActiveClient();
                }
                else
                {
                    client.Close();
                }
            }
        }

        private async Task RunHeartbeatLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(3000, ct);
                    if (IsClientConnected)
                    {
                        SendPacket(new { type = "HEARTBEAT" });
                    }
                }
                catch (TaskCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Heartbeat error: {ex.Message}");
                    break;
                }
            }
        }

        private void ProcessCommand(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string type = root.GetProperty("type").GetString() ?? "";

                if (type.Equals("BUTTON_PRESS", StringComparison.OrdinalIgnoreCase))
                {
                    string buttonId = root.GetProperty("id").GetString() ?? "";
                    var button = ConfigManager.CurrentButtons.Find(b => b.Id == buttonId);
                    if (button != null)
                    {
                        ActionExecutor.ExecuteButton(button);
                    }
                }
                else if (type.Equals("CHANGE_PROFILE", StringComparison.OrdinalIgnoreCase))
                {
                    string profileId = root.GetProperty("profileId").GetString() ?? "";
                    if (!string.IsNullOrEmpty(profileId))
                    {
                        ProfileChangeRequested?.Invoke(profileId);
                    }
                }
                else if (type.Equals("HEARTBEAT_ACK", StringComparison.OrdinalIgnoreCase))
                {
                    // Heartbeat ACK received. Simply reading it resets the client's ReceiveTimeout.
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing command: {ex.Message}");
            }
        }

        private static void LogToFile(string message)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "swift_dock_debug.log");
                File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\r\n");
            }
            catch { }
        }
    }
}

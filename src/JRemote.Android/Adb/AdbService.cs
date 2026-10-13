using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using JRemote.Core.Models;
using System.Net;

namespace JRemote.Android.Adb;

public class AdbService : IDisposable
{
    private readonly IAdbClient _client;
    private readonly IAdbServer _server;
    private IDeviceMonitor? _monitor;
    private bool _isInitialized;

    public event EventHandler<DeviceData>? DeviceConnected;
    public event EventHandler<DeviceData>? DeviceDisconnected;
    public event EventHandler<DeviceData>? DeviceChanged;

    public IAdbClient Client => _client;
    public bool IsInitialized => _isInitialized;

    public AdbService(IAdbClient? client = null, IAdbServer? server = null)
    {
        _client = client ?? new AdbClient();
        _server = server ?? new AdbServer();
    }

    public async Task<bool> InitializeAsync(string? explicitAdbPath = null, CancellationToken cancellationToken = default)
    {
        if (_isInitialized) return true;

        var adbPath = explicitAdbPath ?? AdbLocator.FindAdbPath();
        if (string.IsNullOrEmpty(adbPath) || !File.Exists(adbPath))
        {
            return false;
        }

        var result = await Task.Run(() =>
        {
            var serverStatus = _server.GetStatus();
            if (!serverStatus.IsRunning)
            {
                var startResult = _server.StartServer(adbPath, restartServerIfNewer: false);
                return startResult == StartServerResult.Started || startResult == StartServerResult.AlreadyRunning;
            }
            return true;
        }, cancellationToken);

        if (result)
        {
            _isInitialized = true;
            StartDeviceMonitoring();
        }

        return result;
    }

    public void StartDeviceMonitoring()
    {
        if (_monitor != null) return;

        try
        {
            _monitor = new DeviceMonitor(new AdbSocket(new IPEndPoint(IPAddress.Loopback, AdbClient.AdbServerPort)));
            _monitor.DeviceConnected += (s, e) => DeviceConnected?.Invoke(this, e.Device);
            _monitor.DeviceDisconnected += (s, e) => DeviceDisconnected?.Invoke(this, e.Device);
            _monitor.DeviceChanged += (s, e) => DeviceChanged?.Invoke(this, e.Device);
            _monitor.Start();
        }
        catch
        {
            // Logging or fallback polling if monitor fails
        }
    }

    public async Task<IEnumerable<DeviceData>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        if (!_isInitialized) return Enumerable.Empty<DeviceData>();
        return await Task.Run(() => _client.GetDevices(), cancellationToken);
    }

    public async Task<bool> ConnectWifiDeviceAsync(string ipAddress, int port = 5555)
    {
        if (!_isInitialized) return false;
        try
        {
            var result = await Task.Run(() => _client.Connect(new DnsEndPoint(ipAddress, port)));
            return result.Contains("connected", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> EnableWirelessAdbAsync(DeviceData device, int port = 5555)
    {
        if (!_isInitialized) return false;
        try
        {
            await _client.ExecuteRemoteCommandAsync($"tcpip {port}", device, CancellationToken.None);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        _monitor?.Dispose();
        _monitor = null;
    }
}

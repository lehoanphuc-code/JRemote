using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using JRemote.Core.Logging;
using JRemote.Core.Models;
using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

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

    public async Task<(bool Success, string Output)> ExecuteAdbCliAsync(string arguments, CancellationToken cancellationToken = default)
    {
        var adbPath = AdbLocator.FindAdbPath();
        if (string.IsNullOrEmpty(adbPath) || !File.Exists(adbPath))
        {
            return (false, "Không tìm thấy file adb.exe");
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = adbPath,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc == null) return (false, "Không thể khởi chạy adb.exe");

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = proc.StandardError.ReadToEndAsync(cancellationToken);

            await proc.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            var combined = (stdout + "\n" + stderr).Trim();

            return (proc.ExitCode == 0, combined);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    public async Task<string?> GetDeviceWifiIpAsync(DeviceData device, CancellationToken cancellationToken = default)
    {
        // 1. Thử lệnh: ip route (lấy src IP của default route hoặc wlan0)
        var (_, routeOut) = await ExecuteAdbCliAsync($"-s {device.Serial} shell ip route", cancellationToken);
        var matchRoute = Regex.Match(routeOut, @"(?:src|dev\s+wlan\d+)\s+([0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3})");
        if (matchRoute.Success)
        {
            var ip = matchRoute.Groups[1].Value;
            if (IsValidIpv4(ip)) return ip;
        }

        // 2. Thử lệnh: ip -f inet addr show wlan0
        var (_, addrOut) = await ExecuteAdbCliAsync($"-s {device.Serial} shell ip -f inet addr show wlan0", cancellationToken);
        var matchAddr = Regex.Match(addrOut, @"inet\s+([0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3})");
        if (matchAddr.Success)
        {
            var ip = matchAddr.Groups[1].Value;
            if (IsValidIpv4(ip)) return ip;
        }

        // 3. Fallback: getprop dhcp.wlan0.ipaddress
        var (_, propOut) = await ExecuteAdbCliAsync($"-s {device.Serial} shell getprop dhcp.wlan0.ipaddress", cancellationToken);
        var cleanProp = propOut.Trim();
        if (IsValidIpv4(cleanProp))
        {
            return cleanProp;
        }

        return null;
    }

    private static bool IsValidIpv4(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip)) return false;
        return IPAddress.TryParse(ip, out var parsed) &&
               parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
               !IPAddress.IsLoopback(parsed) &&
               ip != "0.0.0.0";
    }

    public async Task<(bool Success, string Message, string? ConnectedIpPort)> SwitchUsbToWirelessAsync(DeviceData device, int port = 5555, CancellationToken cancellationToken = default)
    {
        AppLogger.Log($"[Adb Wi-Fi] Đang truy vấn địa chỉ IP Wi-Fi của thiết bị {device.Serial}...");
        var wifiIp = await GetDeviceWifiIpAsync(device, cancellationToken);
        if (string.IsNullOrEmpty(wifiIp))
        {
            return (false, "Không tìm thấy địa chỉ IP Wi-Fi trên thiết bị. Hãy đảm bảo điện thoại đã bật Wi-Fi và kết nối cùng mạng với máy tính.", null);
        }

        AppLogger.Log($"[Adb Wi-Fi] Phát hiện IP Wi-Fi thiết bị: {wifiIp}. Đang mở cổng TCP {port}...");
        var tcpipOk = await EnableWirelessAdbAsync(device, port);
        if (!tcpipOk)
        {
            // Thử lại qua CLI
            await ExecuteAdbCliAsync($"-s {device.Serial} tcpip {port}", cancellationToken);
        }

        // Đợi 1 giây để daemon ADB trên điện thoại khởi động lại trên cổng TCP
        await Task.Delay(1000, cancellationToken);

        AppLogger.Log($"[Adb Wi-Fi] Đang kết nối tới {wifiIp}:{port}...");
        var targetIpPort = $"{wifiIp}:{port}";
        var connectResult = await ConnectWifiDeviceAsync(wifiIp, port);
        if (!connectResult)
        {
            // Thử kết nối qua CLI
            var (cliOk, cliOut) = await ExecuteAdbCliAsync($"connect {targetIpPort}", cancellationToken);
            connectResult = cliOk || cliOut.Contains("connected", StringComparison.OrdinalIgnoreCase);
        }

        if (connectResult)
        {
            AppLogger.Log($"[Adb Wi-Fi] ★ Kết nối không dây thành công: {targetIpPort}");
            return (true, $"Đã kết nối Wi-Fi thành công: {targetIpPort}! Bây giờ bạn có thể rút cáp USB.", targetIpPort);
        }

        return (false, $"Đã mở cổng {port} nhưng không thể kết nối tới {targetIpPort}. Hãy thử kết nối lại.", null);
    }

    public async Task<(bool Success, string Message)> PairDeviceAsync(string ipPort, string pairingCode, CancellationToken cancellationToken = default)
    {
        ipPort = ipPort.Trim();
        pairingCode = pairingCode.Trim();

        AppLogger.Log($"[Adb Pairing] Đang gửi yêu cầu ghép đôi tới {ipPort} với mã {pairingCode}...");
        var (ok, output) = await ExecuteAdbCliAsync($"pair {ipPort} {pairingCode}", cancellationToken);

        if (ok || output.Contains("Successfully paired", StringComparison.OrdinalIgnoreCase))
        {
            AppLogger.Log($"[Adb Pairing] ★ Ghép đôi thành công: {ipPort}");
            return (true, $"Ghép đôi thành công thiết bị {ipPort}!");
        }

        AppLogger.Log($"[Adb Pairing] Ghép đôi thất bại: {output}");
        return (false, string.IsNullOrWhiteSpace(output) ? "Ghép đôi thất bại. Hãy kiểm tra lại mã PIN và địa chỉ IP:Cổng." : output);
    }

    public async Task<bool> DisconnectDeviceAsync(string ipPort, CancellationToken cancellationToken = default)
    {
        ipPort = ipPort.Trim();
        AppLogger.Log($"[Adb Wi-Fi] Đang ngắt kết nối {ipPort}...");
        var (ok, _) = await ExecuteAdbCliAsync($"disconnect {ipPort}", cancellationToken);
        return ok;
    }

    public void Dispose()
    {
        _monitor?.Dispose();
        _monitor = null;
    }
}

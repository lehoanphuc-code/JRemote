using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JRemote.Android.Adb;
using JRemote.Android.Devices;
using JRemote.Android.Session;
using JRemote.Core.Interfaces;
using JRemote.Core.Logging;
using JRemote.Core.Models;
using JRemote.Core.Services;
using JRemote.iOS.AirPlay;
using JRemote.iOS.Bonjour;
using JRemote.iOS.Devices;
using JRemote.iOS.Session;
using JRemote.Rendering.Decoders;
using JRemote.Rendering.Renderers;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace JRemote.UI.ViewModels;

public partial class MainViewModel : ObservableObject, IAsyncDisposable
{
    public const int MaxSlots = 3;

    private readonly SettingsService _settingsService = new();
    private AppSettings _appSettings;
    private readonly AdbService _adbService;
    private readonly AirPlayAdvertiser _airPlayAdvertiser;
    private readonly AirPlayReceiverServer _airPlayReceiver;

    [ObservableProperty]
    private string _airPlayDeviceName = string.Empty;

    [ObservableProperty]
    private string _airPlayHintText = string.Empty;

    [ObservableProperty]
    private ObservableCollection<MirroringSlotViewModel> _activeSlots = new();

    [ObservableProperty]
    private ObservableCollection<DeviceItemViewModel> _devices = new();

    [ObservableProperty]
    private DeviceItemViewModel? _selectedDevice;

    [ObservableProperty]
    private DeviceItemViewModel? _activeMirroringDevice;

    [ObservableProperty]
    private bool _isMirroringActive;

    [ObservableProperty]
    private string _adbStatus = "Đang khởi tạo...";

    [ObservableProperty]
    private string _airPlayStatus = "Đang dừng";

    [ObservableProperty]
    private bool _isAirPlayRunning;

    [ObservableProperty]
    private string _wifiIpInput = string.Empty;

    [ObservableProperty]
    private ObservableCollection<string> _recentWifiIps = new();

    [ObservableProperty]
    private string _pairingIpPort = string.Empty;

    [ObservableProperty]
    private string _pairingCode = string.Empty;

    [ObservableProperty]
    private string _pairingConnectPort = string.Empty;

    [ObservableProperty]
    private bool _isWirelessPairingOpen;

    [ObservableProperty]
    private bool _isConnectingWifi;

    [ObservableProperty]
    private bool _isAlwaysOnTop;

    [ObservableProperty]
    private WriteableBitmap? _screenBitmap;

    [ObservableProperty]
    private string _appVersionText = "v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.2.4");

    [ObservableProperty]
    private string _windowTitle = "JRemote v" + (typeof(MainViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.2.4") + " - Phản Chiếu & Điều Khiển Điện Thoại (iOS & Android)";

    [ObservableProperty]
    private string _statusMessage = "Sẵn sàng";

    private UpdateInfo? _pendingUpdateInfo;

    [ObservableProperty]
    private bool _isUpdateAvailable;

    [ObservableProperty]
    private string _updateVersionTag = string.Empty;

    [ObservableProperty]
    private string _updateNotes = string.Empty;

    [ObservableProperty]
    private bool _isDownloadingUpdate;

    [ObservableProperty]
    private bool _isNotDownloadingUpdate = true;

    [ObservableProperty]
    private double _downloadProgressPercent;

    [ObservableProperty]
    private string _downloadProgressText = string.Empty;

    [ObservableProperty]
    private bool _isUpdateBannerVisible;

    [ObservableProperty]
    private ObservableCollection<string> _logEntries = new();

    [ObservableProperty]
    private bool _showLogsTab = true; // Mặc định hiển thị tab Nhật Ký (Logs)

    [RelayCommand]
    public void SelectLogsView() => ShowLogsTab = true;

    [RelayCommand]
    public void SelectMirroringView()
    {
        if (IsMirroringActive)
            ShowLogsTab = false;
    }

    public MainViewModel()
    {
        AppLogger.Initialize();
        AppLogger.LogAdded += line =>
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                LogEntries.Add(line);
                if (LogEntries.Count > 300) LogEntries.RemoveAt(0);
            });
        };

        _appSettings = _settingsService.LoadSettings();
        AirPlayDeviceName = _appSettings.AirPlayDeviceName;
        AirPlayHintText = $"💡 Trên iPhone: Mở Control Center ➔ Chọn 'Phản chiếu màn hình' ➔ Chọn '{AirPlayDeviceName}'";

        foreach (var ip in _appSettings.RecentWifiIps)
        {
            RecentWifiIps.Add(ip);
        }
        if (RecentWifiIps.Count > 0 && string.IsNullOrWhiteSpace(WifiIpInput))
        {
            WifiIpInput = RecentWifiIps[0];
        }

        _adbService = new AdbService();
        var airPlaySecurity = new AirPlaySecurity();
        _airPlayAdvertiser = new AirPlayAdvertiser(airPlaySecurity, AirPlayDeviceName);
        _airPlayReceiver = new AirPlayReceiverServer(airPlaySecurity, serverName: AirPlayDeviceName);

        _adbService.DeviceConnected += async (s, d) => await Application.Current.Dispatcher.InvokeAsync(RefreshDevicesAsync);
        _adbService.DeviceDisconnected += async (s, d) => await Application.Current.Dispatcher.InvokeAsync(RefreshDevicesAsync);
        _airPlayReceiver.ClientConnected += async (s, e) => await Application.Current.Dispatcher.InvokeAsync(() => HandleAirPlayClientConnected(e));
        _airPlayReceiver.ClientDisconnected += async (s, ip) => await Application.Current.Dispatcher.InvokeAsync(() => HandleAirPlayClientDisconnected(ip));
    }

    public async Task InitializeAsync()
    {
        // 1. Tự động kiểm tra bản cập nhật mới trên GitHub ngay khi mở app (chạy nền hoàn toàn)
        _ = Task.Run(CheckForUpdatesAsync);

        StatusMessage = "Đang kết nối ADB server...";
        var adbOk = await _adbService.InitializeAsync();
        if (adbOk)
        {
            AdbStatus = "ADB Sẵn sàng (Port 5037)";
            await RefreshDevicesAsync();

            // Tự động kết nối lại thiết bị Wi-Fi gần nhất nếu có
            if (_appSettings.AutoReconnectWifi && _appSettings.RecentWifiIps.Count > 0)
            {
                _ = Task.Run(async () =>
                {
                    foreach (var ipPort in _appSettings.RecentWifiIps.Take(2))
                    {
                        var parts = ipPort.Split(':');
                        var ip = parts[0];
                        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 5555;
                        await _adbService.ConnectWifiDeviceAsync(ip, port);
                    }
                });
            }
        }
        else
        {
            AdbStatus = "Không tìm thấy adb.exe (Cần cấu hình hoặc đặt vào bin/adb)";
        }

        // Tự động bật AirPlay receiver
        await StartAirPlayAsync();
        StatusMessage = "Khởi tạo hoàn tất";
    }

    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        try
        {
            StatusMessage = "Đang quét thiết bị...";
            var adbDevices = await _adbService.GetDevicesAsync();

            var currentList = new List<DeviceItemViewModel>();
            foreach (var adbDevice in adbDevices)
            {
                var device = new AndroidDevice(_adbService.Client, adbDevice);
                currentList.Add(new DeviceItemViewModel(device));
            }

            // Giữ lại các thiết bị iOS nếu có
            foreach (var existing in Devices.Where(d => d.IsIos))
            {
                currentList.Add(existing);
            }

            Devices.Clear();
            foreach (var item in currentList)
            {
                Devices.Add(item);
            }

            StatusMessage = $"Đã phát hiện {Devices.Count} thiết bị";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi quét thiết bị: {ex.Message}";
        }
    }

    [RelayCommand]
    public async Task ConnectWifiAsync()
    {
        if (string.IsNullOrWhiteSpace(WifiIpInput))
        {
            StatusMessage = "Vui lòng nhập địa chỉ IP thiết bị (ví dụ 192.168.1.50:5555)";
            return;
        }

        IsConnectingWifi = true;
        StatusMessage = $"Đang kết nối tới {WifiIpInput}...";

        try
        {
            var raw = WifiIpInput.Trim();
            var parts = raw.Split(':');
            var ip = parts[0];
            var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 5555;
            var fullIpPort = $"{ip}:{port}";

            var success = await _adbService.ConnectWifiDeviceAsync(ip, port);
            if (success)
            {
                StatusMessage = $"Kết nối Wi-Fi thành công: {fullIpPort}";
                _settingsService.AddRecentWifiIp(_appSettings, fullIpPort);
                UpdateRecentWifiIpsCollection();
                await RefreshDevicesAsync();
            }
            else
            {
                StatusMessage = $"Không thể kết nối tới {fullIpPort}. Hãy đảm bảo đã bật 'Wireless Debugging' trên điện thoại.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi kết nối Wi-Fi: {ex.Message}";
        }
        finally
        {
            IsConnectingWifi = false;
        }
    }

    [RelayCommand]
    public void SelectRecentIp(string? ipPort)
    {
        if (!string.IsNullOrWhiteSpace(ipPort))
        {
            WifiIpInput = ipPort.Trim();
        }
    }

    [RelayCommand]
    public void ToggleWirelessPairing()
    {
        IsWirelessPairingOpen = !IsWirelessPairingOpen;
    }

    [RelayCommand]
    public async Task PairAndConnectWirelessAsync()
    {
        if (string.IsNullOrWhiteSpace(PairingIpPort) || string.IsNullOrWhiteSpace(PairingCode))
        {
            StatusMessage = "Vui lòng nhập Địa chỉ IP:Cổng ghép đôi và Mã PIN ghép nối 6 chữ số!";
            return;
        }

        IsConnectingWifi = true;
        StatusMessage = $"Đang ghép đôi với {PairingIpPort}...";

        try
        {
            var (pairOk, pairMsg) = await _adbService.PairDeviceAsync(PairingIpPort, PairingCode);
            if (!pairOk)
            {
                StatusMessage = $"Lỗi ghép đôi: {pairMsg}";
                return;
            }

            StatusMessage = "Ghép đôi thành công! Đang kết nối kênh truyền...";

            // Lấy IP từ chuỗi IP:Cổng ghép đôi
            var pairParts = PairingIpPort.Trim().Split(':');
            var ip = pairParts[0];

            int connectPort = 5555;
            if (!string.IsNullOrWhiteSpace(PairingConnectPort) && int.TryParse(PairingConnectPort.Trim(), out var cp))
            {
                connectPort = cp;
            }

            var connectOk = await _adbService.ConnectWifiDeviceAsync(ip, connectPort);
            if (!connectOk && connectPort != 5555)
            {
                connectOk = await _adbService.ConnectWifiDeviceAsync(ip, 5555);
                if (connectOk) connectPort = 5555;
            }

            if (connectOk)
            {
                var fullIpPort = $"{ip}:{connectPort}";
                StatusMessage = $"Kết nối không dây thành công: {fullIpPort}!";
                _settingsService.AddRecentWifiIp(_appSettings, fullIpPort);
                UpdateRecentWifiIpsCollection();
                IsWirelessPairingOpen = false;
                PairingCode = string.Empty;
                await RefreshDevicesAsync();
            }
            else
            {
                StatusMessage = $"Đã ghép đôi thành công! Vui lòng nhập đúng Cổng kết nối (Connect Port) hiển thị trên điện thoại.";
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi ghép đôi: {ex.Message}";
        }
        finally
        {
            IsConnectingWifi = false;
        }
    }

    [RelayCommand]
    public async Task SwitchUsbToWifiAsync(DeviceItemViewModel? deviceVm)
    {
        if (deviceVm?.Device is AndroidDevice androidDevice)
        {
            StatusMessage = $"Đang tự động chuyển {androidDevice.Info.Name} sang Wi-Fi...";
            IsConnectingWifi = true;

            try
            {
                var (ok, msg, connectedIpPort) = await _adbService.SwitchUsbToWirelessAsync(androidDevice.DeviceData);
                StatusMessage = msg;
                if (ok && !string.IsNullOrEmpty(connectedIpPort))
                {
                    _settingsService.AddRecentWifiIp(_appSettings, connectedIpPort);
                    UpdateRecentWifiIpsCollection();
                    await RefreshDevicesAsync();
                }
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi chuyển Wi-Fi: {ex.Message}";
            }
            finally
            {
                IsConnectingWifi = false;
            }
        }
    }

    [RelayCommand]
    public async Task DisconnectWifiDeviceAsync(DeviceItemViewModel? deviceVm)
    {
        if (deviceVm?.Device is AndroidDevice androidDevice)
        {
            var target = androidDevice.DeviceData.Serial;
            StatusMessage = $"Đang ngắt kết nối {target}...";

            try
            {
                await _adbService.DisconnectDeviceAsync(target);
                await RefreshDevicesAsync();
                StatusMessage = $"Đã ngắt kết nối Wi-Fi thiết bị {target}";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Lỗi ngắt kết nối: {ex.Message}";
            }
        }
    }

    private void UpdateRecentWifiIpsCollection()
    {
        RecentWifiIps.Clear();
        foreach (var ip in _appSettings.RecentWifiIps)
        {
            RecentWifiIps.Add(ip);
        }
    }

    [RelayCommand]
    public async Task ToggleAirPlayAsync()
    {
        if (IsAirPlayRunning)
        {
            await StopAirPlayAsync();
        }
        else
        {
            await StartAirPlayAsync();
        }
    }

    [RelayCommand]
    public async Task SaveAirPlayDeviceNameAsync()
    {
        var newName = AirPlayDeviceName?.Trim();
        if (string.IsNullOrWhiteSpace(newName))
        {
            StatusMessage = "Tên thiết bị AirPlay không được để trống!";
            return;
        }

        // Giới hạn độ dài tên
        if (newName.Length > 28)
        {
            newName = newName[..28];
        }
        AirPlayDeviceName = newName;

        _appSettings.AirPlayDeviceName = newName;
        _settingsService.SaveSettings(_appSettings);

        _airPlayReceiver.ServerName = newName;
        await _airPlayAdvertiser.UpdateServerNameAsync(newName);

        AirPlayHintText = $"💡 Trên iPhone: Mở Control Center ➔ Chọn 'Phản chiếu màn hình' ➔ Chọn '{newName}'";
        StatusMessage = $"Đã cập nhật tên AirPlay thành: '{newName}'";
    }

    private async Task StartAirPlayAsync()
    {
        try
        {
            await _airPlayReceiver.StartAsync();
            await _airPlayAdvertiser.StartAsync();
            IsAirPlayRunning = true;
            AirPlayStatus = "Đang phát sóng AirPlay (Port 7000) - Sẵn sàng nhận kết nối iPhone";
        }
        catch (Exception ex)
        {
            AirPlayStatus = $"Lỗi AirPlay: {ex.Message}";
        }
    }

    private async Task StopAirPlayAsync()
    {
        await _airPlayAdvertiser.StopAsync();
        await _airPlayReceiver.StopAsync();
        IsAirPlayRunning = false;
        AirPlayStatus = "AirPlay đã tắt";
    }

    private void HandleAirPlayClientConnected(AirPlayClientConnectedEventArgs e)
    {
        var existing = Devices.FirstOrDefault(d => d.Id == e.ClientIp);
        if (existing == null)
        {
            var iosDevice = new IOSDevice(e.ClientIp, e.DeviceName, e.ClientIp);
            existing = new DeviceItemViewModel(iosDevice);
            Devices.Add(existing);
        }

        StatusMessage = $"iPhone kết nối AirPlay: {e.DeviceName}";

        // Tự động mở session mirror nếu chưa phản chiếu thiết bị này
        if (!ActiveSlots.Any(s => s.DeviceItem.Id == e.ClientIp))
        {
            if (ActiveSlots.Count < MaxSlots)
            {
                _ = StartMirroringAsync(existing);
            }
            else
            {
                AppLogger.Log($"[AirPlay] Thiết bị {e.DeviceName} đã kết nối nhưng tất cả {MaxSlots} slot hiển thị đã đầy.");
                StatusMessage = $"Đã đầy {MaxSlots} slot phản chiếu. Vui lòng đóng bớt 1 ô để hiển thị {e.DeviceName}.";
            }
        }
    }

    private void HandleAirPlayClientDisconnected(string clientIp)
    {
        var activeSlot = ActiveSlots.FirstOrDefault(s => s.DeviceItem.Id == clientIp);
        if (activeSlot != null)
        {
            _ = CloseSlotAsync(activeSlot);
        }

        var toRemove = Devices.FirstOrDefault(d => d.Id == clientIp);
        if (toRemove != null)
        {
            Devices.Remove(toRemove);
        }
    }

    [RelayCommand]
    public async Task StartMirroringAsync(DeviceItemViewModel? deviceVm)
    {
        if (deviceVm == null) return;

        // Nếu thiết bị này đã đang phản chiếu thì chuyển sang tab phản chiếu
        if (ActiveSlots.Any(s => s.DeviceItem.Id == deviceVm.Id))
        {
            ShowLogsTab = false;
            return;
        }

        if (ActiveSlots.Count >= MaxSlots)
        {
            StatusMessage = $"Đã đạt giới hạn tối đa {MaxSlots} thiết bị cùng lúc. Hãy đóng bớt 1 ô để tiếp tục.";
            AppLogger.Log($"[Mirroring] Không thể mở thêm {deviceVm.Name}: Đã đầy {MaxSlots} slot.");
            return;
        }

        try
        {
            StatusMessage = $"Đang bắt đầu phản chiếu {deviceVm.Name}...";

            IMirroringSession session;
            if (deviceVm.Device is AndroidDevice androidDev)
            {
                var decoder = new OpenH264Decoder();
                session = new AndroidMirroringSession(androidDev, decoder);
            }
            else if (deviceVm.Device is IOSDevice iosDev)
            {
                var decoder = new OpenH264Decoder();
                session = new IOSMirroringSession(iosDev, _airPlayReceiver, decoder);
            }
            else
            {
                StatusMessage = "Loại thiết bị không được hỗ trợ.";
                return;
            }

            var usedNumbers = ActiveSlots.Select(s => s.SlotNumber).ToHashSet();
            int slotNumber = 1;
            for (int i = 1; i <= MaxSlots; i++)
            {
                if (!usedNumbers.Contains(i))
                {
                    slotNumber = i;
                    break;
                }
            }

            var slotVm = new MirroringSlotViewModel(slotNumber, deviceVm, session);
            slotVm.CloseRequested += async (s, e) => await CloseSlotAsync(e);

            await slotVm.StartAsync();

            ActiveSlots.Add(slotVm);
            ActiveMirroringDevice = deviceVm;
            IsMirroringActive = true;
            ShowLogsTab = false; // Tự động hiển thị màn hình phản chiếu
            StatusMessage = $"Đang phản chiếu ({ActiveSlots.Count}/{MaxSlots} thiết bị): {deviceVm.Name}";
            AppLogger.Log($"[Mirroring] Đã kích hoạt Slot {slotNumber} cho thiết bị: {deviceVm.Name}");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Lỗi khởi động phản chiếu: {ex.Message}";
            AppLogger.Log($"[Mirroring-LỖI] Khởi động phản chiếu {deviceVm.Name} thất bại: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task CloseSlotAsync(MirroringSlotViewModel? slotVm)
    {
        if (slotVm == null) return;
        if (!ActiveSlots.Contains(slotVm)) return;

        try
        {
            ActiveSlots.Remove(slotVm);
            await slotVm.DisposeAsync();

            IsMirroringActive = ActiveSlots.Count > 0;
            if (ActiveSlots.Count == 0)
            {
                ActiveMirroringDevice = null;
                ShowLogsTab = true; // Quay lại hiển thị nhật ký khi không còn thiết bị nào
                StatusMessage = "Đã dừng toàn bộ phản chiếu.";
            }
            else
            {
                ActiveMirroringDevice = ActiveSlots.Last().DeviceItem;
                StatusMessage = $"Đang phản chiếu {ActiveSlots.Count}/{MaxSlots} thiết bị.";
            }

            AppLogger.Log($"[Mirroring] Đã đóng Slot {slotVm.SlotNumber} ({slotVm.DeviceItem.Name}). Còn lại {ActiveSlots.Count} thiết bị.");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Mirroring-LỖI] Đóng slot thất bại: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task StopMirroringAsync()
    {
        var slots = ActiveSlots.ToList();
        foreach (var slot in slots)
        {
            await CloseSlotAsync(slot);
        }
        StatusMessage = "Đã dừng phản chiếu toàn bộ thiết bị.";
    }

    [RelayCommand]
    public async Task SendBackAsync()
    {
        var target = ActiveSlots.LastOrDefault();
        if (target != null) await target.SendBackAsync();
    }

    [RelayCommand]
    public async Task SendHomeAsync()
    {
        var target = ActiveSlots.LastOrDefault();
        if (target != null) await target.SendHomeAsync();
    }

    [RelayCommand]
    public async Task SendRecentAppsAsync()
    {
        var target = ActiveSlots.LastOrDefault();
        if (target != null) await target.SendRecentAppsAsync();
    }

    [RelayCommand]
    public async Task SendPowerAsync()
    {
        var target = ActiveSlots.LastOrDefault();
        if (target != null) await target.SendPowerAsync();
    }

    [RelayCommand]
    public async Task ForwardTouchAsync(TouchEvent touchEvent)
    {
        var target = ActiveSlots.LastOrDefault();
        if (target != null) await target.ForwardTouchAsync(touchEvent);
    }

    [RelayCommand]
    public async Task ForwardScrollAsync(ScrollEvent scrollEvent)
    {
        var target = ActiveSlots.LastOrDefault();
        if (target != null) await target.ForwardScrollAsync(scrollEvent);
    }

    [RelayCommand]
    public void TakeScreenshot()
    {
        var target = ActiveSlots.LastOrDefault();
        target?.TakeScreenshot();
    }

    [RelayCommand]
    public void ToggleAlwaysOnTop()
    {
        IsAlwaysOnTop = !IsAlwaysOnTop;
    }

    [RelayCommand]
    public void ClearLogs()
    {
        LogEntries.Clear();
    }

    [RelayCommand]
    public void CopyLogs()
    {
        var allText = string.Join(Environment.NewLine, LogEntries);
        Clipboard.SetText(allText);
        StatusMessage = "Đã sao chép toàn bộ nhật ký vào Clipboard!";
    }

    [RelayCommand]
    public async Task CheckForUpdatesAsync()
    {
        try
        {
            var updateService = new UpdateService();
            var info = await updateService.CheckForUpdateAsync(AppVersionText);
            if (info != null && info.IsUpdateAvailable)
            {
                var applyInfo = () =>
                {
                    _pendingUpdateInfo = info;
                    UpdateVersionTag = info.TagName;
                    UpdateNotes = info.ReleaseTitle ?? info.TagName;
                    IsUpdateAvailable = true;
                    IsUpdateBannerVisible = true;
                    StatusMessage = $"Có bản cập nhật mới: {info.TagName}!";
                };

                if (Application.Current?.Dispatcher != null)
                {
                    await Application.Current.Dispatcher.InvokeAsync(applyInfo);
                }
                else
                {
                    applyInfo();
                }
            }
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[AutoUpdate] Lỗi kiểm tra cập nhật: {ex.Message}");
        }
    }

    [RelayCommand]
    public async Task ApplyUpdateAsync()
    {
        if (_pendingUpdateInfo == null || IsDownloadingUpdate) return;

        IsDownloadingUpdate = true;
        IsNotDownloadingUpdate = false;
        DownloadProgressPercent = 0;
        DownloadProgressText = "Đang kết nối GitHub...";
        StatusMessage = $"Đang tải bản cập nhật {_pendingUpdateInfo.TagName}...";

        try
        {
            var updateService = new UpdateService();
            var progress = new Progress<(long downloaded, long total)>(p =>
            {
                if (p.total > 0)
                {
                    var pct = (double)p.downloaded / p.total * 100.0;
                    DownloadProgressPercent = Math.Round(pct, 1);
                    var dlMb = (double)p.downloaded / (1024 * 1024);
                    var totMb = (double)p.total / (1024 * 1024);
                    DownloadProgressText = $"Đang tải: {dlMb:F1} MB / {totMb:F1} MB ({DownloadProgressPercent:F0}%)";
                }
            });

            var sourceDir = await updateService.DownloadAndExtractAsync(_pendingUpdateInfo, progress);

            DownloadProgressText = "Tải hoàn tất! Đang khởi động lại ứng dụng...";
            StatusMessage = "Đang khởi động lại để hoàn tất cập nhật...";
            await Task.Delay(1000);

            updateService.LaunchUpdaterAndRestart(sourceDir);
            Application.Current?.Dispatcher?.Invoke(() =>
            {
                Application.Current.Shutdown();
            });
            Environment.Exit(0);
        }
        catch (Exception ex)
        {
            IsDownloadingUpdate = false;
            IsNotDownloadingUpdate = true;
            DownloadProgressText = string.Empty;
            StatusMessage = $"Lỗi cập nhật: {ex.Message}";
            AppLogger.Log($"[AutoUpdate-LỖI] {ex.Message}");
        }
    }

    [RelayCommand]
    public void DismissUpdateBanner()
    {
        IsUpdateBannerVisible = false;
    }

    [RelayCommand]
    public void ShowUpdateBanner()
    {
        if (IsUpdateAvailable)
        {
            IsUpdateBannerVisible = true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopMirroringAsync();
        await StopAirPlayAsync();
        _adbService.Dispose();
    }
}

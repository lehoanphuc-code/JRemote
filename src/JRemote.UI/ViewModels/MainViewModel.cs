using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JRemote.Android.Adb;
using JRemote.Android.Devices;
using JRemote.Android.Session;
using JRemote.Core.Interfaces;
using JRemote.Core.Logging;
using JRemote.Core.Models;
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

    private readonly AdbService _adbService;
    private readonly AirPlayAdvertiser _airPlayAdvertiser;
    private readonly AirPlayReceiverServer _airPlayReceiver;

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
    private bool _isAlwaysOnTop;

    [ObservableProperty]
    private WriteableBitmap? _screenBitmap;

    [ObservableProperty]
    private string _statusMessage = "Sẵn sàng";

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
        _adbService = new AdbService();
        var airPlaySecurity = new AirPlaySecurity();
        _airPlayAdvertiser = new AirPlayAdvertiser(airPlaySecurity);
        _airPlayReceiver = new AirPlayReceiverServer(airPlaySecurity);

        _adbService.DeviceConnected += async (s, d) => await Application.Current.Dispatcher.InvokeAsync(RefreshDevicesAsync);
        _adbService.DeviceDisconnected += async (s, d) => await Application.Current.Dispatcher.InvokeAsync(RefreshDevicesAsync);
        _airPlayReceiver.ClientConnected += async (s, e) => await Application.Current.Dispatcher.InvokeAsync(() => HandleAirPlayClientConnected(e));
        _airPlayReceiver.ClientDisconnected += async (s, ip) => await Application.Current.Dispatcher.InvokeAsync(() => HandleAirPlayClientDisconnected(ip));
    }

    public async Task InitializeAsync()
    {
        StatusMessage = "Đang kết nối ADB server...";
        var adbOk = await _adbService.InitializeAsync();
        if (adbOk)
        {
            AdbStatus = "ADB Sẵn sàng (Port 5037)";
            await RefreshDevicesAsync();
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
            StatusMessage = "Vui lòng nhập địa chỉ IP thiết bị (ví dụ 192.168.1.50)";
            return;
        }

        StatusMessage = $"Đang kết nối tới {WifiIpInput}...";
        var parts = WifiIpInput.Trim().Split(':');
        var ip = parts[0];
        var port = parts.Length > 1 && int.TryParse(parts[1], out var p) ? p : 5555;

        var success = await _adbService.ConnectWifiDeviceAsync(ip, port);
        if (success)
        {
            StatusMessage = $"Kết nối Wi-Fi thành công: {WifiIpInput}";
            WifiIpInput = string.Empty;
            await RefreshDevicesAsync();
        }
        else
        {
            StatusMessage = $"Không thể kết nối tới {WifiIpInput}. Hãy đảm bảo đã bật 'Wireless Debugging' trên điện thoại.";
        }
    }

    [RelayCommand]
    public async Task EnableDeviceWifiAdbAsync(DeviceItemViewModel? deviceVm)
    {
        if (deviceVm?.Device is AndroidDevice androidDevice)
        {
            StatusMessage = $"Đang mở cổng Wi-Fi 5555 cho {androidDevice.Info.Name}...";
            var ok = await _adbService.EnableWirelessAdbAsync(androidDevice.DeviceData);
            if (ok)
            {
                StatusMessage = "Đã kích hoạt cổng Wi-Fi 5555! Bây giờ bạn có thể rút cáp và kết nối bằng IP.";
            }
            else
            {
                StatusMessage = "Không thể kích hoạt cổng Wi-Fi. Hãy thử lại.";
            }
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
                session = new AndroidMirroringSession(androidDev);
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

        try
        {
            await slotVm.DisposeAsync();
            ActiveSlots.Remove(slotVm);

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

    public async ValueTask DisposeAsync()
    {
        await StopMirroringAsync();
        await StopAirPlayAsync();
        _adbService.Dispose();
    }
}

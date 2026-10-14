using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using JRemote.Core.Interfaces;
using JRemote.Core.Logging;
using JRemote.Core.Models;
using JRemote.Rendering.Renderers;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace JRemote.UI.ViewModels;

public partial class MirroringSlotViewModel : ObservableObject, IAsyncDisposable
{
    private readonly IMirroringSession _session;
    private readonly WriteableBitmapRenderer _renderer;
    private long _renderedFrames;
    private DateTime _lastFpsTime = DateTime.UtcNow;

    public int SlotNumber { get; }
    public DeviceItemViewModel DeviceItem { get; }
    public IMirroringSession Session => _session;
    public WriteableBitmapRenderer Renderer => _renderer;

    [ObservableProperty]
    private WriteableBitmap? _screenBitmap;

    [ObservableProperty]
    private string _resolutionText = "Đang tải...";

    [ObservableProperty]
    private string _fpsText = "0 FPS";

    public event EventHandler<MirroringSlotViewModel>? CloseRequested;

    public MirroringSlotViewModel(int slotNumber, DeviceItemViewModel deviceItem, IMirroringSession session)
    {
        SlotNumber = slotNumber;
        DeviceItem = deviceItem;
        _session = session;
        _renderer = new WriteableBitmapRenderer();

        _renderer.BitmapCreatedOrResized += (s, bmp) =>
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                ScreenBitmap = bmp;
            });
        };

        _session.FrameReceived += OnFrameReceived;
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _session.StartAsync(null, cancellationToken);
    }

    private void OnFrameReceived(object? sender, VideoFrame frame)
    {
        _renderer.RenderFrame(frame);
        _renderedFrames++;

        if (_renderedFrames == 1 && frame.Width > 0 && frame.Height > 0)
        {
            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                ResolutionText = $"{frame.Width}x{frame.Height}";
                FpsText = "60 FPS";
            });
        }

        var now = DateTime.UtcNow;
        var elapsed = (now - _lastFpsTime).TotalSeconds;
        if (elapsed >= 1.0)
        {
            var fps = (int)Math.Round(_renderedFrames / elapsed);
            _renderedFrames = 0;
            _lastFpsTime = now;

            Application.Current?.Dispatcher?.InvokeAsync(() =>
            {
                FpsText = $"{fps} FPS";
                if (frame.Width > 0 && frame.Height > 0)
                {
                    ResolutionText = $"{frame.Width}x{frame.Height}";
                }
            });
        }
    }

    [RelayCommand]
    public void RequestClose()
    {
        CloseRequested?.Invoke(this, this);
    }

    [RelayCommand]
    public async Task SendBackAsync()
    {
        if (_session.InputController != null)
            await _session.InputController.SendBackAsync();
    }

    [RelayCommand]
    public async Task SendHomeAsync()
    {
        if (_session.InputController != null)
            await _session.InputController.SendHomeAsync();
    }

    [RelayCommand]
    public async Task SendRecentAppsAsync()
    {
        if (_session.InputController != null)
            await _session.InputController.SendRecentAppsAsync();
    }

    [RelayCommand]
    public async Task SendPowerAsync()
    {
        if (_session.InputController != null)
            await _session.InputController.SendPowerAsync();
    }

    [RelayCommand]
    public async Task ForwardTouchAsync(TouchEvent touchEvent)
    {
        if (_session.InputController != null)
            await _session.InputController.SendTouchAsync(touchEvent);
    }

    [RelayCommand]
    public async Task ForwardScrollAsync(ScrollEvent scrollEvent)
    {
        if (_session.InputController != null)
            await _session.InputController.SendScrollAsync(scrollEvent);
    }

    [RelayCommand]
    public void TakeScreenshot()
    {
        if (ScreenBitmap == null) return;

        try
        {
            var picturesPath = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            var folder = Path.Combine(picturesPath, "JRemote");
            Directory.CreateDirectory(folder);

            var sanitizedDeviceName = string.Join("_", DeviceItem.Name.Split(Path.GetInvalidFileNameChars()));
            var fileName = $"Screenshot_{sanitizedDeviceName}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
            var filePath = Path.Combine(folder, fileName);

            using var stream = new FileStream(filePath, FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(ScreenBitmap));
            encoder.Save(stream);

            Clipboard.SetImage(ScreenBitmap);
            AppLogger.Log($"[Screenshot] Đã lưu ảnh màn hình {DeviceItem.Name} vào: {filePath}");
        }
        catch (Exception ex)
        {
            AppLogger.Log($"[Screenshot-LỖI] Không thể chụp ảnh: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _session.FrameReceived -= OnFrameReceived;
        await _session.StopAsync();
        await _session.DisposeAsync();
        _renderer.Dispose();
    }
}

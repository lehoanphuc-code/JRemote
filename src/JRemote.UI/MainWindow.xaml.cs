using JRemote.Core.Models;
using CoreTouchAction = JRemote.Core.Models.TouchAction;
using JRemote.UI.ViewModels;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;

namespace JRemote.UI;

public partial class MainWindow : FluentWindow
{
    private MainViewModel ViewModel => (MainViewModel)DataContext;
    private bool _isPointerDown;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ViewModel.LogEntries.CollectionChanged += (s, ev) =>
        {
            if (LogsListBox.Items.Count > 0)
            {
                LogsListBox.ScrollIntoView(LogsListBox.Items[^1]);
            }
        };
        await ViewModel.InitializeAsync();
    }

    private void OnSlotMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is Image img && img.Tag is MirroringSlotViewModel slot)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                _isPointerDown = true;
                img.CaptureMouse();
                var touch = CalculateTouchEvent(img, slot, e.GetPosition(img), CoreTouchAction.Down);
                if (touch != null)
                {
                    _ = slot.ForwardTouchAsync(touch);
                }
            }
        }
    }

    private void OnSlotMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPointerDown && e.LeftButton == MouseButtonState.Pressed && sender is Image img && img.Tag is MirroringSlotViewModel slot)
        {
            var touch = CalculateTouchEvent(img, slot, e.GetPosition(img), CoreTouchAction.Move);
            if (touch != null)
            {
                _ = slot.ForwardTouchAsync(touch);
            }
        }
    }

    private void OnSlotMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPointerDown && sender is Image img && img.Tag is MirroringSlotViewModel slot)
        {
            _isPointerDown = false;
            img.ReleaseMouseCapture();
            var touch = CalculateTouchEvent(img, slot, e.GetPosition(img), CoreTouchAction.Up);
            if (touch != null)
            {
                _ = slot.ForwardTouchAsync(touch);
            }
        }
    }

    private void OnSlotMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is Image img && img.Tag is MirroringSlotViewModel slot)
        {
            // Chuột phải tương ứng với nút Back
            _ = slot.SendBackAsync();
        }
    }

    private void OnSlotMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is Image img && img.Tag is MirroringSlotViewModel slot)
        {
            if (img.ActualWidth <= 0 || img.ActualHeight <= 0) return;

            var pos = e.GetPosition(img);
            var normX = Math.Clamp(pos.X / img.ActualWidth, 0.0, 1.0);
            var normY = Math.Clamp(pos.Y / img.ActualHeight, 0.0, 1.0);

            var device = slot.DeviceItem?.Device;
            int devW = slot.ScreenBitmap?.PixelWidth ?? device?.Info.Width ?? 1080;
            int devH = slot.ScreenBitmap?.PixelHeight ?? device?.Info.Height ?? 2400;

            int realX = (int)(normX * devW);
            int realY = (int)(normY * devH);

            var scrollEvent = new ScrollEvent(realX, realY, normX, normY, 0, e.Delta);
            _ = slot.ForwardScrollAsync(scrollEvent);
        }
    }

    private TouchEvent? CalculateTouchEvent(Image img, MirroringSlotViewModel slot, Point clickPoint, CoreTouchAction action)
    {
        if (img.ActualWidth <= 0 || img.ActualHeight <= 0) return null;

        var normX = Math.Clamp(clickPoint.X / img.ActualWidth, 0.0, 1.0);
        var normY = Math.Clamp(clickPoint.Y / img.ActualHeight, 0.0, 1.0);

        var device = slot.DeviceItem?.Device;
        int devW = slot.ScreenBitmap?.PixelWidth ?? device?.Info.Width ?? 1080;
        int devH = slot.ScreenBitmap?.PixelHeight ?? device?.Info.Height ?? 2400;

        int realX = (int)(normX * devW);
        int realY = (int)(normY * devH);

        return new TouchEvent(action, realX, realY, normX, normY);
    }
}
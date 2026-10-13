using JRemote.Core.Models;
using CoreTouchAction = JRemote.Core.Models.TouchAction;
using JRemote.UI.ViewModels;
using System.Windows;
using System.Windows.Input;
using Wpf.Ui.Controls;

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

    private void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _isPointerDown = true;
            ViewportImage.CaptureMouse();
            var touch = CalculateTouchEvent(e.GetPosition(ViewportImage), CoreTouchAction.Down);
            if (touch != null)
            {
                _ = ViewModel.ForwardTouchAsync(touch);
            }
        }
    }

    private void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPointerDown && e.LeftButton == MouseButtonState.Pressed)
        {
            var touch = CalculateTouchEvent(e.GetPosition(ViewportImage), CoreTouchAction.Move);
            if (touch != null)
            {
                _ = ViewModel.ForwardTouchAsync(touch);
            }
        }
    }

    private void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_isPointerDown)
        {
            _isPointerDown = false;
            ViewportImage.ReleaseMouseCapture();
            var touch = CalculateTouchEvent(e.GetPosition(ViewportImage), CoreTouchAction.Up);
            if (touch != null)
            {
                _ = ViewModel.ForwardTouchAsync(touch);
            }
        }
    }

    private void OnViewportMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Right click behaves as Back button on Android/iOS
        _ = ViewModel.SendBackAsync();
    }

    private void OnViewportMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var pos = e.GetPosition(ViewportImage);
        var normX = Math.Clamp(pos.X / ViewportImage.ActualWidth, 0.0, 1.0);
        var normY = Math.Clamp(pos.Y / ViewportImage.ActualHeight, 0.0, 1.0);

        var device = ViewModel.ActiveMirroringDevice?.Device;
        int devW = device?.Info.Width ?? 1080;
        int devH = device?.Info.Height ?? 2400;

        int realX = (int)(normX * devW);
        int realY = (int)(normY * devH);

        var scrollEvent = new ScrollEvent(realX, realY, normX, normY, 0, e.Delta);
        _ = ViewModel.ForwardScrollAsync(scrollEvent);
    }

    private TouchEvent? CalculateTouchEvent(Point clickPoint, CoreTouchAction action)
    {
        if (ViewportImage.ActualWidth <= 0 || ViewportImage.ActualHeight <= 0) return null;

        var normX = Math.Clamp(clickPoint.X / ViewportImage.ActualWidth, 0.0, 1.0);
        var normY = Math.Clamp(clickPoint.Y / ViewportImage.ActualHeight, 0.0, 1.0);

        var device = ViewModel.ActiveMirroringDevice?.Device;
        int devW = device?.Info.Width ?? 1080;
        int devH = device?.Info.Height ?? 2400;

        int realX = (int)(normX * devW);
        int realY = (int)(normY * devH);

        return new TouchEvent(action, realX, realY, normX, normY);
    }
}
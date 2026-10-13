using AdvancedSharpAdbClient;
using AdvancedSharpAdbClient.Models;
using JRemote.Core.Interfaces;
using JRemote.Core.Models;
using System.Text.RegularExpressions;

namespace JRemote.Android.Input;

public class AndroidInputController : IInputController
{
    private readonly IAdbClient _adbClient;
    private readonly DeviceData _device;
    private readonly Func<byte[], Task>? _scrcpyControlSender;

    public AndroidInputController(
        IAdbClient adbClient,
        DeviceData device,
        Func<byte[], Task>? scrcpyControlSender = null)
    {
        _adbClient = adbClient;
        _device = device;
        _scrcpyControlSender = scrcpyControlSender;
    }

    public async Task SendTouchAsync(TouchEvent touchEvent)
    {
        if (_scrcpyControlSender != null)
        {
            // If Scrcpy control channel is connected, send binary control packet
            var packet = CreateScrcpyTouchPacket(touchEvent);
            await _scrcpyControlSender(packet);
            return;
        }

        // Fallback to adb shell input
        switch (touchEvent.Action)
        {
            case TouchAction.Down:
            case TouchAction.Up:
                // Tap
                await ExecuteShellAsync($"input tap {touchEvent.X} {touchEvent.Y}");
                break;
            case TouchAction.Move:
                // ADB input doesn't have single-frame move, so we rely on swipe for drag
                break;
        }
    }

    public async Task SendKeyAsync(KeyEvent keyEvent)
    {
        if (keyEvent.Action == KeyAction.Down)
        {
            await ExecuteShellAsync($"input keyevent {keyEvent.KeyCode}");
        }
    }

    public async Task SendTextAsync(string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        // Escape characters for shell
        var escaped = Regex.Replace(text, @"([ &'""\(\)\<\>\;\*\?\|])", @"\$1");
        await ExecuteShellAsync($"input text \"{escaped}\"");
    }

    public async Task SendScrollAsync(ScrollEvent scrollEvent)
    {
        int x = scrollEvent.X;
        int y = scrollEvent.Y;
        int endY = y + (scrollEvent.DeltaY > 0 ? 300 : -300);
        int duration = 150;

        await ExecuteShellAsync($"input swipe {x} {y} {x} {endY} {duration}");
    }

    public async Task SendBackAsync() => await ExecuteShellAsync("input keyevent 4");
    public async Task SendHomeAsync() => await ExecuteShellAsync("input keyevent 3");
    public async Task SendRecentAppsAsync() => await ExecuteShellAsync("input keyevent 187");
    public async Task SendPowerAsync() => await ExecuteShellAsync("input keyevent 26");

    private async Task ExecuteShellAsync(string command)
    {
        try
        {
            await _adbClient.ExecuteRemoteCommandAsync(command, _device, CancellationToken.None);
        }
        catch
        {
            // Ignore temporary transmission errors
        }
    }

    private static byte[] CreateScrcpyTouchPacket(TouchEvent e)
    {
        // Scrcpy INJECT_TOUCH_EVENT packet format:
        // type (1 byte = 2)
        // action (1 byte: 0=down, 1=up, 2=move)
        // pointer_id (8 bytes)
        // position x (4 bytes), y (4 bytes)
        // screen_width (2 bytes), screen_height (2 bytes)
        // pressure (2 bytes), action_button (4 bytes), buttons (4 bytes)
        byte actionCode = e.Action switch
        {
            TouchAction.Down => 0,
            TouchAction.Up => 1,
            TouchAction.Move => 2,
            _ => 1
        };

        var buffer = new byte[32];
        buffer[0] = 2; // SC_CONTROL_MSG_TYPE_INJECT_TOUCH_EVENT
        buffer[1] = actionCode;
        // Pointer ID
        BitConverter.GetBytes(e.PointerId).CopyTo(buffer, 2);
        // X, Y
        BitConverter.GetBytes(e.X).CopyTo(buffer, 10);
        BitConverter.GetBytes(e.Y).CopyTo(buffer, 14);

        return buffer;
    }
}

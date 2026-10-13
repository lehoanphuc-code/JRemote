using JRemote.Core.Interfaces;
using JRemote.Core.Models;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;

namespace JRemote.iOS.Bluetooth;

/// <summary>
/// Controls iOS devices by emulating a Bluetooth HID Mouse/Keyboard device on Windows 10/11.
/// Works in conjunction with iOS AssistiveTouch to enable touch/mouse control from PC.
/// </summary>
public class BluetoothHidController : IInputController
{
    private static readonly Guid HidServiceUuid = new("00001812-0000-1000-8000-00805f9b34fb");
    private static readonly Guid ReportCharacteristicUuid = new("00002a4d-0000-1000-8000-00805f9b34fb");

    private GattServiceProvider? _serviceProvider;
    private GattLocalCharacteristic? _reportCharacteristic;
    private bool _isStarted;
    private int _lastX;
    private int _lastY;

    public bool IsStarted => _isStarted;

    public async Task<bool> StartEmulationAsync()
    {
        if (_isStarted) return true;

        try
        {
            var result = await GattServiceProvider.CreateAsync(HidServiceUuid);
            if (result.Error != BluetoothError.Success)
            {
                return false;
            }

            _serviceProvider = result.ServiceProvider;
            var characteristicResult = await _serviceProvider.Service.CreateCharacteristicAsync(
                ReportCharacteristicUuid,
                new GattLocalCharacteristicParameters
                {
                    CharacteristicProperties = GattCharacteristicProperties.Notify | GattCharacteristicProperties.Read,
                    ReadProtectionLevel = GattProtectionLevel.Plain
                });

            if (characteristicResult.Error != BluetoothError.Success)
            {
                return false;
            }

            _reportCharacteristic = characteristicResult.Characteristic;
            _serviceProvider.StartAdvertising(new GattServiceProviderAdvertisingParameters
            {
                IsConnectable = true,
                IsDiscoverable = true
            });

            _isStarted = true;
            return true;
        }
        catch
        {
            // Bluetooth hardware might be off or unequipped on host PC
            return false;
        }
    }

    public Task SendTouchAsync(TouchEvent touchEvent)
    {
        // Translate absolute touch coordinates to relative mouse delta for AssistiveTouch
        int dx = touchEvent.X - _lastX;
        int dy = touchEvent.Y - _lastY;
        _lastX = touchEvent.X;
        _lastY = touchEvent.Y;

        byte buttons = touchEvent.Action switch
        {
            TouchAction.Down => 0x01, // Left click down
            TouchAction.Move => 0x01, // Dragging with left click down
            TouchAction.Up => 0x00,   // Release
            _ => 0x00
        };

        return SendHidMouseReportAsync(buttons, (sbyte)Math.Clamp(dx, -127, 127), (sbyte)Math.Clamp(dy, -127, 127), 0);
    }

    public Task SendKeyAsync(KeyEvent keyEvent)
    {
        // HID Keyboard Report mapping
        return Task.CompletedTask;
    }

    public Task SendTextAsync(string text)
    {
        return Task.CompletedTask;
    }

    public Task SendScrollAsync(ScrollEvent scrollEvent)
    {
        sbyte wheel = (sbyte)Math.Clamp(scrollEvent.DeltaY / 120, -127, 127);
        return SendHidMouseReportAsync(0, 0, 0, wheel);
    }

    public Task SendBackAsync() => SendHidMouseReportAsync(0x02, 0, 0, 0); // Right click
    public Task SendHomeAsync() => SendHidMouseReportAsync(0x04, 0, 0, 0); // Middle click
    public Task SendRecentAppsAsync() => Task.CompletedTask;
    public Task SendPowerAsync() => Task.CompletedTask;

    private async Task SendHidMouseReportAsync(byte buttons, sbyte dx, sbyte dy, sbyte wheel)
    {
        if (_reportCharacteristic == null || !_isStarted) return;

        try
        {
            // Standard 4-byte HID Mouse Report: [Buttons, DX, DY, Wheel]
            byte[] report = [buttons, (byte)dx, (byte)dy, (byte)wheel];
            var writer = new DataWriter();
            writer.WriteBytes(report);
            await _reportCharacteristic.NotifyValueAsync(writer.DetachBuffer());
        }
        catch
        {
            // Ignore temporary transmission disconnects
        }
    }

    public void Stop()
    {
        _serviceProvider?.StopAdvertising();
        _isStarted = false;
    }
}

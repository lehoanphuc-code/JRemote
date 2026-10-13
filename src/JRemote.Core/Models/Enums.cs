namespace JRemote.Core.Models;

public enum DeviceType
{
    Android,
    iOS
}

public enum ConnectionType
{
    Usb,
    WiFi
}

public enum DeviceState
{
    Disconnected,
    Connecting,
    Connected,
    Mirroring,
    Error
}

public enum TouchAction
{
    Down,
    Move,
    Up,
    Cancel
}

public enum KeyAction
{
    Down,
    Up
}

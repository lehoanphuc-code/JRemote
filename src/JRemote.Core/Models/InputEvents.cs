namespace JRemote.Core.Models;

public record TouchEvent(
    TouchAction Action,
    int X,
    int Y,
    double NormalizedX,
    double NormalizedY,
    long PointerId = 0
);

public record KeyEvent(
    KeyAction Action,
    int KeyCode,
    int MetaState = 0
);

public record ScrollEvent(
    int X,
    int Y,
    double NormalizedX,
    double NormalizedY,
    int DeltaX,
    int DeltaY
);

public record TextInputEvent(
    string Text
);

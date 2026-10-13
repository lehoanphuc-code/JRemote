using JRemote.Core.Models;

namespace JRemote.Core.Interfaces;

public interface IInputController
{
    Task SendTouchAsync(TouchEvent touchEvent);
    Task SendKeyAsync(KeyEvent keyEvent);
    Task SendTextAsync(string text);
    Task SendScrollAsync(ScrollEvent scrollEvent);
    Task SendBackAsync();
    Task SendHomeAsync();
    Task SendRecentAppsAsync();
    Task SendPowerAsync();
}

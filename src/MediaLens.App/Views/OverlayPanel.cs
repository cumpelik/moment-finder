namespace MediaLens.App.Views;

/// <summary>
/// A window shown over the main window (settings, a folder and the cloud): as tall as its content and never taller
/// than the main window, so it never runs off the screen; a long content scrolls inside it.
/// </summary>
public interface IOverlayPanel
{
    /// <summary>Closed (its Close button, Escape, or done): the main window takes it away.</summary>
    event Action? Closed;
    void Close();
}

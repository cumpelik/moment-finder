using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace MediaLens.App.Views;

/// <summary>Small modal dialogs built in code: a question, a text prompt, a message.</summary>
public static class Dialogs
{
    public static async Task<string?> PromptAsync(Window owner, string title, string label, string initial)
    {
        var box = new TextBox { Text = initial };
        string? result = null;
        var dialog = Build(title, new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = label }, box },
        }, out var ok, out var cancel, "OK");
        ok.Click += (_, _) => { result = box.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { result = box.Text; dialog.Close(); }
        };
        dialog.Opened += (_, _) => { box.Focus(); box.SelectAll(); };
        await dialog.ShowDialog(owner);
        return result;
    }

    public static async Task<bool> ConfirmAsync(Window owner, string title, string message)
    {
        var confirmed = false;
        var dialog = Build(title, new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, out var ok, out var cancel, title.Split(' ')[0]);
        ok.Click += (_, _) => { confirmed = true; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
        return confirmed;
    }

    public static async Task InfoAsync(Window owner, string title, string message)
    {
        var dialog = Build(title, new SelectableTextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, out var ok, out var cancel, "OK");
        cancel.IsVisible = false;
        ok.Click += (_, _) => dialog.Close();
        await dialog.ShowDialog(owner);
    }

    private static Window Build(string title, Control content, out Button ok, out Button cancel, string okText)
    {
        ok = new Button { Content = okText, IsDefault = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
            Children = { ok, cancel },
        };
        DockPanel.SetDock(buttons, Dock.Bottom);
        buttons.Margin = new Avalonia.Thickness(0, 16, 0, 0);
        return new Window
        {
            Title = title,
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel { Margin = new Avalonia.Thickness(20), Children = { buttons, content } },
        };
    }
}

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

    /// <summary>A name, offering the people already known as you type.</summary>
    public static async Task<string?> PromptNameAsync(Window owner, string title, IEnumerable<string> names, string initial = "")
    {
        var box = new AutoCompleteBox
        {
            ItemsSource = names.ToList(),
            Text = initial,
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            MinimumPrefixLength = 1,
            PlaceholderText = "Name",
        };
        string? result = null;
        var dialog = Build(title, new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = "Who is it? An existing name adds to that person.", TextWrapping = Avalonia.Media.TextWrapping.Wrap }, box },
        }, out var ok, out var cancel, "OK");
        ok.Click += (_, _) => { result = box.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !box.IsDropDownOpen) { result = box.Text; dialog.Close(); }
        };
        dialog.Opened += (_, _) => box.Focus();
        await dialog.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    public enum WhoAction { Cancel, Name, NotThisPerson, Ignore }

    /// <summary>
    /// Who is a box: a name, or for a box that already has one "Not Greta" (a mistake), or "not a person" (a hand,
    /// a snow cannon, a stranger: the box goes away for good).
    /// </summary>
    public static async Task<(WhoAction Action, string? Name)> AskWhoAsync(Window owner, IEnumerable<string> names, string? current)
    {
        var box = new AutoCompleteBox
        {
            ItemsSource = names.ToList(),
            FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
            MinimumPrefixLength = 1,
            PlaceholderText = current == null ? "Name" : "Another name",
        };
        var result = (WhoAction.Cancel, (string?)null);
        var content = new StackPanel { Spacing = 8 };
        content.Children.Add(new TextBlock
        {
            Text = current == null ? "Who is it? An existing name adds to that person." : $"Marked as {current}. Who is it really?",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        content.Children.Add(box);
        var dialog = Build(current == null ? "Who is it?" : $"Not {current}?", content, out var ok, out var cancel, "OK");
        void Close((WhoAction, string?) r)
        {
            result = r;
            dialog.Close();
        }
        var actions = new WrapPanel { Margin = new Avalonia.Thickness(0, 6, 0, 0) };
        if (current != null)
        {
            var notThis = new Button { Content = $"Not {current}", Margin = new Avalonia.Thickness(0, 0, 8, 0) };
            notThis.Click += (_, _) => Close((WhoAction.NotThisPerson, null));
            actions.Children.Add(notThis);
        }
        var ignore = new Button { Content = "Not a person / a stranger" };
        ToolTip.SetTip(ignore, "The box goes away and the person is never offered again");
        ignore.Click += (_, _) => Close((WhoAction.Ignore, null));
        actions.Children.Add(ignore);
        content.Children.Add(actions);

        ok.Click += (_, _) => Close((WhoAction.Name, box.Text));
        cancel.Click += (_, _) => dialog.Close();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && !box.IsDropDownOpen) Close((WhoAction.Name, box.Text));
        };
        dialog.Opened += (_, _) => box.Focus();
        await dialog.ShowDialog(owner);
        if (result.Item1 == WhoAction.Name && string.IsNullOrWhiteSpace(result.Item2)) return (WhoAction.Cancel, null);
        return (result.Item1, result.Item2?.Trim());
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

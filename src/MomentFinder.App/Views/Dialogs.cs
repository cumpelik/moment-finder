using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;

namespace MomentFinder.App.Views;

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

    /// <summary>
    /// A password: once to check it, or twice to choose it (both the same, not empty). Null when cancelled. A wrong
    /// one is said in the dialog by the caller's check, and the dialog stays.
    /// </summary>
    public static async Task<string?> PasswordAsync(Window owner, string title, string text, bool choose, Func<string, bool>? check = null)
    {
        var first = new TextBox { PasswordChar = '\u2022', PlaceholderText = "Password" };
        var second = new TextBox { PasswordChar = '\u2022', PlaceholderText = "The same once more", IsVisible = choose };
        var problem = new TextBlock { Foreground = Avalonia.Media.Brush.Parse("#F97066"), TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
        string? result = null;
        var dialog = Build(title, new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, first, second, problem },
        }, out var ok, out var cancel, "OK");
        void Try()
        {
            var password = first.Text ?? "";
            var wrong = password.Length == 0 ? "Type the password."
                : choose && password != (second.Text ?? "") ? "The two are not the same."
                : check != null && !check(password) ? "Wrong password."
                : null;
            if (wrong != null)
            {
                problem.Text = wrong;
                problem.IsVisible = true;
                first.Focus();
                first.SelectAll();
                return;
            }
            result = password;
            dialog.Close();
        }
        ok.Click += (_, _) => Try();
        cancel.Click += (_, _) => dialog.Close();
        // Enter in the first box goes to the second one when there is one (not to OK).
        first.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            if (choose) second.Focus();
            else Try();
        };
        second.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Try();
        };
        dialog.Opened += (_, _) => first.Focus();
        await dialog.ShowDialog(owner);
        return result;
    }

    /// <summary>Makes a source read-only; the first time the password of all read-only folders is chosen. False when cancelled.</summary>
    public static async Task<bool> MakeReadOnlyAsync(Window owner, MomentFinder.Library.ReadOnlyLock locks, long sourceId, string name)
    {
        if (!locks.HasPassword)
        {
            var password = await PasswordAsync(owner, "Make read-only",
                $"\"{name}\" becomes read-only: nothing in it can be deleted, moved or added from MomentFinder, and it cannot be removed " +
                "from the library. To change it later, read-only is turned off with a password. Choose it now; it is the same for all " +
                "read-only folders. Without it read-only cannot be turned off.", choose: true);
            if (password == null) return false;
            locks.SetPassword(password);
        }
        else if (!await ConfirmAsync(owner, "Make read-only",
                     $"Make \"{name}\" read-only? Nothing in it can be deleted, moved or added from MomentFinder, and it cannot be removed " +
                     "from the library, until read-only is turned off with the password."))
            return false;
        locks.Protect(sourceId);
        return true;
    }

    /// <summary>Turns read-only off for now, after the password. False when cancelled.</summary>
    public static async Task<bool> TurnReadOnlyOffAsync(Window owner, MomentFinder.Library.ReadOnlyLock locks, long sourceId, string name)
    {
        var password = await PasswordAsync(owner, "Turn read-only off",
            $"\"{name}\" can be changed until you turn read-only on again or start MomentFinder again.", choose: false, check: locks.Check);
        return password != null && locks.TurnOff(sourceId, password);
    }

    /// <summary>Not read-only any more, for good (it was turned off with the password before).</summary>
    public static async Task<bool> UnprotectAsync(Window owner, MomentFinder.Library.ReadOnlyLock locks, long sourceId, string name)
    {
        if (!await ConfirmAsync(owner, "Stop read-only",
                $"\"{name}\" stops being read-only, also after MomentFinder starts again. You can make it read-only again any time.")) return false;
        locks.Unprotect(sourceId);
        return true;
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

    /// <summary>The names given last, the newest first: offered as buttons, a series of photos is mostly of the same few people.</summary>
    private static readonly List<string> LastNames = [];

    /// <summary>
    /// Who is a box: a name, or for a box that already has one "Not Greta" (a mistake), or "not a person" (a hand,
    /// a snow cannon, a stranger: the box goes away for good). A name the analysis only guessed (guess) is confirmed
    /// with one button; the names given last are buttons too, nothing to type.
    /// </summary>
    public static async Task<(WhoAction Action, string? Name)> AskWhoAsync(Window owner, IEnumerable<string> names, string? current,
        bool guess = false)
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
            Text = current == null ? "Who is it? An existing name adds to that person." :
                guess ? $"The analysis thinks it is {current}. Is it?" : $"Marked as {current}. Who is it really?",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        });
        var quick = new WrapPanel();
        content.Children.Add(quick);
        content.Children.Add(box);
        var dialog = Build(current == null ? "Who is it?" : guess ? $"Is it {current}?" : $"Not {current}?", content, out var ok, out var cancel, "OK");
        void Close((WhoAction, string?) r)
        {
            result = r;
            dialog.Close();
        }
        if (guess && current != null)
        {
            var yes = new Button { Content = $"Yes, it is {current}", Margin = new Avalonia.Thickness(0, 0, 8, 4) };
            yes.Classes.Add("accent");
            yes.Click += (_, _) => Close((WhoAction.Name, current));
            quick.Children.Add(yes);
        }
        foreach (var last in LastNames.Where(n => n != current).Take(4))
        {
            var other = new Button { Content = last, Margin = new Avalonia.Thickness(0, 0, 8, 4) };
            ToolTip.SetTip(other, $"It is {last}");
            other.Click += (_, _) => Close((WhoAction.Name, last));
            quick.Children.Add(other);
        }
        quick.IsVisible = quick.Children.Count > 0;
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
        if (result.Item1 == WhoAction.Name)
        {
            LastNames.Remove(result.Item2!.Trim());
            LastNames.Insert(0, result.Item2.Trim());
        }
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

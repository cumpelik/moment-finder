using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using MomentFinder.App.ViewModels;

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

    /// <summary>
    /// The part of a dialog that asks who somebody is: the person's picture (so the answer goes to the right one), the
    /// most likely people as buttons (one click, after the caller's own first button), and a box to write in or to
    /// pick from: everybody, the most likely first. chosen gets the name of a button, picked in the list, or written
    /// and confirmed with Enter. The box is not in the view: it goes into the dialog's last row, beside OK and
    /// Cancel (Build), so its open list covers nothing of the dialog.
    /// </summary>
    private static (Control View, NameBox Box) NameInput(WhoQuestion who, string text, string placeholder, string? without,
        Action<string> chosen, Button? first = null, Control? more = null)
    {
        var box = new NameBox(who.Names) { PlaceholderText = placeholder };
        var quick = new WrapPanel();
        if (first != null) quick.Children.Add(first);
        foreach (var name in who.Names.Take(who.Likely).Where(n => n != without).Take(4))
        {
            var likely = new Button { Content = name, Margin = new Avalonia.Thickness(0, 0, 8, 4) };
            ToolTip.SetTip(likely, $"It is {name}");
            likely.Click += (_, _) => chosen(name);
            quick.Children.Add(likely);
        }
        quick.IsVisible = quick.Children.Count > 0;
        box.Picked += chosen;
        var view = new DockPanel();
        if (Picture(who.Picture) is { } bitmap)
        {
            var picture = new Border
            {
                CornerRadius = new Avalonia.CornerRadius(6),
                ClipToBounds = true,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Avalonia.Thickness(0, 0, 16, 0),
                Child = new Image { Source = bitmap, MaxWidth = 150, MaxHeight = 200 },
            };
            picture.DetachedFromVisualTree += (_, _) => bitmap.Dispose();
            DockPanel.SetDock(picture, Dock.Left);
            view.Children.Add(picture);
        }
        var words = new StackPanel
        {
            Spacing = 8,
            Children = { new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap }, quick },
        };
        if (more != null) words.Children.Add(more);
        view.Children.Add(words);
        return (view, box);
    }

    private static Avalonia.Media.Imaging.Bitmap? Picture(string? path)
    {
        if (path == null || !File.Exists(path)) return null;
        try
        {
            using var stream = File.OpenRead(path);
            return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 300);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The name box has the keyboard and shows its list at once: the answer is one click or a few letters away.</summary>
    private static void OpenWithList(Window dialog, NameBox box) => dialog.Opened += (_, _) =>
    {
        box.Focus();
        Avalonia.Threading.Dispatcher.UIThread.Post(box.ShowAll, Avalonia.Threading.DispatcherPriority.Background);
    };

    /// <summary>Who is it: the person's picture, the most likely people to click, everybody to pick from, or a new name.</summary>
    public static async Task<string?> PromptNameAsync(Window owner, string title, WhoQuestion who)
    {
        string? result = null;
        Window? dialog = null;
        var (view, box) = NameInput(who, "Who is it? An existing name adds to that person.", "Name", null, name =>
        {
            result = name;
            dialog?.Close();
        });
        dialog = Build(title, view, out var ok, out var cancel, "OK", who.Picture == null ? 440 : 560, box);
        ok.Click += (_, _) => { result = box.Text; dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        OpenWithList(dialog, box);
        await dialog.ShowDialog(owner);
        return string.IsNullOrWhiteSpace(result) ? null : NameBox.Known(who.Names, result);
    }

    public enum WhoAction { Cancel, Name, NotThisPerson, Ignore }

    /// <summary>
    /// Who is a box: a name, or for a box that already has one "Not Greta" (a mistake), or "not a person" (a hand,
    /// a snow cannon, a stranger: the box goes away for good). A name the analysis only guessed (guess) is confirmed
    /// with one button; the other likely people are buttons too, and everybody is in the list of the name box.
    /// </summary>
    public static async Task<(WhoAction Action, string? Name)> AskWhoAsync(Window owner, WhoQuestion who, string? current, bool guess = false)
    {
        var result = (WhoAction.Cancel, (string?)null);
        Window? dialog = null;
        void Close((WhoAction, string?) r)
        {
            result = r;
            dialog?.Close();
        }
        Button? yes = null;
        if (guess && current != null)
        {
            yes = new Button { Content = $"Yes, it is {current}", Margin = new Avalonia.Thickness(0, 0, 8, 4) };
            yes.Classes.Add("accent");
            yes.Click += (_, _) => Close((WhoAction.Name, current));
        }
        var actions = new WrapPanel();
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
        var (view, box) = NameInput(who,
            current == null ? "Who is it? An existing name adds to that person." :
            guess ? $"The analysis thinks it is {current}. Is it?" : $"Marked as {current}. Who is it really?",
            current == null ? "Name" : "Another name", current, name => Close((WhoAction.Name, name)), yes, actions);
        dialog = Build(current == null ? "Who is it?" : guess ? $"Is it {current}?" : $"Not {current}?", view, out var ok, out var cancel, "OK",
            who.Picture == null ? 440 : 560, box);
        ok.Click += (_, _) => Close((WhoAction.Name, box.Text));
        cancel.Click += (_, _) => dialog.Close();
        OpenWithList(dialog, box);
        await dialog.ShowDialog(owner);
        if (result.Item1 == WhoAction.Name && string.IsNullOrWhiteSpace(result.Item2)) return (WhoAction.Cancel, null);
        return (result.Item1, result.Item1 == WhoAction.Name ? NameBox.Known(who.Names, result.Item2!) : null);
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

    /// <param name="beside">Fills the last row left of the buttons (the name box: its list opens below the dialog).</param>
    private static Window Build(string title, Control content, out Button ok, out Button cancel, string okText, double width = 440,
        Control? beside = null)
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
        Control last = buttons;
        if (beside != null)
        {
            DockPanel.SetDock(buttons, Dock.Right);
            buttons.Margin = new Avalonia.Thickness(12, 0, 0, 0);
            beside.VerticalAlignment = VerticalAlignment.Center;
            last = new DockPanel { Children = { buttons, beside } };
        }
        DockPanel.SetDock(last, Dock.Bottom);
        last.Margin = new Avalonia.Thickness(0, 16, 0, 0);
        return new Window
        {
            Title = title,
            Width = width,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new DockPanel { Margin = new Avalonia.Thickness(20), Children = { last, content } },
        };
    }
}

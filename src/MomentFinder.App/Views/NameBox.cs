using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace MomentFinder.App.Views;

/// <summary>
/// A person's name, written or picked: a box to write in over the list of everybody, in the order it is given (the
/// most likely people first). The list is part of the dialog, not a popup: a click on a row answers at once and no
/// click is spent on closing anything. Writing narrows the list (any part of a name, capitals and accents do not
/// matter) and marks its first row; Enter takes the marked row, the arrows move the mark. A name nobody has yet is
/// the row "New person: ...", so a new person is never made by a slip.
/// </summary>
public sealed class NameBox : DockPanel
{
    private sealed record Row(string Name, bool New)
    {
        public override string ToString() => New ? $"New person: {Name}" : Name;
    }

    private readonly IReadOnlyList<string> _names;
    private readonly TextBox _written = new();
    private readonly ListBox _list = new()
    {
        // About six rows; the rest scrolls. Fixed, so the dialog does not change its size while one writes.
        Height = 172,
        Margin = new Thickness(0, 6, 0, 0),
        SelectionMode = SelectionMode.Single,
    };

    /// <summary>The name is said: a row was clicked, or Enter pressed on the marked one.</summary>
    public event Action<string>? Picked;

    public NameBox(IReadOnlyList<string> names, string placeholder)
    {
        _names = names;
        _written.PlaceholderText = placeholder;
        _list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(ListBoxItem.PaddingProperty, new Thickness(10, 4)), new Setter(ListBoxItem.MinHeightProperty, 0d) },
        });
        SetDock(_written, Dock.Top);
        Children.Add(_written);
        Children.Add(_list);
        Show();

        _written.TextChanged += (_, _) => Show();
        // The keyboard stays in the box: the arrows move the mark in the list from there.
        _written.AddHandler(KeyDownEvent, (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down: Move(1); break;
                case Key.Up: Move(-1); break;
                case Key.Enter: Take(); break;
                default: return;
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        _list.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Take();
            e.Handled = true;
        };
        _list.Tapped += (_, e) =>
        {
            if ((e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext is Row row)
                Picked?.Invoke(row.Name);
        };
    }

    /// <summary>The marked row's name; null when nothing is marked (nothing written yet, no arrow pressed).</summary>
    public string? Marked => (_list.SelectedItem as Row)?.Name;

    /// <summary>Says the marked name, if there is one (Enter, or the dialog's OK).</summary>
    public void Take()
    {
        if (Marked is { } name) Picked?.Invoke(name);
    }

    public void FocusBox() => _written.Focus();

    private void Show()
    {
        var written = (_written.Text ?? "").Trim();
        var rows = _names.Where(n => Within(n, written)).Select(n => new Row(n, false)).ToList();
        var same = rows.FirstOrDefault(r => Same(r.Name, written));
        if (written.Length > 0 && same == null) rows.Add(new Row(written, true));
        _list.ItemsSource = rows;
        // Written exactly: that person, though somebody more likely has the letters in the name too.
        _list.SelectedItem = written.Length == 0 ? null : same ?? rows[0];
        if (_list.SelectedItem != null) _list.ScrollIntoView(_list.SelectedItem);
    }

    private void Move(int by)
    {
        if (_list.ItemCount == 0) return;
        _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + by, 0, _list.ItemCount - 1);
        if (_list.SelectedItem != null) _list.ScrollIntoView(_list.SelectedItem);
    }

    private const CompareOptions Loosely = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;

    private static bool Within(string name, string written) =>
        written.Length == 0 || CultureInfo.InvariantCulture.CompareInfo.IndexOf(name, written, Loosely) >= 0;

    private static bool Same(string name, string written) => string.Compare(name, written, CultureInfo.InvariantCulture, Loosely) == 0;
}

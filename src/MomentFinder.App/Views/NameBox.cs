using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace MomentFinder.App.Views;

/// <summary>
/// The box for a person's name: written, or picked in its list. The list keeps the order it is given (the most likely
/// people first), opens when the box is clicked or asked to (ShowAll), and writing narrows it: any part of a name,
/// capitals and accents do not matter.
/// </summary>
public sealed class NameBox : AutoCompleteBox
{
    protected override Type StyleKeyOverride => typeof(AutoCompleteBox);

    /// <summary>
    /// The name is said: clicked in the list, or Enter (on the name chosen in the list with the arrows, otherwise
    /// on what is written).
    /// </summary>
    public event Action<string>? Picked;

    public NameBox(IReadOnlyList<string> names)
    {
        ItemsSource = names;
        FilterMode = AutoCompleteFilterMode.Custom;
        ItemFilter = (written, name) => Within(name as string, written);
        MinimumPrefixLength = 0;
        MaxDropDownHeight = 300;
        // The list is drawn in a window of its own, but its mouse and keys come through here. A row is picked as
        // the mouse goes down on it (the list closes at once, there is no click to wait for).
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (NameAt(e.Source) is { } name) Say(name);
            else ShowAll();
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            Say(IsDropDownOpen && SelectionAdapter?.SelectedItem is string chosen ? chosen : Text ?? "");
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Opens the list with what fits the text written so far (everybody, when nothing is written).</summary>
    public void ShowAll()
    {
        if (!IsDropDownOpen) IsDropDownOpen = true;
    }

    /// <summary>The name of the list's row the mouse is on; null anywhere else.</summary>
    private static string? NameAt(object? source) =>
        (source as Visual)?.GetSelfAndVisualAncestors().OfType<ListBoxItem>().FirstOrDefault()?.DataContext as string;

    /// <summary>After the box has dealt with the click or the key itself (the list closes, the text is set).</summary>
    private void Say(string name) => Dispatcher.UIThread.Post(() =>
    {
        if (!string.IsNullOrWhiteSpace(name)) Picked?.Invoke(name);
    });

    private static bool Within(string? name, string? written) =>
        string.IsNullOrEmpty(written) ||
        name != null && CultureInfo.InvariantCulture.CompareInfo.IndexOf(name, written, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    /// <summary>The known name that is the written one but for capitals and accents ("sarka" is "Šárka"); otherwise as written.</summary>
    public static string Known(IEnumerable<string> names, string written)
    {
        written = written.Trim();
        return names.FirstOrDefault(n => string.Compare(n, written, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) == 0)
               ?? written;
    }
}

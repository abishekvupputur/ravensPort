using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace RavensPort.UI.Tests;

/// <summary>
/// How these tests press things.
///
/// Controls are found by <see cref="AutomationProperties.AutomationIdProperty"/> rather than by
/// their text, and that is a deliberate cost paid in the views: a test that looks for the button
/// reading "Start in single use" breaks when someone improves the wording, which is a change that
/// should not break anything. An automation id is a contract that says "something drives this".
///
/// Everything here goes through the dispatcher, because Avalonia asserts on thread affinity the
/// same way headlessly as it does on a desktop — a command invoked off the UI thread throws rather
/// than misbehaving quietly.
/// </summary>
internal static class UiDriver
{
    /// <summary>How long a UI expectation is given before it is called a failure.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Puts a view in a window and shows it. Showing matters: a control that was never laid out has
    /// no template applied and no visual children, so nothing below could find it.
    /// </summary>
    public static Window Show<TView>(object dataContext) where TView : Control, new()
    {
        var window = new Window
        {
            Content = new TView(),
            DataContext = dataContext,

            // Big enough that nothing this suite drives is scrolled out of the visual tree.
            Width = 1400,
            Height = 1000,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        return window;
    }

    /// <summary>
    /// Every control carrying this automation id, in visual-tree order.
    ///
    /// Needed because some ids are on a template rather than on a control: a route's credential
    /// editors, a funnel's source check boxes. There is one per item, they are all called the same
    /// thing, and which one is meant is a position — the second credential attached to this route.
    /// </summary>
    public static IReadOnlyList<T> FindAll<T>(Visual root, string automationId) where T : Control =>
        [.. root.GetVisualDescendants()
            .OfType<T>()
            .Where(c => AutomationProperties.GetAutomationId(c) == automationId)];

    /// <summary>The one control carrying this automation id, or a failure naming what was there.</summary>
    public static T Find<T>(Visual root, string automationId) where T : Control
    {
        var matches = FindAll<T>(root, automationId);

        if (matches.Count == 1) return matches[0];

        var seen = root.GetVisualDescendants()
            .OfType<Control>()
            .Select(AutomationProperties.GetAutomationId)
            .Where(id => !string.IsNullOrEmpty(id))
            .Order()
            .ToList();

        throw new InvalidOperationException(
            $"Expected exactly one {typeof(T).Name} with AutomationId '{automationId}', found {matches.Count}. "
            + $"Ids present in this view: {(seen.Count == 0 ? "(none)" : string.Join(", ", seen))}");
    }

    /// <summary>
    /// Presses a button and lets whatever it started run.
    ///
    /// Command rather than a synthesised pointer press. The click itself is Avalonia's to get
    /// right; what these tests are for is what the command does, and a real click would additionally
    /// depend on the button not being covered — which is a layout assertion wearing a behaviour
    /// assertion's clothes. IsEnabled is checked, because that is the part of a click that carries
    /// meaning: a disabled button is the view model refusing, and a test that fires its command
    /// anyway would pass through a refusal the user could not.
    /// </summary>
    public static async Task ClickAsync(Visual root, string automationId)
    {
        var button = Find<Button>(root, automationId);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Assert.True(button.IsEnabled, $"'{automationId}' is disabled, so a user could not press it.");
            Assert.True(button.IsVisible, $"'{automationId}' is not visible, so a user could not press it.");

            if (button.Command is { } command && command.CanExecute(button.CommandParameter))
            {
                command.Execute(button.CommandParameter);
            }
            else
            {
                throw new InvalidOperationException(
                    $"'{automationId}' has no command that can execute right now.");
            }
        });

        await PumpAsync();
    }

    /// <summary>Types into a box the way a user does — through the property the binding watches.</summary>
    public static async Task TypeAsync(Visual root, string automationId, string text)
    {
        var box = Find<TextBox>(root, automationId);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Assert.True(box.IsEnabled, $"'{automationId}' is disabled, so a user could not type in it.");
            box.Text = text;
        });

        await PumpAsync();
    }

    /// <summary>Picks an item in a combo box by the text it shows.</summary>
    public static async Task SelectAsync(Visual root, string automationId, string label)
    {
        var combo = Find<ComboBox>(root, automationId);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var match = combo.Items.FirstOrDefault(i => Label(i) == label)
                ?? throw new InvalidOperationException(
                    $"'{automationId}' has no item reading '{label}'. It offers: "
                    + string.Join(", ", combo.Items.Select(Label)));

            combo.SelectedItem = match;
        });

        await PumpAsync();
    }

    /// <summary>
    /// What a combo box item reads as on screen.
    ///
    /// The item templates bind a property rather than showing ToString(), so a test that matched on
    /// ToString() would be matching text no user ever sees — and for a record it would be the whole
    /// object printed out. These three property names are the ones those templates bind.
    /// </summary>
    private static string Label(object? item) => item switch
    {
        null => "",
        Enum e => e.ToString(),
        _ => Property(item, "Label") ?? Property(item, "Name") ?? Property(item, "PathPrefix")
             ?? item.ToString() ?? "",
    };

    private static string? Property(object item, string name) =>
        item.GetType().GetProperty(name)?.GetValue(item)?.ToString();

    /// <summary>Sets a check box, if it is not already where it should be.</summary>
    public static async Task CheckAsync(Visual root, string automationId, bool value)
    {
        var box = Find<CheckBox>(root, automationId);

        await Dispatcher.UIThread.InvokeAsync(() => box.IsChecked = value);
        await PumpAsync();
    }

    /// <summary>
    /// Selects a row in a grid, which is how the row's editor comes into existence.
    ///
    /// Route and funnel editing lives in row details shown only for the selected row, so there is
    /// nothing in the visual tree to drive until something is selected — and, usefully, never more
    /// than one row's worth of it, which is what keeps the ids inside those templates unambiguous.
    /// </summary>
    public static async Task SelectRowAsync<TItem>(Visual root, string automationId, Func<TItem, bool> match)
    {
        var grid = Find<DataGrid>(root, automationId);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var item = grid.ItemsSource!.OfType<TItem>().FirstOrDefault(match)
                ?? throw new InvalidOperationException($"No row in '{automationId}' matched.");

            grid.SelectedItem = item;
        });

        await PumpAsync();
    }

    /// <summary>Types into the nth box carrying this id — see <see cref="FindAll{T}"/>.</summary>
    public static async Task TypeNthAsync(Visual root, string automationId, int index, string text)
    {
        var boxes = FindAll<TextBox>(root, automationId);
        Assert.True(index < boxes.Count,
            $"wanted '{automationId}' #{index} but the view has {boxes.Count}");

        await Dispatcher.UIThread.InvokeAsync(() => boxes[index].Text = text);
        await PumpAsync();
    }

    /// <summary>Picks in the nth combo carrying this id, by the text an item shows.</summary>
    public static async Task SelectNthAsync(Visual root, string automationId, int index, string label)
    {
        var combos = FindAll<ComboBox>(root, automationId);
        Assert.True(index < combos.Count,
            $"wanted '{automationId}' #{index} but the view has {combos.Count}");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var combo = combos[index];
            var match = combo.Items.FirstOrDefault(i => Label(i) == label)
                ?? throw new InvalidOperationException(
                    $"'{automationId}' #{index} has no item reading '{label}'. It offers: "
                    + string.Join(", ", combo.Items.Select(Label)));

            combo.SelectedItem = match;
        });

        await PumpAsync();
    }

    /// <summary>Ticks the nth check box carrying this id.</summary>
    public static async Task CheckNthAsync(Visual root, string automationId, int index, bool value)
    {
        var boxes = FindAll<CheckBox>(root, automationId);
        Assert.True(index < boxes.Count,
            $"wanted '{automationId}' #{index} but the view has {boxes.Count}");

        await Dispatcher.UIThread.InvokeAsync(() => boxes[index].IsChecked = value);
        await PumpAsync();
    }

    /// <summary>The text a control is currently showing.</summary>
    public static string TextOf(Visual root, string automationId) =>
        Find<TextBlock>(root, automationId).Text ?? "";

    /// <summary>
    /// Waits for something to become true, pumping the dispatcher while it does.
    ///
    /// Every button here starts work that finishes on another thread and comes back to the UI, so
    /// asserting straight after a click is a race that passes on a fast machine. A timeout failure
    /// names the condition rather than reporting a stale assertion further down.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, string? because = null)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            await PumpAsync();

            if (condition()) return;

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Waited {Patience.TotalSeconds:N0}s for {because ?? "a UI condition"} and it never came true.");
    }

    /// <summary>Lets queued UI work run, including the continuations a command posted back.</summary>
    public static async Task PumpAsync()
    {
        for (var i = 0; i < 4; i++)
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            Dispatcher.UIThread.RunJobs();
            await Task.Yield();
        }
    }
}

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Interactivity;
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
        // The parking spot exists so that leaving a field means something. Several boxes bind with
        // UpdateSourceTrigger=LostFocus, and focus only leaves a control when it arrives somewhere
        // else — focusing the window is a no-op headlessly, so without a real focusable control to
        // move to, typing changes the screen and never reaches the view model.
        var park = new Button { Content = "focus park", Width = 1, Height = 1 };

        var window = new Window
        {
            Content = new DockPanel
            {
                Children =
                {
                    park,
                    new TView(),
                },
            },
            DataContext = dataContext,

            // Big enough that nothing this suite drives is scrolled out of the visual tree.
            Width = 1400,
            Height = 1000,
        };

        window.Show();
        Dispatcher.UIThread.RunJobs();

        FocusParks[window] = park;

        return window;
    }

    /// <summary>Where focus goes when a field is left. One per window shown.</summary>
    private static readonly Dictionary<Window, Button> FocusParks = [];

    /// <summary>
    /// Shows a window that already exists, rather than wrapping a view in one.
    ///
    /// For the shell: MainWindow is a Window, so it cannot be the content of another, and it comes
    /// from the container with all five tabs wired the way the app wires them. No focus park — it
    /// owns its own content, so anything typed into it must be a control this window already has.
    /// </summary>
    public static Window ShowWindow(Window window)
    {
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

    /// <summary>
    /// Presses the button belonging to one row of a list.
    ///
    /// Matched on the item the button would act on rather than on its position, because position is
    /// what a sort order or a filter changes. Every one of these buttons already carries its row's
    /// view model as CommandParameter — that is how the command knows what to act on — so it is
    /// also the thing that identifies the button.
    /// </summary>
    public static async Task ClickForItemAsync<TItem>(Visual root, string automationId, Func<TItem, bool> match)
    {
        var buttons = FindAll<Button>(root, automationId);

        var button = buttons.FirstOrDefault(b => b.CommandParameter is TItem item && match(item))
            ?? throw new InvalidOperationException(
                $"No '{automationId}' button belongs to a matching row; the view has {buttons.Count} of them.");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Assert.True(button.IsEnabled, $"'{automationId}' is disabled for this row.");

            if (button.Command is { } command && command.CanExecute(button.CommandParameter))
            {
                command.Execute(button.CommandParameter);
            }
            else
            {
                throw new InvalidOperationException($"'{automationId}' cannot execute for this row.");
            }
        });

        await PumpAsync();
    }

    /// <summary>Types into a box the way a user does — including leaving it afterwards.</summary>
    public static async Task TypeAsync(Visual root, string automationId, string text)
    {
        var box = Find<TextBox>(root, automationId);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Assert.True(box.IsEnabled, $"'{automationId}' is disabled, so a user could not type in it.");
            Fill(box, text);
        });

        await PumpAsync();
    }

    /// <summary>
    /// Puts text in a box and then leaves it, which is not a flourish.
    ///
    /// Several of these boxes bind with UpdateSourceTrigger=LostFocus — sensibly, so a route’s
    /// parameter name is not rewritten and re-saved on every keystroke. Setting Text alone therefore
    /// changes what is on screen and nothing else: the view model never hears about it, the route is
    /// saved with a blank field, and the only symptom is a header that does not arrive. Focusing the
    /// box and then clearing focus is what a person does by moving to the next field, and it is what
    /// makes the binding fire.
    /// </summary>
    private static void Fill(TextBox box, string text)
    {
        box.Focus();
        box.Text = text;

        // Leaving the field, which is what makes a LostFocus binding fire. Focus has to genuinely
        // arrive somewhere else: raising the event by hand throws, because Avalonia builds a
        // FocusChangedEventArgs its handler expects, and focusing the window does nothing at all
        // headlessly. So it moves to the parking button Show put in the window.
        if (TopLevel.GetTopLevel(box) is Window window && FocusParks.TryGetValue(window, out var park))
        {
            park.Focus();
        }
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

    /// <summary>
    /// The grid row belonging to one item, to be searched inside instead of the whole view.
    ///
    /// Necessary because a DataGrid does not keep only the selected row's details realized: the
    /// previously selected row's are still in the tree, so ids inside that template appear twice
    /// and looking for exactly one of them fails. Scoping to the row makes the position of a
    /// credential editor mean "on this route" rather than "somewhere in this grid".
    /// </summary>
    public static DataGridRow Row<TItem>(Visual root, string gridAutomationId, Func<TItem, bool> match)
    {
        var grid = Find<DataGrid>(root, gridAutomationId);

        return grid.GetVisualDescendants()
            .OfType<DataGridRow>()
            .FirstOrDefault(r => r.DataContext is TItem item && match(item))
            ?? throw new InvalidOperationException(
                $"No realized row in '{gridAutomationId}' matched. Rows present: "
                + grid.GetVisualDescendants().OfType<DataGridRow>().Count());
    }

    /// <summary>
    /// The smallest visual holding one item of a list, to be searched inside instead of the view.
    ///
    /// The grid rows above have a type to look for; an ItemsControl has no such thing, so this
    /// finds the outermost control whose DataContext is the item — everything inside it inherits
    /// that DataContext, so the whole item is reachable from there. Same purpose either way: "the
    /// tools list of the beta source" rather than "one of the six tool lists on this tab".
    /// </summary>
    public static Control Container<TItem>(Visual root, Func<TItem, bool> match)
    {
        var matches = root.GetVisualDescendants()
            .OfType<Control>()
            .Where(c => c.DataContext is TItem item && match(item))
            .ToList();

        if (matches.Count == 0)
        {
            throw new InvalidOperationException(
                $"Nothing in this view is bound to a matching {typeof(TItem).Name}.");
        }

        // The shallowest, which is the item's own container. Everything inside an item inherits its
        // DataContext, so most of those matches are controls *within* the item — a check box, the
        // panel holding its name — and searching from one of those finds only its own subtree.
        return matches.MinBy(Depth)!;
    }

    private static int Depth(Visual visual)
    {
        var depth = 0;
        for (var v = visual.GetVisualParent(); v is not null; v = v.GetVisualParent()) depth++;
        return depth;
    }

    /// <summary>Selects a row and hands back the row itself, ready to be driven.</summary>
    public static async Task<DataGridRow> SelectAndOpenRowAsync<TItem>(
        Visual root, string gridAutomationId, Func<TItem, bool> match)
    {
        await SelectRowAsync(root, gridAutomationId, match);
        return Row(root, gridAutomationId, match);
    }

    /// <summary>Types into the nth box carrying this id — see <see cref="FindAll{T}"/>.</summary>
    public static async Task TypeNthAsync(Visual root, string automationId, int index, string text)
    {
        var boxes = FindAll<TextBox>(root, automationId);
        Assert.True(index < boxes.Count,
            $"wanted '{automationId}' #{index} but the view has {boxes.Count}");

        await Dispatcher.UIThread.InvokeAsync(() => Fill(boxes[index], text));
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

    /// <summary>Presses the nth button carrying this id — see <see cref="FindAll{T}"/>.</summary>
    public static async Task ClickNthAsync(Visual root, string automationId, int index)
    {
        var buttons = FindAll<Button>(root, automationId);
        Assert.True(index < buttons.Count,
            $"wanted '{automationId}' #{index} but the view has {buttons.Count}");

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var button = buttons[index];
            Assert.True(button.IsEnabled, $"'{automationId}' #{index} is disabled.");

            if (button.Command is { } command && command.CanExecute(button.CommandParameter))
            {
                command.Execute(button.CommandParameter);
            }
            else
            {
                throw new InvalidOperationException($"'{automationId}' #{index} cannot execute right now.");
            }
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
    /// <summary>
    /// The same, with the reason built only if it is needed.
    ///
    /// Worth having as its own overload: an interpolated string is evaluated where it is written, so
    /// a message that reports live state describes the moment before the wait rather than the moment
    /// it gave up — which is exactly backwards for a diagnostic.
    /// </summary>
    public static async Task UntilAsync(Func<bool> condition, Func<string> because)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (DateTime.UtcNow < deadline)
        {
            await PumpAsync();

            if (condition()) return;

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"Waited {Patience.TotalSeconds:N0}s for {because()} and it never came true.");
    }

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

    /// <summary>
    /// Runs something on the UI thread and lets what it started settle.
    ///
    /// For the few things a test drives that no control exposes — a command bound to a template this
    /// suite has no id for, on a window it did not build.
    /// </summary>
    public static async Task RunOnUiAsync(Action action)
    {
        await Dispatcher.UIThread.InvokeAsync(action);
        await PumpAsync();
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

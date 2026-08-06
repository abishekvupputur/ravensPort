using Avalonia.Controls;

namespace RavensPort.Views;

/// <summary>
/// Nothing here, where the WPF version had sixty lines.
///
/// All of it existed to work around WPF's PasswordBox, whose Password property is not a
/// DependencyProperty and so cannot be bound: the view had to copy the box into the view model on
/// every keystroke, and then watch the view model for a reset so it could clear the box again —
/// without that second half, a saved form still showed the old secret and the next credential was
/// stored with an empty one. Avalonia has no PasswordBox; a TextBox with PasswordChar binds like
/// any other, in both directions, so the whole mechanism is deleted rather than ported.
/// </summary>
public partial class CredentialsView : UserControl
{
    public CredentialsView() => InitializeComponent();
}

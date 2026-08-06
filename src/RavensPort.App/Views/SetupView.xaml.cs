using System.Windows.Controls;
using RavensPort.UI.ViewModels;
using UserControl = System.Windows.Controls.UserControl;

namespace RavensPort.App.Views;

public partial class SetupView : UserControl
{
    public SetupView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Pushes the typed service-account token into the card it belongs to.
    ///
    /// <see cref="PasswordBox.Password"/> is not a DependencyProperty and cannot be bound, which is
    /// why this exists at all — the same reason the credential and certificate fields have handlers
    /// of their own. Read off <c>sender</c> rather than a named field because the box lives inside
    /// the per-manager template, so there is one per card and no field is generated for it.
    ///
    /// One-way, box to view model. Nothing ever writes back: the token is not redisplayed, and the
    /// box is discarded along with the card when the page rebuilds after a connect.
    /// </summary>
    private void ServiceTokenBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (sender is PasswordBox box && box.DataContext is ManagerCardViewModel card)
        {
            card.ServiceToken = box.Password;
        }
    }
}

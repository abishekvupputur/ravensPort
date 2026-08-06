using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using UserControl = System.Windows.Controls.UserControl;
using RavensPort.UI.ViewModels;

namespace RavensPort.App.Views;

public partial class CredentialsView : UserControl
{
    public CredentialsView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged previous) previous.PropertyChanged -= OnViewModelPropertyChanged;
        if (e.NewValue is CredentialsViewModel current) current.PropertyChanged += OnViewModelPropertyChanged;
    }

    private void ClientSecretBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is CredentialsViewModel vm)
        {
            vm.NewClientSecret = ClientSecretBox.Password;
        }
    }

    private void ApiKeyBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is CredentialsViewModel vm)
        {
            vm.NewApiKey = ApiKeyBox.Password;
        }
    }

    /// <summary>
    /// PasswordBox.Password is not a DependencyProperty, so it cannot be bound and the flow
    /// above is one-way: box to view model. That left the box still showing the previous
    /// secret after a save, edit, or cancel had cleared the view model's copy — so the form
    /// looked filled while the view model held "", and adding a second credential without
    /// retyping stored an empty client secret. It also left a secret on screen indefinitely.
    /// </summary>
    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CredentialsViewModel vm) return;

        // Only ever clears, and only when the two have actually diverged. Pushing the view
        // model's value back into the box in general would fight the user's typing, since
        // every keystroke round-trips through here.
        switch (e.PropertyName)
        {
            case nameof(CredentialsViewModel.NewClientSecret)
                when vm.NewClientSecret.Length == 0 && ClientSecretBox.Password.Length > 0:
                ClientSecretBox.Clear();
                break;

            case nameof(CredentialsViewModel.NewApiKey)
                when vm.NewApiKey.Length == 0 && ApiKeyBox.Password.Length > 0:
                ApiKeyBox.Clear();
                break;
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using ReverseX.App.ViewModels;

namespace ReverseX.App.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage() => InitializeComponent();

    private void OnKeyChanged(object sender, RoutedEventArgs e) =>
        KeyHint.Visibility = KeyBox.Password.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The key goes straight from the PasswordBox to the encrypted store; the box is cleared right after.</summary>
    private void OnSaveKey(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel vm) return;
        vm.SaveKey(KeyBox.Password);
        if (vm.HasKey) KeyBox.Clear();
    }
}

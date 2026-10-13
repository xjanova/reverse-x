using System.Windows;
using System.Windows.Controls;

namespace ReverseX.App.Views;

public partial class MainShell : UserControl
{
    public MainShell()
    {
        InitializeComponent();
    }

    private Window? Host => Window.GetWindow(this);

    private void OnMinimize(object sender, RoutedEventArgs e)
    {
        if (Host is { } w) w.WindowState = WindowState.Minimized;
    }

    private void OnMaximize(object sender, RoutedEventArgs e)
    {
        if (Host is { } w)
            w.WindowState = w.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Host?.Close();

    /// <summary>Maximised borderless windows overhang the screen by the resize border; the host pads the shell to compensate.</summary>
    public void SetMaximized(bool maximized)
    {
        Root.Margin = maximized ? new Thickness(SystemParameters.WindowResizeBorderThickness.Left + 1) : new Thickness(0);
        MaxButton.Content = maximized ? "\uE923" : "\uE922";
    }
}

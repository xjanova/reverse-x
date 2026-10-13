using System.Windows;
using ReverseX.App.ViewModels;

namespace ReverseX.App;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        Shell.DataContext = vm;
        StateChanged += (_, _) => Shell.SetMaximized(WindowState == WindowState.Maximized);
    }
}

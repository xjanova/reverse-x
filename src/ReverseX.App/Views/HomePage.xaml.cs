using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using ReverseX.App.Services;
using ReverseX.App.ViewModels;

namespace ReverseX.App.Views;

public partial class HomePage : UserControl
{
    public HomePage() => InitializeComponent();

    private HomeViewModel? Vm => DataContext as HomeViewModel;

    private void OnDrag(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            if (Vm is { } vm) vm.IsDragging = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        if (Vm is { } vm) vm.IsDragging = false;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (Vm is not { } vm) return;
        vm.IsDragging = false;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await vm.AddPathsAsync(paths);
        e.Handled = true;
    }

    private async void OnPickFile(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new OpenFileDialog
        {
            Title = Loc.Get("Dlg.PickFile"),
            Filter = Loc.Get("Dlg.AllFiles") + " (*.*)|*.*",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true) await vm.AddPathsAsync(dlg.FileNames);
    }

    private async void OnPickFolder(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var dlg = new OpenFolderDialog { Title = Loc.Get("Dlg.PickFolder"), Multiselect = false };
        if (dlg.ShowDialog() == true) await vm.AddPathsAsync(new[] { dlg.FolderName });
    }
}

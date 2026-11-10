using System.Windows.Controls;

namespace ReverseX.App.Views;

public partial class BinaryPage : UserControl
{
    public BinaryPage() => InitializeComponent();

    /// <summary>A function picked from the refs card may be far down the list: bring it into view.</summary>
    private void OnSymbolSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: { } item } list) list.ScrollIntoView(item);
    }
}

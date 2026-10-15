using System.Windows.Controls;
using ReverseX.App.ViewModels;

namespace ReverseX.App.Views;

public partial class InsightPage : UserControl
{
    public InsightPage() => InitializeComponent();

    // DataTable columns are c0..cN; show the tab's real header text and hide the row index.
    private void OnAutoGeneratingColumn(object sender, DataGridAutoGeneratingColumnEventArgs e)
    {
        if (e.PropertyName == InsightTab.IndexColumn) { e.Cancel = true; return; }
        if (Table.DataContext is InsightTab tab) e.Column.Header = tab.HeaderFor(e.PropertyName);
        e.Column.MaxWidth = 520;
    }
}

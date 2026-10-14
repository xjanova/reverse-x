using System.Windows.Controls;

namespace ReverseX.App.Views;

public partial class AiPage : UserControl
{
    private bool _stick = true;

    public AiPage() => InitializeComponent();

    /// <summary>Follow new text while the user is at the bottom; stop following once they scroll up to read.</summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            _stick = Scroller.VerticalOffset >= Scroller.ScrollableHeight - 24;
        else if (_stick)
            Scroller.ScrollToEnd();
    }
}

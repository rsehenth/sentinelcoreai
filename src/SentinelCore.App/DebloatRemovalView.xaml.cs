using System.Windows.Controls;
using SentinelCore.Application;

namespace SentinelCore.App;

public partial class DebloatRemovalView : UserControl
{
    public DebloatRemovalView()
    {
        InitializeComponent();
        DataContext = DebloatRemovalPresentation.Unavailable();
    }

    public void Present(DebloatRemovalPresentation presentation) => DataContext = presentation;
}

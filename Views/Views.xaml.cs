using System.Windows.Controls;

namespace HardwareCenter.Views;

public partial class DashboardView : UserControl
{
    public DashboardView() => InitializeComponent();
}

public partial class DevicePageView : UserControl
{
    public DevicePageView()
    {
        InitializeComponent();

        // One view instance is reused for every device page, so start each page at its top.
        DataContextChanged += (_, _) => Scroll.ScrollToTop();
    }
}

public partial class ThermalsView : UserControl
{
    public ThermalsView() => InitializeComponent();
}

public partial class SensorsView : UserControl
{
    public SensorsView() => InitializeComponent();
}

public partial class SystemView : UserControl
{
    public SystemView() => InitializeComponent();
}

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();
}

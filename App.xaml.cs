using System.Windows;
using System.Windows.Media;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace NotAirplay;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ApplicationThemeManager.Apply(ApplicationTheme.Dark, WindowBackdropType.None, false);
        ApplicationAccentColorManager.Apply(
            Color.FromRgb(0x04, 0x78, 0x57),
            Color.FromRgb(0x34, 0xD3, 0x99),
            Color.FromRgb(0x10, 0xB9, 0x81),
            Color.FromRgb(0x06, 0x5F, 0x46));
    }
}

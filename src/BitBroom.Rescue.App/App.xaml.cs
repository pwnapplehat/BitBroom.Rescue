using System.Windows;
using System.Windows.Threading;

namespace BitBroom.Rescue.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        Wpf.Ui.Appearance.ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x38, 0xBD, 0xF8),
            Wpf.Ui.Appearance.ApplicationTheme.Dark);
        base.OnStartup(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            "BitBroom Rescue hit an unexpected error:\n\n" + e.Exception.Message,
            "BitBroom Rescue", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

using System.Windows;
using System.Windows.Media;
using BitBroom.Rescue.App.ViewModels;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace BitBroom.Rescue.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(Confirm, PickFolder, PickImageToOpen, PickImageToSave);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Same taskbar-style acrylic as BitBroom (Cleaner): the DWM backdrop only shows
        // through transparent pixels, so apply dark theme + acrylic, then clear the window
        // background so the wallpaper blur comes through with a smoke tint for contrast.
        // When acrylic can't apply — Windows 10, or transparency effects disabled in
        // Settings — fall back to the stock solid dark background. XAML says None so DWM
        // never paints its own washed-out acrylic over our solid fallback.
        bool wantAcrylic = IsSystemTransparencyEnabled();
        ApplicationThemeManager.Apply(
            ApplicationTheme.Dark,
            wantAcrylic ? WindowBackdropType.Acrylic : WindowBackdropType.None,
            updateAccent: false);

        if (wantAcrylic && WindowBackdrop.ApplyBackdrop(this, WindowBackdropType.Acrylic))
        {
            WindowBackdropType = WindowBackdropType.Acrylic;
            Background = Brushes.Transparent;
            SmokeTint.Visibility = Visibility.Visible;
        }
        else if (TryFindResource("ApplicationBackgroundBrush") is Brush solid)
        {
            Background = solid;
        }
    }

    /// <summary>Settings → Personalization → Colors → "Transparency effects".</summary>
    private static bool IsSystemTransparencyEnabled()
    {
        try
        {
            using Microsoft.Win32.RegistryKey? key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private bool Confirm(string title, string message)
    {
        System.Windows.MessageBoxResult result = System.Windows.MessageBox.Show(
            message, "BitBroom Rescue — " + title,
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);
        return result == System.Windows.MessageBoxResult.OK;
    }

    private string? PickFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose a folder on a DIFFERENT drive to save recovered files",
        };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }

    private string? PickImageToOpen()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open a disk image",
            Filter = "Disk images (*.img;*.dd;*.bin;*.raw)|*.img;*.dd;*.bin;*.raw|All files (*.*)|*.*",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private string? PickImageToSave()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save the clone image on a DIFFERENT drive",
            Filter = "Disk image (*.img)|*.img",
            FileName = "rescue-clone.img",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}

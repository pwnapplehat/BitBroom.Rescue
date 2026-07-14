using BitBroom.Rescue.App.ViewModels;
using Wpf.Ui.Controls;

namespace BitBroom.Rescue.App;

public partial class MainWindow : FluentWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(Confirm, PickFolder, PickImageToOpen, PickImageToSave);
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

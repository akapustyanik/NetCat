using Microsoft.Win32;
using v2rayN.Views;

namespace v2rayN.Common;

internal class UI
{
    private static readonly string caption = Global.AppName;

    public static void Show(string msg)
    {
        MessageBox.Show(msg, caption, MessageBoxButton.OK, MessageBoxImage.Information, MessageBoxResult.OK);
    }

    public static MessageBoxResult ShowYesNo(string msg)
    {
        return ShowStyledYesNo(msg);
    }

    public static bool? OpenFileDialog(out string fileName, string filter)
    {
        fileName = string.Empty;

        var fileDialog = new OpenFileDialog
        {
            Multiselect = false,
            Filter = filter
        };

        if (fileDialog.ShowDialog() != true)
        {
            return false;
        }
        fileName = fileDialog.FileName;

        return true;
    }

    public static bool? SaveFileDialog(out string fileName, string filter)
    {
        fileName = string.Empty;

        SaveFileDialog fileDialog = new()
        {
            Filter = filter,
            FilterIndex = 2,
            RestoreDirectory = true
        };
        if (fileDialog.ShowDialog() != true)
        {
            return false;
        }

        fileName = fileDialog.FileName;

        return true;
    }

    public static bool? OpenZapretDialog(out string folderPath)
    {
        folderPath = string.Empty;
        var fileDialog = new OpenFileDialog
        {
            Multiselect = false,
            CheckFileExists = true,
            Filter = "winws.exe|winws.exe|Executable (*.exe)|*.exe|All files (*.*)|*.*",
            Title = "Select zapret bin\\winws.exe"
        };

        if (fileDialog.ShowDialog() != true)
        {
            return false;
        }

        var binPath = Path.GetDirectoryName(fileDialog.FileName);
        folderPath = Directory.GetParent(binPath ?? string.Empty)?.FullName ?? string.Empty;
        return true;
    }

    private static MessageBoxResult ShowStyledYesNo(string msg)
    {
        var confirmText = string.Equals(msg, ResUI.RemoveServer, StringComparison.Ordinal)
            ? ResUI.menuRemoveServer
            : ResUI.TbConfirm;
        var dialog = new ConfirmDialogWindow(ResUI.TbConfirm, msg, confirmText);
        return dialog.ShowDialog() == true ? MessageBoxResult.Yes : MessageBoxResult.No;
    }
}

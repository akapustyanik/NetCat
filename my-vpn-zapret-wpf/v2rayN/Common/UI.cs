using Microsoft.Win32;
using System.Windows.Controls;
using System.Windows.Media;

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
        var owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);
        var result = MessageBoxResult.No;

        var titleText = new TextBlock
        {
            Text = "Подтверждение",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = CreateBrush("#E8EEF7"),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var bodyText = new TextBlock
        {
            Text = msg,
            FontSize = 13,
            Foreground = CreateBrush("#B8C4D8"),
            TextWrapping = TextWrapping.Wrap
        };

        var confirmButton = CreateDialogButton("Да", "#4F8CFF", Brushes.White, true);
        var cancelButton = CreateDialogButton("Нет", "#162235", CreateBrush("#E8EEF7"), false);

        var window = new Window
        {
            Title = caption,
            Owner = owner,
            WindowStartupLocation = owner == null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Width = 420,
            Height = 220,
            MinWidth = 360,
            MinHeight = 200,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Background = CreateBrush("#0B1220"),
            Foreground = CreateBrush("#E8EEF7"),
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true
        };

        confirmButton.Click += (_, _) =>
        {
            result = MessageBoxResult.Yes;
            window.DialogResult = true;
            window.Close();
        };

        cancelButton.Click += (_, _) =>
        {
            result = MessageBoxResult.No;
            window.DialogResult = false;
            window.Close();
        };

        window.Content = new Border
        {
            Background = CreateBrush("#111A2B"),
            BorderBrush = CreateBrush("#253246"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(20),
            Child = new Grid
            {
                RowDefinitions =
                {
                    new RowDefinition { Height = GridLength.Auto },
                    new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
                    new RowDefinition { Height = GridLength.Auto }
                },
                Children =
                {
                    titleText,
                    bodyText,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 18, 0, 0),
                        Children =
                        {
                            cancelButton,
                            confirmButton
                        }
                    }
                }
            }
        };

        Grid.SetRow(bodyText, 1);
        Grid.SetRow((UIElement)((Grid)((Border)window.Content).Child).Children[2], 2);
        window.ShowDialog();
        return result;
    }

    private static Button CreateDialogButton(string text, string background, Brush foreground, bool primary)
    {
        return new Button
        {
            Content = text,
            MinWidth = 96,
            MinHeight = 40,
            Margin = primary ? new Thickness(10, 0, 0, 0) : new Thickness(0),
            Padding = new Thickness(16, 8, 16, 8),
            Background = CreateBrush(background),
            Foreground = foreground,
            BorderBrush = primary ? CreateBrush(background) : CreateBrush("#253246"),
            BorderThickness = new Thickness(1),
            FontSize = 13,
            FontWeight = FontWeights.SemiBold
        };
    }

    private static SolidColorBrush CreateBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFrom(hex)!;
        brush.Freeze();
        return brush;
    }
}

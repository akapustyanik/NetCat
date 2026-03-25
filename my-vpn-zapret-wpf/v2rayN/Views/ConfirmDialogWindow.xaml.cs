namespace v2rayN.Views;

public partial class ConfirmDialogWindow : Window
{
    public ConfirmDialogWindow(string title, string message, string confirmText)
    {
        InitializeComponent();

        Owner = Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
            ?? Application.Current?.MainWindow;
        Title = Global.AppName;
        txtTitle.Text = title;
        txtMessage.Text = message;
        btnConfirm.Content = confirmText;
        btnCancel.Content = ResUI.TbCancel;

        btnConfirm.Click += (_, _) =>
        {
            DialogResult = true;
            Close();
        };

        btnCancel.Click += (_, _) =>
        {
            DialogResult = false;
            Close();
        };

        Loaded += (_, _) =>
        {
            btnCancel.Focus();
            Activate();
        };
        WindowsUtils.SetDarkBorder(this, AppManager.Instance.Config.UiItem.CurrentTheme);
    }
}

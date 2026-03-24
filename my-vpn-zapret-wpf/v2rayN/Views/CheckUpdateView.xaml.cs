namespace v2rayN.Views;

public partial class CheckUpdateView
{
    public CheckUpdateView()
    {
        InitializeComponent();

        ViewModel = new CheckUpdateViewModel(UpdateViewHandler);

        this.WhenActivated(disposables =>
        {
            this.OneWayBind(ViewModel, vm => vm.CheckUpdateModels, v => v.lstCheckUpdates.ItemsSource).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.CheckUpdateCmd, v => v.btnCheckUpdate).DisposeWith(disposables);
        });
    }

    private async Task<bool> UpdateViewHandler(EViewAction action, object? obj)
    {
        switch (action)
        {
            case EViewAction.SelectLocalUpdatePackage:
                if (obj is string[] selectedPath)
                {
                    string fileName = string.Empty;
                    var result = await Application.Current.Dispatcher.InvokeAsync(() =>
                        UI.OpenFileDialog(out fileName, "Zip archive|*.zip|All files|*.*"));

                    if (result == true)
                    {
                        selectedPath[0] = fileName;
                        return true;
                    }
                }
                return false;
        }

        return await Task.FromResult(true);
    }
}

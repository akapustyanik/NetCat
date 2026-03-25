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

    private void OnRootPreviewDragOver(object sender, DragEventArgs e)
    {
        if (TryGetDroppedZipPath(e.Data, out _))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnRootDrop(object sender, DragEventArgs e)
    {
        if (!TryGetDroppedZipPath(e.Data, out var zipPath))
        {
            return;
        }

        if (ViewModel == null)
        {
            return;
        }

        await ViewModel.TryInstallLocalPackageFromPathAsync(zipPath);
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

    private static bool TryGetDroppedZipPath(IDataObject dataObject, out string zipPath)
    {
        zipPath = string.Empty;
        if (!dataObject.GetDataPresent(DataFormats.FileDrop))
        {
            return false;
        }

        if (dataObject.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0)
        {
            return false;
        }

        var candidate = files.FirstOrDefault(path =>
            !path.IsNullOrEmpty()
            && string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)
            && File.Exists(path));

        if (candidate.IsNullOrEmpty())
        {
            return false;
        }

        zipPath = candidate;
        return true;
    }
}

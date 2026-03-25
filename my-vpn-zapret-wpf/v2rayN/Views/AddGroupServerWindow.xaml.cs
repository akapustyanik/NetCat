namespace v2rayN.Views;

public partial class AddGroupServerWindow
{
    private enum GroupEditorPage
    {
        Subscriptions,
        Children,
        Preview
    }

    private sealed class PolicyGroupFilterPreset
    {
        public required string Label { get; init; }
        public required string Pattern { get; init; }
    }

    public AddGroupServerWindow(ProfileItem profileItem)
    {
        InitializeComponent();

        Owner = Application.Current.MainWindow;
        Loaded += Window_Loaded;
        PreviewKeyDown += AddGroupServerWindow_PreviewKeyDown;
        lstChild.SelectionChanged += LstChild_SelectionChanged;
        menuSelectAllChild.Click += MenuSelectAllChild_Click;
        cmbFilter.SelectionChanged += CmbFilter_SelectionChanged;

        ViewModel = new AddGroupServerViewModel(profileItem, UpdateViewHandler);

        cmbCoreType.ItemsSource = Global.CoreTypes;
        cmbPolicyGroupType.ItemsSource = new List<string>
        {
            ResUI.TbLeastPing,
            ResUI.TbFallback,
            ResUI.TbRandom,
            ResUI.TbRoundRobin,
            ResUI.TbLeastLoad,
        };
        cmbFilter.ItemsSource = CreatePolicyGroupFilterPresets();

        switch (profileItem.ConfigType)
        {
            case EConfigType.PolicyGroup:
                Title = ResUI.TbConfigTypePolicyGroup;
                SetActivePage(GroupEditorPage.Subscriptions);
                break;

            case EConfigType.ProxyChain:
                Title = ResUI.TbConfigTypeProxyChain;
                gridPolicyGroup.Visibility = Visibility.Collapsed;
                btnSubscriptionsPage.Visibility = Visibility.Collapsed;
                subscriptionsPage.Visibility = Visibility.Collapsed;
                SetActivePage(GroupEditorPage.Children);
                break;
        }

        this.WhenActivated(disposables =>
        {
            this.Bind(ViewModel, vm => vm.SelectedSource.Remarks, v => v.txtRemarks.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.CoreType, v => v.cmbCoreType.Text).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.PolicyGroupType, v => v.cmbPolicyGroupType.Text).DisposeWith(disposables);
            this.OneWayBind(ViewModel, vm => vm.SubItems, v => v.cmbSubChildItems.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedSubItem, v => v.cmbSubChildItems.SelectedItem).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.Filter, v => v.cmbFilter.Text).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.ChildItemsObs, v => v.lstChild.ItemsSource).DisposeWith(disposables);
            this.Bind(ViewModel, vm => vm.SelectedChild, v => v.lstChild.SelectedItem).DisposeWith(disposables);

            this.OneWayBind(ViewModel, vm => vm.AllProfilePreviewItemsObs, v => v.lstPreviewChild.ItemsSource).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.RemoveCmd, v => v.menuRemoveChildServer).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveTopCmd, v => v.menuMoveTop).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveUpCmd, v => v.menuMoveUp).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveDownCmd, v => v.menuMoveDown).DisposeWith(disposables);
            this.BindCommand(ViewModel, vm => vm.MoveBottomCmd, v => v.menuMoveBottom).DisposeWith(disposables);

            this.BindCommand(ViewModel, vm => vm.SaveCmd, v => v.btnSave).DisposeWith(disposables);
        });
        WindowsUtils.SetDarkBorder(this, AppManager.Instance.Config.UiItem.CurrentTheme);
    }

    private async Task<bool> UpdateViewHandler(EViewAction action, object? obj)
    {
        switch (action)
        {
            case EViewAction.CloseWindow:
                DialogResult = true;
                break;
        }
        return await Task.FromResult(true);
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        txtRemarks.Focus();
    }

    private static List<PolicyGroupFilterPreset> CreatePolicyGroupFilterPresets()
    {
        return
        [
            new PolicyGroupFilterPreset
            {
                Label = "Все серверы",
                Pattern = Global.PolicyGroupDefaultAllFilter
            },
            new PolicyGroupFilterPreset
            {
                Label = "Низкий множитель",
                Pattern = @"^.*(?:[×xX✕*]\s*0\.[0-9]+|0\.[0-9]+\s*[×xX✕*倍]).*$"
            },
            new PolicyGroupFilterPreset
            {
                Label = "Выделенные линии",
                Pattern = $@"^(?!.*(?:{Global.PolicyGroupExcludeKeywords})).*(?:专线|IPLC|IEPL|中转).*$"
            },
            new PolicyGroupFilterPreset
            {
                Label = "Япония",
                Pattern = $@"^(?!.*(?:{Global.PolicyGroupExcludeKeywords})).*(?:日本|\b[Jj][Pp]\b|🇯🇵|[Jj]apan).*$"
            }
        ];
    }

    private void AddGroupServerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!lstChild.IsKeyboardFocusWithin)
        {
            return;
        }

        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
        {
            if (e.Key == Key.A)
            {
                lstChild.SelectAll();
            }
        }
        else
        {
            switch (e.Key)
            {
                case Key.T:
                    ViewModel?.MoveServer(EMove.Top);
                    break;

                case Key.U:
                    ViewModel?.MoveServer(EMove.Up);
                    break;

                case Key.D:
                    ViewModel?.MoveServer(EMove.Down);
                    break;

                case Key.B:
                    ViewModel?.MoveServer(EMove.Bottom);
                    break;

                case Key.Delete:
                case Key.Back:
                    ViewModel?.ChildRemoveAsync();
                    break;
            }
        }
    }

    private async void MenuAddChild_Click(object sender, RoutedEventArgs e)
    {
        var selectWindow = new ProfilesSelectWindow();
        selectWindow.SetConfigTypeFilter([EConfigType.Custom], exclude: true);
        selectWindow.AllowMultiSelect(true);
        if (selectWindow.ShowDialog() == true)
        {
            var profiles = await selectWindow.ProfileItems;
            ViewModel?.ChildItemsObs.AddRange(profiles);
        }
    }

    private void LstChild_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ViewModel != null)
        {
            ViewModel.SelectedChildren = lstChild.SelectedItems.Cast<ProfileItem>().ToList();
        }
    }

    private void MenuSelectAllChild_Click(object sender, RoutedEventArgs e)
    {
        lstChild.SelectAll();
    }

    private void CmbFilter_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (cmbFilter.SelectedItem is not PolicyGroupFilterPreset preset)
        {
            return;
        }

        cmbFilter.Text = preset.Pattern;
        cmbFilter.SelectedIndex = -1;
    }

    private void OnSubscriptionsPageClick(object sender, RoutedEventArgs e)
    {
        SetActivePage(GroupEditorPage.Subscriptions);
    }

    private void OnChildrenPageClick(object sender, RoutedEventArgs e)
    {
        SetActivePage(GroupEditorPage.Children);
    }

    private async void OnPreviewPageClick(object sender, RoutedEventArgs e)
    {
        SetActivePage(GroupEditorPage.Preview);
        if (ViewModel != null)
        {
            await ViewModel.UpdatePreviewList();
        }
    }

    private void SetActivePage(GroupEditorPage page)
    {
        subscriptionsPage.Visibility = page == GroupEditorPage.Subscriptions ? Visibility.Visible : Visibility.Collapsed;
        lstChild.Visibility = page == GroupEditorPage.Children ? Visibility.Visible : Visibility.Collapsed;
        lstPreviewChild.Visibility = page == GroupEditorPage.Preview ? Visibility.Visible : Visibility.Collapsed;

        btnSubscriptionsPage.Style = (Style)FindResource(page == GroupEditorPage.Subscriptions ? "DefButton" : "SubtleButton");
        btnChildrenPage.Style = (Style)FindResource(page == GroupEditorPage.Children ? "DefButton" : "SubtleButton");
        btnPreviewPage.Style = (Style)FindResource(page == GroupEditorPage.Preview ? "DefButton" : "SubtleButton");
    }
}

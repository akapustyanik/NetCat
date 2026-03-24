using System.ComponentModel;

namespace ServiceLib.ViewModels;

public class CheckUpdateViewModel : MyReactiveObject
{
    private const string _geo = "GeoFiles";
    private const string _zapret = "Zapret";
    private static readonly string[] _retryFailureMarkers =
    [
        "failed",
        "error",
        "exception",
        "cancelled",
        "canceled",
        "timeout",
        "timed out",
        "denied",
        "not found",
        "not exist",
        "missing",
        "does not contain",
        "abnormal",
        "statuscode error",
        "operation failed",
        "launch)",
        "не удалось",
        "ошиб",
        "отмен",
        "не найден",
        "не существует",
        "сбой",
    ];
    private readonly string _v2rayN = ECoreType.v2rayN.ToString();
    private List<CheckUpdateModel> _lstUpdated = [];
    private static readonly string _tag = "CheckUpdateViewModel";
    private bool _isCheckingUpdates;

    public IObservableCollection<CheckUpdateModel> CheckUpdateModels { get; } = new ObservableCollectionExtended<CheckUpdateModel>();
    public ReactiveCommand<Unit, Unit> CheckUpdateCmd { get; }
    public ReactiveCommand<CheckUpdateModel, Unit> RetryUpdateCmd { get; }
    public ReactiveCommand<CheckUpdateModel, Unit> InstallLocalPackageCmd { get; }
    public CheckUpdateViewModel(Func<EViewAction, object?, Task<bool>>? updateView)
    {
        _config = AppManager.Instance.Config;
        _updateView = updateView;

        CheckUpdateCmd = ReactiveCommand.CreateFromTask(CheckUpdate);
        CheckUpdateCmd.ThrownExceptions.Subscribe(ex =>
        {
            Logging.SaveLog(_tag, ex);
            _ = UpdateView(_v2rayN, ex.Message);
        });
        RetryUpdateCmd = ReactiveCommand.CreateFromTask<CheckUpdateModel>(RetryUpdateAsync);
        RetryUpdateCmd.ThrownExceptions.Subscribe(ex =>
        {
            Logging.SaveLog(_tag, ex);
        });
        InstallLocalPackageCmd = ReactiveCommand.CreateFromTask<CheckUpdateModel>(InstallLocalPackageAsync);
        InstallLocalPackageCmd.ThrownExceptions.Subscribe(ex =>
        {
            Logging.SaveLog(_tag, ex);
        });

        _config.CheckUpdateItem.CheckPreReleaseUpdate = false;

        RefreshCheckUpdateItems();
    }

    private void RefreshCheckUpdateItems()
    {
        CheckUpdateModels.Clear();

        if (RuntimeInformation.ProcessArchitecture != Architecture.X86)
        {
            CheckUpdateModels.Add(GetCheckUpdateModel(_v2rayN));
            //Not Windows and under Win10
            if (!(Utils.IsWindows() && Environment.OSVersion.Version.Major < 10))
            {
                CheckUpdateModels.Add(GetCheckUpdateModel(ECoreType.Xray.ToString()));
                CheckUpdateModels.Add(GetCheckUpdateModel(ECoreType.mihomo.ToString()));
                CheckUpdateModels.Add(GetCheckUpdateModel(ECoreType.sing_box.ToString()));
            }
        }
        CheckUpdateModels.Add(GetCheckUpdateModel(_zapret));
        CheckUpdateModels.Add(GetCheckUpdateModel(_geo));
    }

    private CheckUpdateModel GetCheckUpdateModel(string coreType)
    {
        if (coreType == _v2rayN && Utils.IsPackagedInstall())
        {
            return new()
            {
                IsSelected = false,
                CoreType = coreType,
                DisplayName = coreType == _v2rayN ? "NetCat" : coreType,
                CanUseLocalPackage = false,
                Hint = GetModuleHint(coreType),
                Remarks = "Update is not supported for packaged installs.",
            };
        }

        return new()
        {
            IsSelected = _config.CheckUpdateItem.SelectedCoreTypes?.Contains(coreType) ?? true,
            CoreType = coreType,
            DisplayName = coreType == _v2rayN ? "NetCat" : coreType,
            CanUseLocalPackage = coreType == _v2rayN,
            Hint = GetModuleHint(coreType),
            Remarks = "Ready to check for updates.",
        };
    }

    private async Task SaveSelectedCoreTypes()
    {
        _config.CheckUpdateItem.SelectedCoreTypes = CheckUpdateModels.Where(t => t.IsSelected == true).Select(t => t.CoreType ?? "").ToList();
        await ConfigHandler.SaveConfig(_config);
    }

    private async Task CheckUpdate()
    {
        await Task.Run(CheckUpdateTask);
    }

    private async Task CheckUpdateTask()
    {
        _isCheckingUpdates = true;
        try
        {
            _lstUpdated.Clear();
            await SaveSelectedCoreTypes();

            for (var k = CheckUpdateModels.Count - 1; k >= 0; k--)
            {
                var item = CheckUpdateModels[k];
                if (item.IsSelected != true)
                {
                    continue;
                }

                await RunUpdateForItemAsync(item);
            }

            await UpdateFinished();
        }
        finally
        {
            _isCheckingUpdates = false;
        }
    }

    private void UpdatedPlusPlus(string coreType, string fileName)
    {
        var item = _lstUpdated.FirstOrDefault(x => x.CoreType == coreType);
        if (item == null)
        {
            item = new CheckUpdateModel() { CoreType = coreType };
            _lstUpdated.Add(item);
        }
        item.IsFinished = true;
        if (!fileName.IsNullOrEmpty())
        {
            item.FileName = fileName;
        }
    }

    private async Task CheckUpdateGeo()
    {
        async Task _updateUI(bool success, string msg)
        {
            await UpdateView(_geo, msg);
            if (success)
            {
                UpdatedPlusPlus(_geo, "");
            }
        }
        await new UpdateService(_config, _updateUI).UpdateGeoFileAll();
    }

    private async Task CheckUpdateZapret()
    {
        async Task _updateUI(bool success, string msg)
        {
            await UpdateView(_zapret, msg);
            if (success)
            {
                UpdatedPlusPlus(_zapret, "");
            }
        }

        await new UpdateService(_config, _updateUI).CheckUpdateZapret();
    }

    private async Task CheckUpdateN(bool preRelease)
    {
        async Task _updateUI(bool success, string msg)
        {
            await UpdateView(_v2rayN, msg);
            if (success)
            {
                await UpdateView(_v2rayN, ResUI.OperationSuccess);
                UpdatedPlusPlus(_v2rayN, msg);
            }
        }
        await new UpdateService(_config, _updateUI).CheckUpdateGuiN(preRelease);
    }

    private async Task CheckUpdateCore(CheckUpdateModel model, bool preRelease)
    {
        async Task _updateUI(bool success, string msg)
        {
            await UpdateView(model.CoreType, msg);
            if (success)
            {
                await UpdateView(model.CoreType, ResUI.MsgUpdateV2rayCoreSuccessfullyMore);

                UpdatedPlusPlus(model.CoreType, msg);
            }
        }
        var type = (ECoreType)Enum.Parse(typeof(ECoreType), model.CoreType);
        await new UpdateService(_config, _updateUI).CheckUpdateCore(type, preRelease);
    }

    private async Task RetryUpdateAsync(CheckUpdateModel? item)
    {
        if (item?.CoreType.IsNullOrEmpty() != false || _isCheckingUpdates)
        {
            return;
        }

        _isCheckingUpdates = true;
        item.IsRetrying = true;
        item.CanRetry = false;
        _lstUpdated.Clear();
        try
        {
            await RunUpdateForItemAsync(item);
            await UpdateFinished();
        }
        finally
        {
            item.IsRetrying = false;
            item.CanRetry = ShouldAllowRetry(item.Remarks);
            _isCheckingUpdates = false;
        }
    }

    private async Task InstallLocalPackageAsync(CheckUpdateModel? item)
    {
        if (item?.CoreType != _v2rayN || _isCheckingUpdates)
        {
            return;
        }

        var selectedPath = new string[1];
        if (await _updateView(EViewAction.SelectLocalUpdatePackage, selectedPath) != true || selectedPath[0].IsNullOrEmpty())
        {
            return;
        }
        var fileName = selectedPath[0];

        _isCheckingUpdates = true;
        item.IsRetrying = true;
        item.CanRetry = false;
        try
        {
            await UpdateView(_v2rayN, "Preparing local update package...");

            var service = new UpdateService(_config, async (_, msg) => await UpdateView(_v2rayN, msg));
            var result = service.PrepareLocalGuiUpdateArchive(fileName);
            if (!result.Success || result.LocalArchivePath.IsNullOrEmpty())
            {
                await UpdateView(_v2rayN, result.Msg ?? "Failed to prepare local update package.");
                item.CanRetry = ShouldAllowRetry(result.Msg);
                return;
            }

            UpdatedPlusPlus(_v2rayN, result.LocalArchivePath);
            await UpdateView(_v2rayN, result.Msg ?? "Local update package is ready.");
            await UpgradeN(result.LocalArchivePath);
        }
        finally
        {
            item.IsRetrying = false;
            item.CanRetry = ShouldAllowRetry(item.Remarks);
            _isCheckingUpdates = false;
        }
    }

    private async Task RunUpdateForItemAsync(CheckUpdateModel item)
    {
        item.CanRetry = false;
        await UpdateView(item.CoreType, "...");
        if (item.CoreType == _geo)
        {
            await CheckUpdateGeo();
        }
        else if (item.CoreType == _zapret)
        {
            await CheckUpdateZapret();
        }
        else if (item.CoreType == _v2rayN)
        {
            if (Utils.IsPackagedInstall())
            {
                await UpdateView(_v2rayN, "Not Support");
                return;
            }
            await CheckUpdateN(false);
        }
        else if (item.CoreType == ECoreType.Xray.ToString())
        {
            await CheckUpdateCore(item, false);
        }
        else
        {
            await CheckUpdateCore(item, false);
        }
    }

    private async Task UpdateFinished()
    {
        if (_lstUpdated.Count > 0 && _lstUpdated.Count(x => x.IsFinished == true) == _lstUpdated.Count)
        {
            var requiresCoreReload = _lstUpdated.Any(x => x.CoreType != _v2rayN && x.CoreType != _zapret);
            if (requiresCoreReload)
            {
                await UpdateFinishedSub(false);
                await Task.Delay(2000);
                await UpgradeCore();
            }

            if (_lstUpdated.Any(x => x.CoreType == _v2rayN && x.IsFinished == true))
            {
                await Task.Delay(1000);
                await UpgradeN();
            }
            else if (requiresCoreReload)
            {
                await Task.Delay(1000);
                await UpdateFinishedSub(true);
            }
        }
    }

    private async Task UpdateFinishedSub(bool blReload)
    {
        RxSchedulers.MainThreadScheduler.Schedule(blReload, (scheduler, blReload) =>
        {
            _ = UpdateFinishedResult(blReload);
            return Disposable.Empty;
        });
        await Task.CompletedTask;
    }

    public async Task UpdateFinishedResult(bool blReload)
    {
        if (blReload)
        {
            AppEvents.ReloadRequested.Publish();
        }
        else
        {
            await CoreManager.Instance.CoreStop();
        }
    }

    private async Task UpgradeN(string? packageOverridePath = null)
    {
        string? updateLauncherPath = null;
        string? updatePackagePath = null;
        string? sourceUpgradeAssemblyPath = null;
        string? sourceUpgradeFileName = null;
        string? stagedUpgradeAssemblyPath = null;
        string? stagedUpgradeFileName = null;
        try
        {
            updatePackagePath = packageOverridePath ?? _lstUpdated.FirstOrDefault(x => x.CoreType == _v2rayN)?.FileName;
            if (updatePackagePath.IsNullOrEmpty())
            {
                return;
            }
            if (!Utils.UpgradeAppExists(out var upgradeFileName))
            {
                await UpdateView(_v2rayN, $"Updater is missing. {ResUI.UpgradeAppNotExistTip}");
                NoticeManager.Instance.SendMessageAndEnqueue(ResUI.UpgradeAppNotExistTip);
                Logging.SaveLog("UpgradeApp does not exist");
                return;
            }

            sourceUpgradeFileName = upgradeFileName;
            stagedUpgradeFileName = StageUpgradeApp(sourceUpgradeFileName);
            stagedUpgradeAssemblyPath = Path.Combine(Path.GetDirectoryName(stagedUpgradeFileName) ?? Utils.GetTempPath(), "AmazTool.dll");
            if (Utils.TryGetManagedUpgradeHost(out var dotnetHostPath, out var upgradeAssemblyPath) && File.Exists(stagedUpgradeAssemblyPath))
            {
                updateLauncherPath = dotnetHostPath;
                sourceUpgradeAssemblyPath = upgradeAssemblyPath;
            }
            else
            {
                updateLauncherPath = stagedUpgradeFileName;
            }

            Logging.SaveLog(BuildUpdaterLaunchDiagnostics(sourceUpgradeFileName, sourceUpgradeAssemblyPath, stagedUpgradeFileName, stagedUpgradeAssemblyPath, updateLauncherPath, updatePackagePath));
            Process proc = new()
            {
                StartInfo = new ProcessStartInfo
                {
                    UseShellExecute = string.Equals(updateLauncherPath, stagedUpgradeFileName, StringComparison.OrdinalIgnoreCase),
                    FileName = updateLauncherPath,
                    WorkingDirectory = Path.GetDirectoryName(stagedUpgradeFileName) ?? Utils.GetTempPath(),
                }
            };
            if (string.Equals(updateLauncherPath, stagedUpgradeFileName, StringComparison.OrdinalIgnoreCase))
            {
                proc.StartInfo.ArgumentList.Add("upgrade");
                proc.StartInfo.ArgumentList.Add(Utils.StartupPath());
                proc.StartInfo.ArgumentList.Add(updatePackagePath);
            }
            else
            {
                proc.StartInfo.ArgumentList.Add(stagedUpgradeAssemblyPath);
                proc.StartInfo.ArgumentList.Add("upgrade");
                proc.StartInfo.ArgumentList.Add(Utils.StartupPath());
                proc.StartInfo.ArgumentList.Add(updatePackagePath);
            }

            if (proc.Start())
            {
                await AppManager.Instance.AppExitAsync(true);
            }
            else
            {
                CleanupStagedUpdater(stagedUpgradeFileName);
                await UpdateView(_v2rayN, "Failed to launch updater. Try again or use a local .zip package.");
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            CleanupStagedUpdater(stagedUpgradeFileName);
            await UpdateView(_v2rayN, "Updater launch was cancelled or blocked by Windows protection. You can retry or install from a local .zip package.");
            Logging.SaveLog(BuildUpdaterLaunchDiagnostics(sourceUpgradeFileName, sourceUpgradeAssemblyPath, stagedUpgradeFileName, stagedUpgradeAssemblyPath, updateLauncherPath, updatePackagePath));
            Logging.SaveLog(_tag, ex);
        }
        catch (Exception ex)
        {
            CleanupStagedUpdater(stagedUpgradeFileName);
            await UpdateView(_v2rayN, $"Failed to launch updater. Details: {ex.Message}");
            Logging.SaveLog(BuildUpdaterLaunchDiagnostics(sourceUpgradeFileName, sourceUpgradeAssemblyPath, stagedUpgradeFileName, stagedUpgradeAssemblyPath, updateLauncherPath, updatePackagePath));
            Logging.SaveLog(_tag, ex);
        }
    }

    private static string StageUpgradeApp(string upgradeFileName)
    {
        CleanupStaleStagedUpdaters();

        var sourceDir = Path.GetDirectoryName(upgradeFileName) ?? Utils.GetBaseDirectory();
        var targetDir = Utils.GetTempPath($"updater-{Utils.GetGuid()}");
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDir, file);
            var targetPath = Path.Combine(targetDir, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? targetDir);
            FileUtils.CopyFileWithRetry(file, targetPath, true);
        }

        var unblockResult = FileUtils.TryUnblockDirectoryFiles(targetDir);

        var stagedUpgradeFileName = Path.Combine(targetDir, Path.GetFileName(upgradeFileName));
        if (!File.Exists(stagedUpgradeFileName))
        {
            throw new FileNotFoundException("Failed to stage updater files.", stagedUpgradeFileName);
        }

        Logging.SaveLog($"StageUpgradeApp prepared updater | sourceDir={sourceDir} | targetDir={targetDir} | scanned={unblockResult.ScannedFiles} | blocked={unblockResult.BlockedFiles} | unblocked={unblockResult.UnblockedFiles}");
        Logging.SaveLog(BuildUpdaterLaunchDiagnostics(upgradeFileName, Path.Combine(sourceDir, "AmazTool.dll"), stagedUpgradeFileName, Path.Combine(targetDir, "AmazTool.dll"), null, null));

        return stagedUpgradeFileName;
    }

    private static string BuildUpdaterLaunchDiagnostics(string? sourceUpdaterPath, string? sourceUpdaterAssemblyPath, string? stagedUpdaterPath, string? stagedUpdaterAssemblyPath, string? launcherPath, string? packagePath)
    {
        return string.Join(" | ", [
            "Updater launch diagnostics",
            $"sourceExe={FileUtils.DescribeWindowsTrustState(sourceUpdaterPath)}",
            $"sourceDll={FileUtils.DescribeWindowsTrustState(sourceUpdaterAssemblyPath)}",
            $"stagedExe={FileUtils.DescribeWindowsTrustState(stagedUpdaterPath)}",
            $"stagedDll={FileUtils.DescribeWindowsTrustState(stagedUpdaterAssemblyPath)}",
            $"launcher={FileUtils.DescribeWindowsTrustState(launcherPath)}",
            $"package={FileUtils.DescribeWindowsTrustState(packagePath)}"
        ]);
    }

    private static void CleanupStaleStagedUpdaters()
    {
        try
        {
            foreach (var directoryPath in Directory.GetDirectories(Utils.GetTempPath(), "updater-*", SearchOption.TopDirectoryOnly))
            {
                FileUtils.TryDeleteDirectory(directoryPath);
            }
        }
        catch
        {
            // ignore temp cleanup failures
        }
    }

    private static void CleanupStagedUpdater(string? stagedUpgradeFileName)
    {
        try
        {
            var stagedDirectory = Path.GetDirectoryName(stagedUpgradeFileName ?? string.Empty);
            if (stagedDirectory.IsNullOrEmpty() || !Directory.Exists(stagedDirectory))
            {
                return;
            }

            FileUtils.TryDeleteDirectory(stagedDirectory);
        }
        catch
        {
            // ignore temp cleanup failures
        }
    }

    private async Task UpgradeCore()
    {
        foreach (var item in _lstUpdated)
        {
            if (item.FileName.IsNullOrEmpty())
            {
                continue;
            }

            var fileName = item.FileName;
            if (!File.Exists(fileName))
            {
                continue;
            }
            var toPath = Utils.GetBinPath("", item.CoreType);

            if (fileName.Contains(".tar.gz"))
            {
                FileUtils.DecompressTarFile(fileName, toPath);
                var dir = new DirectoryInfo(toPath);
                if (dir.Exists)
                {
                    foreach (var subDir in dir.GetDirectories())
                    {
                        FileUtils.CopyDirectory(subDir.FullName, toPath, false, true);
                        subDir.Delete(true);
                    }
                }
            }
            else if (fileName.Contains(".gz"))
            {
                FileUtils.DecompressFile(fileName, toPath, item.CoreType);
            }
            else
            {
                FileUtils.ZipExtractToFile(fileName, toPath, "geo");
            }

            if (Utils.IsNonWindows())
            {
                var filesList = new DirectoryInfo(toPath).GetFiles().Select(u => u.FullName).ToList();
                foreach (var file in filesList)
                {
                    await Utils.SetLinuxChmod(Path.Combine(toPath, item.CoreType.ToLower()));
                }
            }

            await UpdateView(item.CoreType, ResUI.MsgUpdateV2rayCoreSuccessfully);

            if (File.Exists(fileName))
            {
                FileUtils.TryDeleteFile(fileName);
            }
        }
    }

    private async Task UpdateView(string coreType, string msg)
    {
        var item = new CheckUpdateModel()
        {
            CoreType = coreType,
            Remarks = msg,
        };

        RxSchedulers.MainThreadScheduler.Schedule(item, (scheduler, model) =>
        {
            _ = UpdateViewResult(model);
            return Disposable.Empty;
        });
        await Task.CompletedTask;
    }

    public async Task UpdateViewResult(CheckUpdateModel model)
    {
        var found = CheckUpdateModels.FirstOrDefault(t => t.CoreType == model.CoreType);
        if (found == null)
        {
            return;
        }
        found.Remarks = model.Remarks;
        found.CanRetry = !found.IsRetrying && ShouldAllowRetry(model.Remarks);
        await Task.CompletedTask;
    }

    private static bool ShouldAllowRetry(string? msg)
    {
        if (msg.IsNullOrEmpty())
        {
            return false;
        }

        var normalized = msg.Trim().ToLowerInvariant();
        if (normalized is "" or "..." or "not support")
        {
            return false;
        }

        return _retryFailureMarkers.Any(marker => normalized.Contains(marker, StringComparison.Ordinal));
    }

    private static string GetModuleHint(string coreType)
    {
        return coreType switch
        {
            "GeoFiles" => GetResourceText("UpdateHintGeoFiles", "Geo databases and sing-box rule sets used by routing and DNS."),
            "Zapret" => GetResourceText("UpdateHintZapret", "Zapret bundle with winws, service.bat and bundled DPI bypass presets."),
            "Xray" => GetResourceText("UpdateHintXray", "Xray core used for proxy protocols and connections."),
            "mihomo" => GetResourceText("UpdateHintMihomo", "Mihomo core used for Clash-compatible profiles and rule processing."),
            "sing_box" => GetResourceText("UpdateHintSingBox", "sing-box core used for sing-box profiles, DNS and rule sets."),
            _ => GetResourceText("UpdateHintNetCat", "Main application update from GitHub Releases. If GitHub is unavailable, you can install from a local .zip package.")
        };
    }

    private static string GetResourceText(string resourceKey, string fallback)
    {
        return ResUI.ResourceManager.GetString(resourceKey, ResUI.Culture) ?? fallback;
    }
}

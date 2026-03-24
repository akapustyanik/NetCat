namespace ServiceLib.Services;

public class UpdateService(Config config, Func<bool, string, Task> updateFunc)
{
    private const string _zapretModuleName = "Zapret";
    private const string _zapretRepository = "Flowseal/zapret-discord-youtube";
    private const string _zapretReleaseApiUrl = $"{Global.GithubApiUrl}/{_zapretRepository}/releases";
    private sealed class GeoFileCacheMetadata
    {
        public string? SourceUrl { get; set; }
        public string? FinalUrl { get; set; }
        public long? ContentLength { get; set; }
        public string? ETag { get; set; }
        public DateTimeOffset? LastModified { get; set; }
    }

    private static readonly string[] _zipExtensions = [".zip"];
    private readonly Config? _config = config;
    private readonly Func<bool, string, Task>? _updateFunc = updateFunc;
    private readonly int _timeout = 30;
    private static readonly string _tag = "UpdateService";

    public async Task<UpdateResult> CheckGuiUpdateAvailability(bool preRelease)
    {
        try
        {
            var downloadHandle = new DownloadService();
            return await CheckUpdateAsync(downloadHandle, ECoreType.v2rayN, preRelease);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            return new UpdateResult(false, ex.Message)
            {
                Status = EUpdateAvailabilityStatus.Failed,
                FailureStage = EUpdateFailureStage.Check
            };
        }
    }

    public async Task CheckUpdateGuiN(bool preRelease)
    {
        var url = string.Empty;
        var fileName = string.Empty;

        DownloadService downloadHandle = new();
        downloadHandle.UpdateCompleted += (sender2, args) =>
        {
            if (args.Success)
            {
                FileUtils.TryUnblockFile(fileName);
                _ = UpdateFunc(false, ResUI.MsgDownloadV2rayCoreSuccessfully);
                _ = UpdateFunc(true, Utils.UrlEncode(fileName));
            }
            else
            {
                _ = UpdateFunc(false, args.Msg);
            }
        };
        downloadHandle.Error += (sender2, args) =>
        {
            _ = UpdateFunc(false, args.GetException().Message);
        };

        await UpdateFunc(false, string.Format(ResUI.MsgStartUpdating, ECoreType.v2rayN));
        var result = await CheckUpdateAsync(downloadHandle, ECoreType.v2rayN, preRelease);
        if (result.Success)
        {
            await UpdateFunc(false, string.Format(ResUI.MsgParsingSuccessfully, ECoreType.v2rayN));
            await UpdateFunc(false, result.Msg);

            url = result.Url.ToString();
            fileName = GetGuiUpdateArchivePath(result);
            result.LocalArchivePath = fileName;
            CleanupStaleGuiUpdateArtifacts(fileName);
            if (TryUseCachedGuiUpdateArchive(fileName, result.Asset))
            {
                FileUtils.TryUnblockFile(fileName);
                result.UsedCachedArchive = true;
                await UpdateFunc(false, "Using previously downloaded update package.");
                await UpdateFunc(false, ResUI.MsgDownloadV2rayCoreSuccessfully);
                await UpdateFunc(true, Utils.UrlEncode(fileName));
                return;
            }

            await downloadHandle.DownloadFileAsync(url, fileName, true, _timeout);
        }
        else
        {
            await UpdateFunc(false, FormatUpdateStatusMessage(result));
        }
    }

    public async Task CheckUpdateCore(ECoreType type, bool preRelease)
    {
        var url = string.Empty;
        var fileName = string.Empty;

        DownloadService downloadHandle = new();
        downloadHandle.UpdateCompleted += (sender2, args) =>
        {
            if (args.Success)
            {
                FileUtils.TryUnblockFile(fileName);
                _ = UpdateFunc(false, ResUI.MsgDownloadV2rayCoreSuccessfully);
                _ = UpdateFunc(false, ResUI.MsgUnpacking);

                try
                {
                    _ = UpdateFunc(true, fileName);
                }
                catch (Exception ex)
                {
                    _ = UpdateFunc(false, ex.Message);
                }
            }
            else
            {
                _ = UpdateFunc(false, args.Msg);
            }
        };
        downloadHandle.Error += (sender2, args) =>
        {
            _ = UpdateFunc(false, args.GetException().Message);
        };

        await UpdateFunc(false, string.Format(ResUI.MsgStartUpdating, type));
        var result = await CheckUpdateAsync(downloadHandle, type, preRelease);
        if (result.Success)
        {
            await UpdateFunc(false, string.Format(ResUI.MsgParsingSuccessfully, type));
            await UpdateFunc(false, result.Msg);

            url = result.Url.ToString();
            var ext = url.Contains(".tar.gz") ? ".tar.gz" : Path.GetExtension(url);
            fileName = Utils.GetTempPath(Utils.GetGuid() + ext);
            await downloadHandle.DownloadFileAsync(url, fileName, true, _timeout);
        }
        else
        {
            if (!result.Msg.IsNullOrEmpty())
            {
                await UpdateFunc(false, result.Msg);
            }
        }
    }

    public async Task CheckUpdateZapret()
    {
        var archivePath = Utils.GetTempPath($"{Utils.GetGuid()}.zip");
        var extractPath = Utils.GetTempPath($"zapret-update-{Utils.GetGuid()}");
        string? preservePath = null;

        try
        {
            await UpdateFunc(false, string.Format(ResUI.MsgStartUpdating, _zapretModuleName));

            var downloadHandle = new DownloadService();
            var release = await GetGitHubRelease(downloadHandle, _zapretReleaseApiUrl, false);
            if (release == null)
            {
                await UpdateFunc(false, "Failed to resolve Zapret release information.");
                return;
            }

            var asset = GetPreferredZapretAsset(release);
            if (asset?.BrowserDownloadUrl.IsNullOrEmpty() != false)
            {
                await UpdateFunc(false, "GitHub release does not contain a compatible Zapret .zip asset.");
                return;
            }

            var targetPath = GetZapretInstallPath();
            var currentVersion = GetZapretLocalVersion(targetPath);
            var latestVersion = NormalizeZapretVersion(release.TagName)
                ?? NormalizeZapretVersion(release.Name)
                ?? NormalizeZapretVersion(asset.Name);

            if (currentVersion.IsNotEmpty()
                && latestVersion.IsNotEmpty()
                && string.Equals(currentVersion, latestVersion, StringComparison.OrdinalIgnoreCase))
            {
                var latestTemplate = GetResourceText("MsgZapretAlreadyUpToDate", "Zapret {0} is already up to date.");
                await UpdateFunc(false, string.Format(latestTemplate, currentVersion));
                return;
            }

            await UpdateFunc(false, string.Format(ResUI.MsgParsingSuccessfully, _zapretModuleName));
            await UpdateFunc(false, $"Update available: {latestVersion ?? release.TagName ?? asset.Name ?? "latest"}");
            await downloadHandle.DownloadFileAsync(asset.BrowserDownloadUrl!, archivePath, true, _timeout);
            if (!File.Exists(archivePath))
            {
                await UpdateFunc(false, "Failed to download Zapret update archive.");
                return;
            }

            FileUtils.TryUnblockFile(archivePath);

            await UpdateFunc(false, ResUI.MsgUnpacking);

            var wasRunning = ZapretHandler.IsRunning();
            var preferredConfig = _config?.GuiItem.LastZapretConfig;
            if (wasRunning)
            {
                ZapretHandler.Stop();
                await WaitForZapretStopAsync();
            }

            Directory.CreateDirectory(extractPath);
            System.IO.Compression.ZipFile.ExtractToDirectory(archivePath, extractPath, true);
            FileUtils.TryUnblockDirectoryFiles(extractPath);

            var extractedZapretPath = FindZapretExtractRoot(extractPath);
            if (extractedZapretPath.IsNullOrEmpty())
            {
                throw new DirectoryNotFoundException("Zapret archive does not contain a valid bundle.");
            }

            preservePath = BackupZapretUserFiles(targetPath);

            Directory.CreateDirectory(targetPath);
            DeleteDirectoryContents(targetPath);
            FileUtils.CopyDirectory(extractedZapretPath, targetPath, true, true);
            RestoreZapretUserFiles(preservePath, targetPath);

            var successTemplate = GetResourceText("MsgUpdateZapretSuccessfully", "Updated Zapret successfully ({0}).");
            var successVersion = latestVersion ?? release.TagName ?? asset.Name ?? "latest";
            await UpdateFunc(true, string.Format(successTemplate, successVersion));

            if (wasRunning)
            {
                var startConfig = ResolveZapretStartConfig(targetPath, preferredConfig);
                if (startConfig.IsNotEmpty() && !ZapretHandler.Start(targetPath, startConfig, out var error))
                {
                    await UpdateFunc(false, error);
                }
            }

            AppEvents.ReloadRequested.Publish();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(false, ex.Message);
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(extractPath);
            TryDeleteDirectory(preservePath);
        }
    }

    public async Task<bool> UpdateGeoFileAll()
    {
        var hasChanges = false;
        hasChanges |= await UpdateGeoFiles();
        hasChanges |= await UpdateOtherFiles();
        hasChanges |= await UpdateSrsFileAll();

        var message = hasChanges
            ? string.Format(ResUI.MsgDownloadGeoFileSuccessfully, "geo")
            : GetResourceText("MsgGeoFilesAlreadyUpToDate", "GeoFiles are already up to date.");
        await UpdateFunc(hasChanges, message);
        return hasChanges;
    }

    #region CheckUpdate private

    private async Task<UpdateResult> CheckUpdateAsync(DownloadService downloadHandle, ECoreType type, bool preRelease)
    {
        try
        {
            var result = await GetRemoteVersion(downloadHandle, type, preRelease);
            if (!result.Success || result.Version is null)
            {
                return result;
            }
            return await ParseDownloadUrl(type, result);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(false, ex.Message);
            return new UpdateResult(false, ex.Message)
            {
                Status = EUpdateAvailabilityStatus.Failed,
                FailureStage = EUpdateFailureStage.Check
            };
        }
    }

    private async Task<UpdateResult> GetRemoteVersion(DownloadService downloadHandle, ECoreType type, bool preRelease)
    {
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(type);
        var tagName = string.Empty;
        GitHubRelease? gitHubRelease = null;
        if (type == ECoreType.v2rayN)
        {
            gitHubRelease = await GetGitHubRelease(downloadHandle, coreInfo, preRelease);
            if (gitHubRelease == null)
            {
                return new UpdateResult(false, "Failed to fetch NetCat release metadata.")
                {
                    Status = EUpdateAvailabilityStatus.Failed,
                    FailureStage = EUpdateFailureStage.ReleaseLookup
                };
            }

            tagName = gitHubRelease.TagName;
            return new UpdateResult(true, new SemanticVersion(tagName))
            {
                Release = gitHubRelease,
                Status = EUpdateAvailabilityStatus.Available
            };
        }

        if (preRelease)
        {
            var url = coreInfo?.ReleaseApiUrl;
            var result = await downloadHandle.TryDownloadString(url, true, Global.AppName);
            if (result.IsNullOrEmpty())
            {
                return new UpdateResult(false, "Failed to fetch release list.")
                {
                    Status = EUpdateAvailabilityStatus.Failed,
                    FailureStage = EUpdateFailureStage.ReleaseLookup
                };
            }

            var gitHubReleases = JsonUtils.Deserialize<List<GitHubRelease>>(result);
            var selectedRelease = preRelease ? gitHubReleases?.First() : gitHubReleases?.First(r => r.Prerelease == false);
            tagName = selectedRelease?.TagName;
            //var body = gitHubRelease?.Body;
        }
        else
        {
            var url = Path.Combine(coreInfo.Url, "latest");
            var lastUrl = await downloadHandle.UrlRedirectAsync(url, true);
            if (lastUrl == null)
            {
                return new UpdateResult(false, "Failed to resolve latest release redirect.")
                {
                    Status = EUpdateAvailabilityStatus.Failed,
                    FailureStage = EUpdateFailureStage.ReleaseLookup
                };
            }

            tagName = lastUrl?.Split("/tag/").LastOrDefault();
        }
        return new UpdateResult(true, new SemanticVersion(tagName))
        {
            Status = EUpdateAvailabilityStatus.Available
        };
    }

    private async Task<SemanticVersion> GetCoreVersion(ECoreType type)
    {
        try
        {
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(type);
            var filePath = string.Empty;
            foreach (var name in coreInfo.CoreExes)
            {
                var vName = Utils.GetBinPath(Utils.GetExeName(name), coreInfo.CoreType.ToString());
                if (File.Exists(vName))
                {
                    filePath = vName;
                    break;
                }
            }

            if (!File.Exists(filePath))
            {
                var msg = string.Format(ResUI.NotFoundCore, @"", "", "");
                //ShowMsg(true, msg);
                return new SemanticVersion("");
            }

            var result = await Utils.GetCliWrapOutput(filePath, coreInfo.VersionArg);
            var echo = result ?? "";
            var version = string.Empty;
            switch (type)
            {
                case ECoreType.v2fly:
                case ECoreType.Xray:
                case ECoreType.v2fly_v5:
                    version = Regex.Match(echo, $"{coreInfo.Match} ([0-9.]+) \\(").Groups[1].Value;
                    break;

                case ECoreType.mihomo:
                    version = Regex.Match(echo, $"v[0-9.]+").Groups[0].Value;
                    break;

                case ECoreType.sing_box:
                    version = Regex.Match(echo, $"([0-9.]+)").Groups[1].Value;
                    break;
            }
            return new SemanticVersion(version);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(false, ex.Message);
            return new SemanticVersion("");
        }
    }

    private async Task<UpdateResult> ParseDownloadUrl(ECoreType type, UpdateResult result)
    {
        try
        {
            var version = result.Version ?? new SemanticVersion(0, 0, 0);
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo(type);
            var coreUrl = await GetUrlFromCore(coreInfo) ?? string.Empty;
            SemanticVersion curVersion;
            string message;
            string? url;
            switch (type)
            {
                case ECoreType.v2fly:
                case ECoreType.Xray:
                case ECoreType.v2fly_v5:
                    {
                        curVersion = await GetCoreVersion(type);
                        message = string.Format(ResUI.IsLatestCore, type, curVersion.ToVersionString("v"));
                        url = string.Format(coreUrl, version.ToVersionString("v"));
                        break;
                    }
                case ECoreType.mihomo:
                    {
                        curVersion = await GetCoreVersion(type);
                        message = string.Format(ResUI.IsLatestCore, type, curVersion);
                        url = string.Format(coreUrl, version.ToVersionString("v"));
                        break;
                    }
                case ECoreType.sing_box:
                    {
                        curVersion = await GetCoreVersion(type);
                        message = string.Format(ResUI.IsLatestCore, type, curVersion.ToVersionString("v"));
                        url = string.Format(coreUrl, version.ToVersionString("v"), version);
                        break;
                    }
                case ECoreType.v2rayN:
                    {
                        curVersion = new SemanticVersion(Utils.GetVersionInfo());
                        message = string.Format(ResUI.IsLatestN, Global.AppName, curVersion);
                        var asset = GetPreferredGuiAsset(result.Release);
                        url = asset?.BrowserDownloadUrl;
                        if (url.IsNullOrEmpty())
                        {
                            return new UpdateResult(false, "GitHub release does not contain a compatible .zip asset for NetCat.")
                            {
                                Status = EUpdateAvailabilityStatus.Failed,
                                FailureStage = EUpdateFailureStage.AssetSelection
                            };
                        }
                        result.Asset = asset;
                        break;
                    }
                default:
                    throw new ArgumentException("Type");
            }

            if (curVersion >= version && version != new SemanticVersion(0, 0, 0))
            {
                return new UpdateResult(false, message)
                {
                    Status = EUpdateAvailabilityStatus.UpToDate,
                    FailureStage = EUpdateFailureStage.None,
                    Release = result.Release,
                    Asset = result.Asset,
                    Version = result.Version,
                    Url = result.Url
                };
            }

            result.Msg = type == ECoreType.v2rayN
                ? $"Update available: {version}"
                : result.Msg;
            result.Url = url;
            result.Status = EUpdateAvailabilityStatus.Available;
            return result;
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(false, ex.Message);
            return new UpdateResult(false, ex.Message)
            {
                Status = EUpdateAvailabilityStatus.Failed,
                FailureStage = EUpdateFailureStage.ReleaseParsing
            };
        }
    }

    private async Task<GitHubRelease?> GetGitHubRelease(DownloadService downloadHandle, CoreInfo? coreInfo, bool preRelease)
    {
        var releaseApiUrl = coreInfo?.ReleaseApiUrl;
        if (releaseApiUrl.IsNullOrEmpty())
        {
            return null;
        }

        return await GetGitHubRelease(downloadHandle, releaseApiUrl, preRelease);
    }

    private async Task<GitHubRelease?> GetGitHubRelease(DownloadService downloadHandle, string releaseApiUrl, bool preRelease)
    {
        if (preRelease)
        {
            var result = await downloadHandle.TryDownloadString(releaseApiUrl, true, Global.AppName);
            if (result.IsNullOrEmpty())
            {
                return null;
            }

            var gitHubReleases = JsonUtils.Deserialize<List<GitHubRelease>>(result);
            return gitHubReleases?
                .OrderByDescending(r => r.PublishedAt)
                .FirstOrDefault();
        }

        var latestReleaseUrl = $"{releaseApiUrl}/latest";
        var latestResult = await downloadHandle.TryDownloadString(latestReleaseUrl, true, Global.AppName);
        if (latestResult.IsNullOrEmpty())
        {
            return null;
        }

        return JsonUtils.Deserialize<GitHubRelease>(latestResult);
    }

    private string? GetGuiUpdateAssetUrl(GitHubRelease? release)
    {
        var asset = GetPreferredGuiAsset(release);
        return asset?.BrowserDownloadUrl;
    }

    private string GetGuiUpdateArchivePath(UpdateResult result)
    {
        var cacheDirectory = Path.Combine(Utils.GetTempPath(), "updates");
        Directory.CreateDirectory(cacheDirectory);

        var releaseTag = SanitizeFileName(result.Release?.TagName ?? result.Version?.ToString() ?? "latest");
        var assetName = result.Asset?.Name;
        if (assetName.IsNullOrEmpty())
        {
            var fallbackHash = Utils.GetMd5(result.Url ?? releaseTag);
            assetName = $"NetCat-{fallbackHash}.zip";
        }

        return Path.Combine(cacheDirectory, $"{releaseTag}-{SanitizeFileName(assetName)}");
    }

    private void CleanupStaleGuiUpdateArtifacts(string keepArchivePath)
    {
        try
        {
            var normalizedKeepArchivePath = Path.GetFullPath(keepArchivePath);
            var tempPath = Utils.GetTempPath();

            foreach (var directoryPath in Directory.GetDirectories(tempPath, "updater-*", SearchOption.TopDirectoryOnly))
            {
                TryDeleteDirectory(directoryPath);
            }

            foreach (var filePath in Directory.GetFiles(tempPath, "*", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFullPath(filePath), normalizedKeepArchivePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!Utils.IsGuidByParse(Path.GetFileName(filePath)))
                {
                    continue;
                }

                if (IsValidGuiUpdateArchive(filePath, null))
                {
                    TryDeleteFile(filePath);
                }
            }

            var cacheDirectory = Path.GetDirectoryName(normalizedKeepArchivePath);
            if (cacheDirectory.IsNullOrEmpty() || !Directory.Exists(cacheDirectory))
            {
                return;
            }

            foreach (var filePath in Directory.GetFiles(cacheDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFullPath(filePath), normalizedKeepArchivePath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsValidGuiUpdateArchive(filePath, null))
                {
                    TryDeleteFile(filePath);
                }
            }
        }
        catch
        {
            // ignore temp cleanup failures
        }
    }

    private bool TryUseCachedGuiUpdateArchive(string filePath, GitHubReleaseAsset? asset)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        long? expectedSize = asset?.Size > 0 ? asset.Size : null;
        if (!IsValidGuiUpdateArchive(filePath, expectedSize))
        {
            TryDeleteFile(filePath);
            return false;
        }

        Logging.SaveLog($"Reusing cached NetCat update package: {filePath}");
        return true;
    }

    private static bool IsValidGuiUpdateArchive(string filePath, long? expectedSize)
    {
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists || fileInfo.Length <= 0)
            {
                return false;
            }

            if (expectedSize.HasValue && fileInfo.Length != expectedSize.Value)
            {
                return false;
            }

            using var archive = System.IO.Compression.ZipFile.OpenRead(filePath);
            return archive.Entries.Any(entry =>
                entry.Length > 0
                && string.Equals(entry.Name, Utils.GetExeName(Global.AppName), StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static string SanitizeFileName(string value)
    {
        if (value.IsNullOrEmpty())
        {
            return "update.zip";
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Concat(value.Select(ch => invalidChars.Contains(ch) ? '_' : ch));
    }

    private GitHubReleaseAsset? GetPreferredGuiAsset(GitHubRelease? release)
    {
        var assets = release?.Assets?
            .Where(asset => asset.BrowserDownloadUrl.IsNotEmpty() && asset.Name.IsNotEmpty())
            .ToList();
        if (assets == null || assets.Count == 0)
        {
            return null;
        }

        var zipAssets = assets
            .Where(asset => _zipExtensions.Any(ext => asset.Name!.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (zipAssets.Count == 0)
        {
            return null;
        }

        if (zipAssets.Count == 1)
        {
            return zipAssets[0];
        }

        return zipAssets
            .OrderByDescending(GetGuiAssetScore)
            .ThenByDescending(asset => asset.Size)
            .FirstOrDefault();
    }

    private GitHubReleaseAsset? GetPreferredZapretAsset(GitHubRelease? release)
    {
        var assets = release?.Assets?
            .Where(asset => asset.BrowserDownloadUrl.IsNotEmpty() && asset.Name.IsNotEmpty())
            .ToList();
        if (assets == null || assets.Count == 0)
        {
            return null;
        }

        var zipAssets = assets
            .Where(asset => _zipExtensions.Any(ext => asset.Name!.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (zipAssets.Count == 0)
        {
            return null;
        }

        return zipAssets
            .OrderByDescending(GetZapretAssetScore)
            .ThenByDescending(asset => asset.Size)
            .FirstOrDefault();
    }

    private int GetGuiAssetScore(GitHubReleaseAsset asset)
    {
        var name = asset.Name?.ToLowerInvariant() ?? string.Empty;
        var score = 0;

        if (name.Contains(Global.AppName.ToLowerInvariant()))
        {
            score += 100;
        }

        if (name.Contains("release"))
        {
            score += 20;
        }

        if (Utils.IsWindows())
        {
            score += MatchKeyword(name, ["windows", "win"], 40);
            score -= MatchKeyword(name, ["linux", "osx", "macos", "darwin"], 60);
        }
        else if (Utils.IsLinux())
        {
            score += MatchKeyword(name, ["linux"], 40);
            score -= MatchKeyword(name, ["windows", "osx", "macos", "darwin"], 60);
        }
        else if (Utils.IsMacOS())
        {
            score += MatchKeyword(name, ["osx", "macos", "darwin"], 40);
            score -= MatchKeyword(name, ["windows", "linux"], 60);
        }

        score += RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => MatchKeyword(name, ["arm64", "aarch64"], 30) - MatchKeyword(name, ["x64", "amd64"], 30),
            Architecture.X64 => MatchKeyword(name, ["x64", "amd64"], 30) - MatchKeyword(name, ["arm64", "aarch64"], 30),
            _ => 0,
        };

        return score;
    }

    private int GetZapretAssetScore(GitHubReleaseAsset asset)
    {
        var name = asset.Name?.ToLowerInvariant() ?? string.Empty;
        var score = 0;

        score += MatchKeyword(name, ["zapret", "discord", "youtube"], 20);

        if (Utils.IsWindows())
        {
            score += MatchKeyword(name, ["windows", "win"], 50);
            score -= MatchKeyword(name, ["linux", "macos", "darwin", "android"], 100);
        }

        score += RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => MatchKeyword(name, ["arm64", "aarch64"], 20) - MatchKeyword(name, ["x64", "amd64"], 20),
            Architecture.X64 => MatchKeyword(name, ["x64", "amd64"], 20) - MatchKeyword(name, ["arm64", "aarch64"], 20),
            _ => 0,
        };

        return score;
    }

    private static int MatchKeyword(string source, IEnumerable<string> keywords, int value)
    {
        return keywords.Any(source.Contains) ? value : 0;
    }

    private async Task<string?> GetUrlFromCore(CoreInfo? coreInfo)
    {
        if (Utils.IsWindows())
        {
            var url = RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => coreInfo?.DownloadUrlWinArm64,
                Architecture.X64 => coreInfo?.DownloadUrlWin64,
                _ => null,
            };

            if (coreInfo?.CoreType != ECoreType.v2rayN)
            {
                return url;
            }

            //Check for avalonia desktop windows version
            if (File.Exists(Path.Combine(Utils.GetBaseDirectory(), "libHarfBuzzSharp.dll")))
            {
                return url?.Replace(".zip", "-desktop.zip");
            }

            return url;
        }
        else if (Utils.IsLinux())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => coreInfo?.DownloadUrlLinuxArm64,
                Architecture.X64 => coreInfo?.DownloadUrlLinux64,
                _ => null,
            };
        }
        else if (Utils.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture switch
            {
                Architecture.Arm64 => coreInfo?.DownloadUrlOSXArm64,
                Architecture.X64 => coreInfo?.DownloadUrlOSX64,
                _ => null,
            };
        }
        return await Task.FromResult("");
    }

    #endregion CheckUpdate private

    #region Geo private

    private async Task<bool> UpdateGeoFiles()
    {
        var geoUrl = string.IsNullOrEmpty(_config?.ConstItem.GeoSourceUrl)
            ? Global.GeoUrl
            : _config.ConstItem.GeoSourceUrl;

        var hasChanges = false;
        List<string> files = ["geosite", "geoip"];
        foreach (var geoName in files)
        {
            var fileName = $"{geoName}.dat";
            var targetPath = Utils.GetBinPath($"{fileName}");
            var url = string.Format(geoUrl, geoName);

            hasChanges |= await DownloadGeoFile(url, fileName, targetPath);
        }
        return hasChanges;
    }

    private async Task<bool> UpdateOtherFiles()
    {
        //If it is not in China area, no update is required
        if (_config.ConstItem.GeoSourceUrl.IsNotEmpty())
        {
            return false;
        }

        var hasChanges = false;
        foreach (var url in Global.OtherGeoUrls)
        {
            var fileName = Path.GetFileName(url);
            var targetPath = Utils.GetBinPath($"{fileName}");

            hasChanges |= await DownloadGeoFile(url, fileName, targetPath);
        }
        return hasChanges;
    }

    private async Task<bool> UpdateSrsFileAll()
    {
        var geoipFiles = new List<string>();
        var geoSiteFiles = new List<string>();

        // Collect from routing rules
        var routingItems = await AppManager.Instance.RoutingItems();
        foreach (var routing in routingItems)
        {
            var rules = JsonUtils.Deserialize<List<RulesItem>>(routing.RuleSet);
            foreach (var item in rules ?? [])
            {
                AddPrefixedItems(item.Ip, "geoip:", geoipFiles);
                AddPrefixedItems(item.Domain, "geosite:", geoSiteFiles);
            }
        }

        // Collect from DNS configuration
        var dnsItem = await AppManager.Instance.GetDNSItem(ECoreType.sing_box);
        if (dnsItem != null)
        {
            ExtractDnsRuleSets(dnsItem.NormalDNS, geoipFiles, geoSiteFiles);
            ExtractDnsRuleSets(dnsItem.TunDNS, geoipFiles, geoSiteFiles);
        }

        // Append default items
        geoSiteFiles.AddRange(["google", "cn", "geolocation-cn", "category-ads-all"]);

        // Download files
        var path = Utils.GetBinPath("srss");
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path);
        }

        var hasChanges = false;
        foreach (var item in geoipFiles.Distinct())
        {
            hasChanges |= await UpdateSrsFile("geoip", item);
        }

        foreach (var item in geoSiteFiles.Distinct())
        {
            hasChanges |= await UpdateSrsFile("geosite", item);
        }
        return hasChanges;
    }

    private void AddPrefixedItems(List<string>? items, string prefix, List<string> output)
    {
        if (items == null)
        {
            return;
        }

        foreach (var item in items)
        {
            if (item.StartsWith(prefix))
            {
                output.Add(item.Substring(prefix.Length));
            }
        }
    }

    private void ExtractDnsRuleSets(string? dnsJson, List<string> geoipFiles, List<string> geoSiteFiles)
    {
        if (string.IsNullOrEmpty(dnsJson))
        {
            return;
        }

        try
        {
            var dns = JsonUtils.Deserialize<Dns4Sbox>(dnsJson);
            if (dns?.rules != null)
            {
                foreach (var rule in dns.rules)
                {
                    ExtractSrsRuleSets(rule, geoipFiles, geoSiteFiles);
                }
            }
        }
        catch { }
    }

    private void ExtractSrsRuleSets(Rule4Sbox? rule, List<string> geoipFiles, List<string> geoSiteFiles)
    {
        if (rule == null)
        {
            return;
        }

        AddPrefixedItems(rule.rule_set, "geosite-", geoSiteFiles);
        AddPrefixedItems(rule.rule_set, "geoip-", geoipFiles);

        // Handle nested rules recursively
        if (rule.rules != null)
        {
            foreach (var nestedRule in rule.rules)
            {
                ExtractSrsRuleSets(nestedRule, geoipFiles, geoSiteFiles);
            }
        }
    }

    private async Task<bool> UpdateSrsFile(string type, string srsName)
    {
        var srsUrl = string.IsNullOrEmpty(_config.ConstItem.SrsSourceUrl)
                        ? Global.SingboxRulesetUrl
                        : _config.ConstItem.SrsSourceUrl;

        var fileName = $"{type}-{srsName}.srs";
        var targetPath = Path.Combine(Utils.GetBinPath("srss"), fileName);
        var url = string.Format(srsUrl, type, $"{type}-{srsName}", srsName);

        return await DownloadGeoFile(url, fileName, targetPath);
    }

    private async Task<bool> DownloadGeoFile(string url, string fileName, string targetPath)
    {
        var tmpFileName = Utils.GetTempPath(Utils.GetGuid());
        var hasChanges = false;

        DownloadService downloadHandle = new();
        var remoteMetadata = await downloadHandle.GetRemoteFileMetadataAsync(url, true, Global.AppName);
        if (remoteMetadata != null && IsGeoFileUpToDate(url, targetPath, remoteMetadata))
        {
            await UpdateFunc(false, GetGeoFileUpToDateMessage(fileName));
            return false;
        }

        downloadHandle.UpdateCompleted += (sender2, args) =>
        {
            if (args.Success)
            {
                try
                {
                    if (File.Exists(tmpFileName))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? Utils.GetBaseDirectory());
                        if (File.Exists(targetPath) && FilesAreEqual(tmpFileName, targetPath))
                        {
                            if (remoteMetadata != null)
                            {
                                SaveGeoFileMetadata(url, targetPath, remoteMetadata, new FileInfo(tmpFileName).Length);
                            }

                            _ = UpdateFunc(false, GetGeoFileUpToDateMessage(fileName));
                        }
                        else
                        {
                            FileUtils.CopyFileWithRetry(tmpFileName, targetPath, true);
                            SaveGeoFileMetadata(url, targetPath, remoteMetadata, new FileInfo(tmpFileName).Length);
                            hasChanges = true;
                            _ = UpdateFunc(false, string.Format(ResUI.MsgDownloadGeoFileSuccessfully, fileName));
                        }

                        FileUtils.TryDeleteFile(tmpFileName);
                    }
                }
                catch (Exception ex)
                {
                    _ = UpdateFunc(false, ex.Message);
                }
            }
            else
            {
                _ = UpdateFunc(false, args.Msg);
            }
        };
        downloadHandle.Error += (sender2, args) =>
        {
            _ = UpdateFunc(false, args.GetException().Message);
        };

        await downloadHandle.DownloadFileAsync(url, tmpFileName, true, _timeout);
        return hasChanges;
    }

    #endregion Geo private

    private async Task UpdateFunc(bool notify, string msg)
    {
        await _updateFunc?.Invoke(notify, msg);
    }

    private string GetGeoFileUpToDateMessage(string fileName)
    {
        var template = GetResourceText("MsgGeoFileAlreadyUpToDate", "Geo file {0} is already up to date.");
        return string.Format(template, fileName);
    }

    private static string GetResourceText(string resourceKey, string fallback)
    {
        return ResUI.ResourceManager.GetString(resourceKey, ResUI.Culture) ?? fallback;
    }

    private static string GetGeoFileMetadataPath(string targetPath)
    {
        return $"{targetPath}.meta.json";
    }

    private static bool FilesAreEqual(string leftPath, string rightPath)
    {
        var leftInfo = new FileInfo(leftPath);
        var rightInfo = new FileInfo(rightPath);
        if (!leftInfo.Exists || !rightInfo.Exists || leftInfo.Length != rightInfo.Length)
        {
            return false;
        }

        using var leftStream = File.OpenRead(leftPath);
        using var rightStream = File.OpenRead(rightPath);
        using var leftHash = SHA256.Create();
        using var rightHash = SHA256.Create();
        return leftHash.ComputeHash(leftStream).SequenceEqual(rightHash.ComputeHash(rightStream));
    }

    private static bool IsGeoFileUpToDate(string sourceUrl, string targetPath, DownloadService.RemoteFileMetadata remoteMetadata)
    {
        if (!File.Exists(targetPath))
        {
            return false;
        }

        var fileInfo = new FileInfo(targetPath);
        var cachedMetadata = LoadGeoFileMetadata(targetPath);
        if (cachedMetadata != null
            && string.Equals(cachedMetadata.SourceUrl, sourceUrl, StringComparison.OrdinalIgnoreCase))
        {
            var etagMatches = remoteMetadata.ETag.IsNotEmpty()
                              && cachedMetadata.ETag.IsNotEmpty()
                              && string.Equals(remoteMetadata.ETag, cachedMetadata.ETag, StringComparison.Ordinal);
            if (etagMatches)
            {
                return true;
            }

            var sizeMatches = remoteMetadata.ContentLength.HasValue
                              && cachedMetadata.ContentLength.HasValue
                              && remoteMetadata.ContentLength == cachedMetadata.ContentLength
                              && fileInfo.Length == cachedMetadata.ContentLength.Value;
            var modifiedMatches = remoteMetadata.LastModified.HasValue
                                  && cachedMetadata.LastModified.HasValue
                                  && Math.Abs((remoteMetadata.LastModified.Value.UtcDateTime - cachedMetadata.LastModified.Value.UtcDateTime).TotalSeconds) < 1;
            var urlMatches = remoteMetadata.FinalUrl.IsNotEmpty()
                             && cachedMetadata.FinalUrl.IsNotEmpty()
                             && string.Equals(remoteMetadata.FinalUrl, cachedMetadata.FinalUrl, StringComparison.OrdinalIgnoreCase);
            if (sizeMatches && modifiedMatches && (urlMatches || remoteMetadata.FinalUrl.IsNullOrEmpty()))
            {
                return true;
            }
        }

        var fallbackSizeMatches = remoteMetadata.ContentLength.HasValue && fileInfo.Length == remoteMetadata.ContentLength.Value;
        var fallbackTimeMatches = remoteMetadata.LastModified.HasValue
                                  && fileInfo.LastWriteTimeUtc >= remoteMetadata.LastModified.Value.UtcDateTime.AddSeconds(-1);
        return fallbackSizeMatches && fallbackTimeMatches;
    }

    private static GeoFileCacheMetadata? LoadGeoFileMetadata(string targetPath)
    {
        try
        {
            var metadataPath = GetGeoFileMetadataPath(targetPath);
            if (!File.Exists(metadataPath))
            {
                return null;
            }

            var json = File.ReadAllText(metadataPath);
            return JsonUtils.Deserialize<GeoFileCacheMetadata>(json);
        }
        catch
        {
            return null;
        }
    }

    private static void SaveGeoFileMetadata(string sourceUrl, string targetPath, DownloadService.RemoteFileMetadata? remoteMetadata, long? contentLength = null)
    {
        try
        {
            var metadata = new GeoFileCacheMetadata
            {
                SourceUrl = sourceUrl,
                FinalUrl = remoteMetadata?.FinalUrl ?? sourceUrl,
                ContentLength = remoteMetadata?.ContentLength ?? contentLength,
                ETag = remoteMetadata?.ETag,
                LastModified = remoteMetadata?.LastModified
            };
            FileUtils.WriteAllTextAtomic(GetGeoFileMetadataPath(targetPath), JsonUtils.Serialize(metadata));
        }
        catch
        {
            // Ignore metadata cache failures, the update itself already succeeded.
        }
    }

    private static string GetZapretInstallPath()
    {
        var configuredPath = AppManager.Instance.Config.GuiItem.ZapretPath;
        var existingPath = ZapretHandler.FindZapretPath(configuredPath);
        if (existingPath.IsNotEmpty())
        {
            return existingPath;
        }

        if (configuredPath.IsNotEmpty())
        {
            return configuredPath;
        }

        return Path.Combine(Utils.StartupPath(), "zapret");
    }

    private static string? GetZapretLocalVersion(string zapretPath)
    {
        try
        {
            var servicePath = Path.Combine(zapretPath, "service.bat");
            if (!File.Exists(servicePath))
            {
                return null;
            }

            foreach (var line in File.ReadLines(servicePath).Take(12))
            {
                var match = Regex.Match(line, "LOCAL_VERSION=([^\"\\r\\n]+)");
                if (match.Success)
                {
                    return NormalizeZapretVersion(match.Groups[1].Value);
                }
            }
        }
        catch
        {
            // ignore local version read errors and continue with update
        }

        return null;
    }

    private static string? NormalizeZapretVersion(string? value)
    {
        if (value.IsNullOrEmpty())
        {
            return null;
        }

        var normalized = value.Trim().Trim('"', '\'');
        var match = Regex.Match(normalized, @"(?i)\bv?(?<version>\d+(?:\.\d+)*(?:[a-z]+\d*)?)\b");
        return match.Success ? match.Groups["version"].Value : normalized.TrimStart('v', 'V');
    }

    private static string? FindZapretExtractRoot(string rootPath)
    {
        IEnumerable<string> candidates = [rootPath];
        if (Directory.Exists(rootPath))
        {
            candidates = candidates.Concat(Directory.GetDirectories(rootPath, "*", SearchOption.AllDirectories));
        }

        return candidates
            .Where(path => ZapretHandler.IsValidZapretPath(path) && File.Exists(Path.Combine(path, "service.bat")))
            .OrderBy(path => path.Length)
            .FirstOrDefault();
    }

    private static string? BackupZapretUserFiles(string zapretPath)
    {
        if (!Directory.Exists(zapretPath))
        {
            return null;
        }

        var backupPath = Utils.GetTempPath($"zapret-preserve-{Utils.GetGuid()}");
        var hasFiles = false;
        foreach (var filePath in Directory.GetFiles(zapretPath, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(zapretPath, filePath);
            if (!ShouldPreserveZapretFile(relativePath))
            {
                continue;
            }

            var targetPath = Path.Combine(backupPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? backupPath);
            FileUtils.CopyFileWithRetry(filePath, targetPath, true);
            hasFiles = true;
        }

        return hasFiles ? backupPath : null;
    }

    private static void RestoreZapretUserFiles(string? backupPath, string targetPath)
    {
        if (backupPath.IsNullOrEmpty() || !Directory.Exists(backupPath))
        {
            return;
        }

        FileUtils.CopyDirectory(backupPath, targetPath, true, true);
    }

    private static bool ShouldPreserveZapretFile(string relativePath)
    {
        var normalizedPath = relativePath.Replace('/', '\\');
        if (string.Equals(normalizedPath, @"utils\check_updates.enabled", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!normalizedPath.StartsWith(@"lists\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var fileName = Path.GetFileName(normalizedPath);
        return fileName.Contains("-user", StringComparison.OrdinalIgnoreCase)
            || fileName.EndsWith(".backup", StringComparison.OrdinalIgnoreCase);
    }

    private static void DeleteDirectoryContents(string directoryPath)
    {
        if (!Directory.Exists(directoryPath))
        {
            return;
        }

        var dir = new DirectoryInfo(directoryPath);
        foreach (var file in dir.GetFiles())
        {
            file.IsReadOnly = false;
            file.Delete();
        }

        foreach (var subDir in dir.GetDirectories())
        {
            subDir.Delete(true);
        }
    }

    private static async Task WaitForZapretStopAsync()
    {
        for (var i = 0; i < 30; i++)
        {
            if (!ZapretHandler.IsRunning())
            {
                return;
            }

            await Task.Delay(250);
        }

        throw new TimeoutException("Failed to stop running Zapret before update.");
    }

    private static string? ResolveZapretStartConfig(string zapretPath, string? preferredConfig)
    {
        var configs = ZapretHandler.GetBatFiles(zapretPath);
        if (configs.Count == 0)
        {
            return null;
        }

        if (preferredConfig.IsNotEmpty()
            && configs.Any(config => string.Equals(config, preferredConfig, StringComparison.OrdinalIgnoreCase)))
        {
            return preferredConfig;
        }

        return configs.FirstOrDefault(config => string.Equals(config, "general.bat", StringComparison.OrdinalIgnoreCase))
            ?? configs[0];
    }

    private static void TryDeleteFile(string? filePath)
    {
        FileUtils.TryDeleteFile(filePath);
    }

    private static void TryDeleteDirectory(string? directoryPath)
    {
        FileUtils.TryDeleteDirectory(directoryPath);
    }

    private static string FormatUpdateStatusMessage(UpdateResult result)
    {
        if (!result.Msg.IsNullOrEmpty())
        {
            return result.Status == EUpdateAvailabilityStatus.Failed && result.FailureStage != EUpdateFailureStage.None
                ? $"{result.Msg} ({result.FailureStage})"
                : result.Msg;
        }

        return result.Status switch
        {
            EUpdateAvailabilityStatus.UpToDate => "Already up to date.",
            EUpdateAvailabilityStatus.Failed when result.FailureStage != EUpdateFailureStage.None => $"Update check failed ({result.FailureStage}).",
            EUpdateAvailabilityStatus.Failed => "Update check failed.",
            _ => "No updates."
        };
    }
}

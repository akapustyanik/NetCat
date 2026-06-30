namespace ServiceLib.Manager;

public sealed class CoreInfoManager
{
    private static readonly Lazy<CoreInfoManager> _instance = new(() => new());
    private List<CoreInfo>? _coreInfo;
    public static CoreInfoManager Instance => _instance.Value;

    private CoreInfoManager()
    {
        InitCoreInfo();
    }

    public CoreInfo? GetCoreInfo(ECoreType coreType)
    {
        if (_coreInfo == null)
        {
            InitCoreInfo();
        }
        return _coreInfo?.FirstOrDefault(t => t.CoreType == coreType);
    }

    public List<CoreInfo> GetCoreInfo()
    {
        if (_coreInfo == null)
        {
            InitCoreInfo();
        }
        return _coreInfo ?? [];
    }

    public string GetCoreExecFile(CoreInfo? coreInfo, out string msg)
    {
        var fileName = string.Empty;
        msg = string.Empty;
        foreach (var name in coreInfo?.CoreExes ?? [])
        {
            var vName = Utils.GetBinPath(Utils.GetExeName(name), coreInfo.CoreType.ToString());
            if (File.Exists(vName))
            {
                fileName = vName;
                break;
            }
        }
        if (fileName.IsNullOrEmpty())
        {
            msg = string.Format(ResUI.NotFoundCore, Utils.GetBinPath("", coreInfo?.CoreType.ToString()), coreInfo?.CoreExes?.LastOrDefault(), coreInfo?.Url);
            Logging.SaveLog(msg);
        }
        return fileName;
    }

    private void InitCoreInfo()
    {
        var urlN = GetCoreUrl(ECoreType.v2rayN);
        var urlSingbox = GetCoreUrl(ECoreType.sing_box);

        _coreInfo =
        [
            new CoreInfo
                {
                    CoreType = ECoreType.v2rayN,
                    Url = GetCoreUrl(ECoreType.v2rayN),
                    ReleaseApiUrl = urlN.Replace(Global.GithubUrl, Global.GithubApiUrl),
                    DownloadUrlWin64 = urlN + "/download/{0}/v2rayN-windows-64.zip",
                    DownloadUrlWinArm64 = urlN + "/download/{0}/v2rayN-windows-arm64.zip",
                    DownloadUrlLinux64 = urlN + "/download/{0}/v2rayN-linux-64.zip",
                    DownloadUrlLinuxArm64 = urlN + "/download/{0}/v2rayN-linux-arm64.zip",
                    DownloadUrlOSX64 = urlN + "/download/{0}/v2rayN-macos-64.zip",
                    DownloadUrlOSXArm64 = urlN + "/download/{0}/v2rayN-macos-arm64.zip",
                },

                new CoreInfo
                {
                    CoreType = ECoreType.sing_box,
                    CoreExes = ["sing-box-client", "sing-box"],
                    Arguments = "run -c {0} --disable-color",
                    Url = GetCoreUrl(ECoreType.sing_box),

                    ReleaseApiUrl = urlSingbox.Replace(Global.GithubUrl, Global.GithubApiUrl),
                    DownloadUrlWin64 = urlSingbox + "/download/{0}/sing-box-{1}-windows-amd64.zip",
                    DownloadUrlWinArm64 = urlSingbox + "/download/{0}/sing-box-{1}-windows-arm64.zip",
                    DownloadUrlLinux64 = urlSingbox + "/download/{0}/sing-box-{1}-linux-amd64.tar.gz",
                    DownloadUrlLinuxArm64 = urlSingbox + "/download/{0}/sing-box-{1}-linux-arm64.tar.gz",
                    DownloadUrlOSX64 = urlSingbox + "/download/{0}/sing-box-{1}-darwin-amd64.tar.gz",
                    DownloadUrlOSXArm64 = urlSingbox + "/download/{0}/sing-box-{1}-darwin-arm64.tar.gz",
                    Match = "sing-box",
                    VersionArg = "version",
                },
        ];
    }

    private static string GetCoreUrl(ECoreType eCoreType)
    {
        if (!Global.CoreUrls.TryGetValue(eCoreType, out var path))
            return string.Empty;
        return $"{Global.GithubUrl}/{path}/releases";
    }
}

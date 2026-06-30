namespace ServiceLib.Manager;

/// <summary>
/// Core process processing class
/// </summary>
public class CoreManager
{
    private static readonly Lazy<CoreManager> _instance = new(() => new());
    public static CoreManager Instance => _instance.Value;
    private Config _config;
    private WindowsJobService? _processJob;
    private ProcessService? _processService;
    private ProcessService? _processPreService;
    private readonly AutoProtocolWarmStandbyService _autoProtocolWarmStandbyService = new();
    private bool _linuxSudo = false;
    private Func<bool, string, Task>? _updateFunc;
    private const string _tag = "CoreHandler";

    public async Task Init(Config config, Func<bool, string, Task> updateFunc)
    {
        _config = config;
        _updateFunc = updateFunc;

        //Copy the bin folder to the storage location (for init)
        if (Environment.GetEnvironmentVariable(Global.LocalAppData) == "1")
        {
            var fromPath = Utils.GetBaseDirectory("bin");
            var toPath = Utils.GetBinPath("");
            if (fromPath != toPath)
            {
                FileUtils.CopyDirectory(fromPath, toPath, true, false);
            }
        }

        if (Utils.IsNonWindows())
        {
            var coreInfo = CoreInfoManager.Instance.GetCoreInfo();
            foreach (var it in coreInfo)
            {
                if (it.CoreType == ECoreType.v2rayN)
                {
                    if (Utils.UpgradeAppExists(out var upgradeFileName))
                    {
                        await Utils.SetLinuxChmod(upgradeFileName);
                    }
                    continue;
                }

                foreach (var name in it.CoreExes)
                {
                    var exe = Utils.GetBinPath(Utils.GetExeName(name), it.CoreType.ToString());
                    if (File.Exists(exe))
                    {
                        await Utils.SetLinuxChmod(exe);
                    }
                }
            }
        }
    }

    /// <param name="mainContext">Resolved main context (with pre-socks ports already merged if applicable).</param>
    /// <param name="preContext">Optional pre-socks context passed to <see cref="CoreStartPreService"/>.</param>
    public async Task LoadCore(CoreConfigContext? mainContext, CoreConfigContext? preContext)
    {
        if (mainContext == null)
        {
            Logging.SaveLog("VpnDiagnostics LoadCore skipped | mainContext=null");
            await UpdateFunc(false, ResUI.CheckServerSettings);
            return;
        }

        var node = mainContext.Node;
        var fileName = Utils.GetBinConfigPath(Global.CoreConfigFileName);
        Logging.SaveLog(BuildLoadCoreDiagnostics("begin", mainContext, fileName));
        var result = await CoreConfigHandler.GenerateClientConfig(mainContext, fileName);
        if (result.Success != true)
        {
            Logging.SaveLog($"VpnDiagnostics GenerateClientConfig failed | msg={result.Msg}");
            await UpdateFunc(true, result.Msg);
            return;
        }
        Logging.SaveLog(BuildGeneratedConfigDiagnostics(fileName));

        await UpdateFunc(false, $"{node.GetSummary()}");
        await UpdateFunc(false, $"{Utils.GetRuntimeInfo()}");
        await UpdateFunc(false, string.Format(ResUI.StartService, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")));
        await CoreStop();
        await Task.Delay(100);

        if (Utils.IsWindows() && _config.TunModeItem.EnableTun)
        {
            await Task.Delay(100);
            await WindowsUtils.RemoveTunDevice();
        }

        await CoreStart(mainContext);
        await CoreStartPreService(preContext);
        if (_processService != null)
        {
            _autoProtocolWarmStandbyService.StartFromSingboxConfig(fileName);
            _ = ProbeLocalProxySafeAsync();
            await UpdateFunc(true, $"{node.GetSummary()}");
        }
        else
        {
            Logging.SaveLog("VpnDiagnostics CoreStart finished | process=null");
        }
    }

    public async Task<ProcessService?> LoadCoreConfigSpeedtest(List<ServerTestItem> selecteds)
    {
        var coreType = selecteds.FirstOrDefault()?.CoreType == ECoreType.sing_box ? ECoreType.sing_box : ECoreType.Xray;
        var fileName = string.Format(Global.CoreSpeedtestConfigFileName, Utils.GetGuid(false));
        var configPath = Utils.GetBinConfigPath(fileName);
        var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(_config, configPath, selecteds, coreType);
        await UpdateFunc(false, result.Msg);
        if (result.Success != true)
        {
            return null;
        }

        await UpdateFunc(false, string.Format(ResUI.StartService, DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss")));
        await UpdateFunc(false, configPath);

        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        return await RunProcess(coreInfo, fileName, true, false);
    }

    public async Task<ProcessService?> LoadCoreConfigSpeedtest(ServerTestItem testItem)
    {
        var node = await AppManager.Instance.GetProfileItem(testItem.IndexId);
        if (node is null)
        {
            return null;
        }

        var fileName = string.Format(Global.CoreSpeedtestConfigFileName, Utils.GetGuid(false));
        var configPath = Utils.GetBinConfigPath(fileName);
        var (context, _) = await CoreConfigContextBuilder.Build(_config, node);
        var result = await CoreConfigHandler.GenerateClientSpeedtestConfig(_config, context, testItem, configPath);
        if (result.Success != true)
        {
            return null;
        }

        var coreType = context.RunCoreType;
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        return await RunProcess(coreInfo, fileName, true, false);
    }

    public async Task CoreStop()
    {
        try
        {
            _autoProtocolWarmStandbyService.Stop();

            if (_linuxSudo)
            {
                await CoreAdminManager.Instance.KillProcessAsLinuxSudo();
                _linuxSudo = false;
            }

            if (_processService != null)
            {
                await _processService.StopAsync();
                _processService.Dispose();
                _processService = null;
            }

            if (_processPreService != null)
            {
                await _processPreService.StopAsync();
                _processPreService.Dispose();
                _processPreService = null;
            }
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
        }
    }

    #region Private

    private async Task CoreStart(CoreConfigContext context)
    {
        var node = context.Node;
        var coreType = AppManager.Instance.RunningCoreType = AppManager.Instance.GetCoreType(node, node.ConfigType);
        var coreInfo = CoreInfoManager.Instance.GetCoreInfo(coreType);
        Logging.SaveLog($"VpnDiagnostics CoreStart | coreType={coreType} | node={SafeNodeSummary(node)} | configType={node.ConfigType} | network={node.GetNetwork()} | address={MaskHost(node.Address)} | port={node.Port}");

        var displayLog = node.ConfigType != EConfigType.Custom || node.DisplayLog;
        var proc = await RunProcess(coreInfo, Global.CoreConfigFileName, displayLog, true);
        if (proc is null)
        {
            return;
        }
        _processService = proc;
    }

    private async Task CoreStartPreService(CoreConfigContext? preContext)
    {
        if (_processService is { HasExited: false } && preContext != null)
        {
            var preCoreType = preContext?.Node?.CoreType ?? ECoreType.sing_box;
            var fileName = Utils.GetBinConfigPath(Global.CorePreConfigFileName);
            var result = await CoreConfigHandler.GenerateClientConfig(preContext, fileName);
            if (result.Success)
            {
                var coreInfo = CoreInfoManager.Instance.GetCoreInfo(preCoreType);
                var proc = await RunProcess(coreInfo, Global.CorePreConfigFileName, true, true);
                if (proc is null)
                {
                    return;
                }
                _processPreService = proc;
            }
        }
    }

    private async Task UpdateFunc(bool notify, string msg)
    {
        if (_updateFunc != null)
        {
            await _updateFunc(notify, msg);
        }
    }

    #endregion Private

    #region Process

    private async Task<ProcessService?> RunProcess(CoreInfo? coreInfo, string configPath, bool displayLog, bool mayNeedSudo)
    {
        var fileName = CoreInfoManager.Instance.GetCoreExecFile(coreInfo, out var msg);
        if (fileName.IsNullOrEmpty())
        {
            await UpdateFunc(false, msg);
            return null;
        }

        try
        {
            if (mayNeedSudo
                && _config.TunModeItem.EnableTun
                && (coreInfo.CoreType is ECoreType.sing_box or ECoreType.mihomo)
                && Utils.IsNonWindows())
            {
                _linuxSudo = true;
                await CoreAdminManager.Instance.Init(_config, _updateFunc);
                return await CoreAdminManager.Instance.RunProcessAsLinuxSudo(fileName, coreInfo, configPath);
            }

            return await RunProcessNormal(fileName, coreInfo, configPath, displayLog);
        }
        catch (Exception ex)
        {
            Logging.SaveLog(_tag, ex);
            await UpdateFunc(mayNeedSudo, ex.Message);
            return null;
        }
    }

    private async Task<ProcessService?> RunProcessNormal(string fileName, CoreInfo? coreInfo, string configPath, bool displayLog)
    {
        var environmentVars = new Dictionary<string, string>();
        foreach (var kv in coreInfo.Environment)
        {
            environmentVars[kv.Key] = string.Format(kv.Value, coreInfo.AbsolutePath ? Utils.GetBinConfigPath(configPath).AppendQuotes() : configPath);
        }

        var arguments = string.Format(coreInfo.Arguments, coreInfo.AbsolutePath ? Utils.GetBinConfigPath(configPath).AppendQuotes() : configPath);
        Logging.SaveLog($"VpnDiagnostics RunProcess | exe={fileName} | exists={File.Exists(fileName)} | args={arguments} | cwd={Utils.GetBinConfigPath()} | displayLog={displayLog} | envKeys={string.Join(",", environmentVars.Keys)}");

        var procService = new ProcessService(
            fileName: fileName,
            arguments: arguments,
            workingDirectory: Utils.GetBinConfigPath(),
            displayLog: displayLog,
            redirectInput: false,
            environmentVars: environmentVars,
            updateFunc: _updateFunc
        );

        await procService.StartAsync();
        Logging.SaveLog($"VpnDiagnostics RunProcess started | pid={procService.Id} | hasExited={procService.HasExited}");

        await Task.Delay(100);

        if (procService is null or { HasExited: true })
        {
            Logging.SaveLog("VpnDiagnostics RunProcess failed | process exited during startup");
            throw new Exception(ResUI.FailedToRunCore);
        }
        AddProcessJob(procService.Handle);

        return procService;
    }

    private async Task ProbeLocalProxyAsync()
    {
        var localPort = _config.Inbound?.FirstOrDefault()?.LocalPort ?? 0;
        if (localPort <= 0)
        {
            Logging.SaveLog("VpnDiagnostics ProbeLocalProxy skipped | localPort=0");
            return;
        }

        var proxyUri = new Uri($"http://127.0.0.1:{localPort}");
        using var handler = new HttpClientHandler
        {
            Proxy = new WebProxy(proxyUri),
            UseProxy = true
        };
        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(3)
        };

        var targets = new[]
        {
            "http://cp.cloudflare.com/generate_204",
            "https://www.google.com/generate_204"
        };

        await Task.WhenAll(targets.Select(async target =>
        {
            var started = DateTime.UtcNow;
            try
            {
                using var response = await client.GetAsync(target);
                var elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                Logging.SaveLog($"VpnDiagnostics ProbeLocalProxy | proxy={proxyUri} | target={target} | status={(int)response.StatusCode} | elapsedMs={elapsedMs}");
            }
            catch (Exception ex)
            {
                var elapsedMs = (int)(DateTime.UtcNow - started).TotalMilliseconds;
                Logging.SaveLog($"VpnDiagnostics ProbeLocalProxy failed | proxy={proxyUri} | target={target} | elapsedMs={elapsedMs} | error={ex.GetType().Name}: {ex.Message}");
            }
        }));
    }

    private async Task ProbeLocalProxySafeAsync()
    {
        try
        {
            await ProbeLocalProxyAsync();
        }
        catch (Exception ex)
        {
            Logging.SaveLog(nameof(ProbeLocalProxyAsync), ex);
        }
    }

    private string BuildLoadCoreDiagnostics(string stage, CoreConfigContext context, string configPath)
    {
        var node = context.Node;
        return $"VpnDiagnostics LoadCore {stage} | index={_config.IndexId} | coreType={context.RunCoreType} | tun={_config.TunModeItem.EnableTun} | localPort={_config.Inbound?.FirstOrDefault()?.LocalPort} | configPath={configPath} | node={SafeNodeSummary(node)} | configType={node.ConfigType} | network={node.GetNetwork()} | address={MaskHost(node.Address)} | port={node.Port}";
    }

    private static string BuildGeneratedConfigDiagnostics(string configPath)
    {
        try
        {
            var fileInfo = new FileInfo(configPath);
            if (!fileInfo.Exists)
            {
                return $"VpnDiagnostics GeneratedConfig | path={configPath} | exists=False";
            }

            using var stream = fileInfo.OpenRead();
            using var sha256 = SHA256.Create();
            var hash = Convert.ToHexString(sha256.ComputeHash(stream)).ToLowerInvariant();
            return $"VpnDiagnostics GeneratedConfig | path={configPath} | exists=True | size={fileInfo.Length} | sha256={hash}";
        }
        catch (Exception ex)
        {
            return $"VpnDiagnostics GeneratedConfig failed | path={configPath} | error={ex.GetType().Name}: {ex.Message}";
        }
    }

    private static string SafeNodeSummary(ProfileItem node)
    {
        return (node.Remarks ?? node.GetSummary() ?? string.Empty)
            .Replace("\r", " ")
            .Replace("\n", " ");
    }

    private static string MaskHost(string? host)
    {
        if (host.IsNullOrEmpty())
        {
            return string.Empty;
        }

        var value = host.Trim();
        if (IPAddress.TryParse(value, out _))
        {
            return value;
        }

        var parts = value.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length <= 2)
        {
            return value;
        }

        return $"{parts[0]}***.{string.Join('.', parts.Skip(parts.Length - 2))}";
    }

    private void AddProcessJob(nint processHandle)
    {
        if (Utils.IsWindows())
        {
            _processJob ??= new();
            try
            {
                _processJob?.AddProcess(processHandle);
            }
            catch { }
        }
    }

    #endregion Process
}

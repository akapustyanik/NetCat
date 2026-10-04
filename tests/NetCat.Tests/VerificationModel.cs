using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NetCat.Core;
using NetCat.Network;
using NetCat.Updater;

namespace NetCat.Verification;

// Test-only model, shared by the VM collector and offline evidence validator.
public static class VerificationModel
{
    public const string ReleaseHash = "2671DB7C363C9ACBD3FCC9F6E1C992C2F92E8509063133DBFBC9EB8682A8D5B3";
    const double WarmupFraction=.4, MemoryRangeMb=50, MemorySlopeMbPerMinute=5, HealthyRatio=.95;
    const int SteadySamples=10, HandleRange=50, ThreadGrowthLimit=5;
    const double ThreadSlopeLimit=1;
    public static readonly string[] Roles = ["NetCat", "MainSingBox", "OpenVpnSidecar", "Xray", "OpenVpn", "Zapret"];
    public static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    public static T Read<T>(JsonNode n, string key) => (n[key] ?? throw new InvalidDataException("Missing field: " + key)).GetValue<T>();
    static JsonArray Array(JsonNode n, string key) => n[key] as JsonArray ?? throw new InvalidDataException("Missing array: " + key);
    static double Number(JsonNode n, string key)
    {
        var element = JsonSerializer.SerializeToElement(n[key] ?? throw new InvalidDataException("Missing number: " + key));
        var value = element.GetDouble();
        return double.IsFinite(value) && value >= 0 ? value : throw new InvalidDataException("Non-finite or negative measurement");
    }
    static bool SiteAvailable(JsonNode n)
    {
        var site=n["SiteA"] ?? throw new InvalidDataException("Missing Site-A measurement");
        return Read<bool>(site,"DnsSucceeded") && Read<bool>(site,"TcpSucceeded") && Read<bool>(site,"TlsCertificateValid") && Read<int>(site,"HttpStatus")==200;
    }
    public static bool AuthoritativeTun(string text)
    {
        var line = text.Split('\n').LastOrDefault(x => x.Contains("TUN_OBSERVE", StringComparison.Ordinal));
        return line != null && Regex.IsMatch(line, @"\bruntimeLifecycle=Running\b") && Regex.IsMatch(line, @"\bfinalStatus=Healthy\b");
    }
    public static JsonObject Tun(string text, bool present) => new()
    {
        ["TunPresent"] = present,
        ["TunDataPathHealthy"] = Regex.IsMatch(text.Split('\n').LastOrDefault(x => x.Contains("TUN_OBSERVE")) ?? "", @"\blocalDataPath=True\b", RegexOptions.IgnoreCase),
        ["TunAuthoritativeHealthy"] = AuthoritativeTun(text)
    };
    public static JsonObject Lifecycle(string appendedText) => new()
    {
        ["MainSingBox"] = Regex.Matches(appendedText, @"MAIN_ROUTER lifecycle=[^\r\n]* -> Starting\b").Count,
        ["OpenVpn"] = Regex.Matches(appendedText, @"ACTION StartOpenVpn result=(?:ok|failed)\b").Count,
        ["OpenVpnSidecar"] = Regex.Matches(appendedText, @"OPENVPN_SIDECAR state=started pid=\d+").Count,
        ["Zapret"] = Regex.Matches(appendedText, @"ZAPRET_PROCESS state=started pid=(\d+)").Select(m=>m.Groups[1].Value).Distinct().Count(),
        ["RetryEventCount"] = Regex.Matches(appendedText, @"\bRETRY in=\d").Count,
        ["ReconcileCount"] = Regex.Matches(appendedText, @"\bRECONCILE gen=\d+ trigger=\w+ begin\b").Count,
        ["OpenVpnSessionReconnectCount"] = Regex.Matches(appendedText, @"OPENVPN_RECONNECT state=long-reconnect\b").Count
    };
    // Upper bound for one retry key. Physical recovery grants one further initial attempt.
    public static int MainBudget(double seconds)
    {
        int count = 1; double elapsed = 0;
        while (true)
        {
            elapsed += RuntimePlanner.DefaultBackoffIntervals[Math.Min(count - 1, RuntimePlanner.DefaultBackoffIntervals.Length - 1)].TotalSeconds;
            if (elapsed > seconds) return count;
            count++;
        }
    }
    public static int OpenVpnBudget(double seconds)
    {
        var retry = new OpenVpnRetryController(); var key = OpenVpnRetryKey.Empty;
        var start = DateTimeOffset.UnixEpoch; var now = start; int count = 0;
        while (retry.CanAttempt(key, now, ReconcileReason.OpenVpnStateChanged))
        {
            count++; retry.RecordAttemptStarted(key, now);
            retry.RecordFailure(key, OpenVpnFailureClass.TransientNetwork, "synthetic", now);
            if (retry.NextAttemptAt is not { } next || (next - start).TotalSeconds > seconds) break;
            now = next;
            if (count > 100) throw new InvalidDataException("Unbounded retry policy");
        }
        return count;
    }
    public static JsonObject Evaluate(string stage, JsonObject input, DateTimeOffset start, DateTimeOffset end, string runId)
    {
        if (end < start || end - start > TimeSpan.FromDays(2) || !Guid.TryParse(runId, out _)) throw new InvalidDataException("Invalid run identity/timestamps");
        var assertions = new JsonArray(); var metrics = new JsonObject();
        void Check(string name, object expected, object actual, bool pass) => assertions.Add(new JsonObject
        { ["Name"] = name, ["Expected"] = JsonSerializer.SerializeToNode(expected), ["Actual"] = JsonSerializer.SerializeToNode(actual), ["Passed"] = pass });
        void True(string key) { var b = Read<bool>(input, key); Check(key, true, b, b); }
        void Zero(string key) { var n = Number(input, key); Check(key, 0, n, n == 0); }
        void False(string key) { var b = Read<bool>(input, key); Check(key, false, b, !b); }
        var statusOverride = "";
        if(stage is "Crash" or "Network" or "Soak" or "Graceful") True("InitialHealthy");
        switch (stage)
        {
            case "Soak":
            {
                var samples = Array(input, "Samples").Select(x => x ?? throw new InvalidDataException()).ToArray();
                if (samples.Length < 6) throw new InvalidDataException("At least six samples required");
                var warmup = (int)Math.Ceiling(samples.Length * WarmupFraction);
                var steady = samples.Skip(Math.Max(warmup, samples.Length - SteadySamples)).ToArray();
                var times = samples.Select(x => DateTimeOffset.Parse(Read<string>(x, "Time"))).ToArray();
                if (times.Zip(times.Skip(1)).Any(x => x.First >= x.Second) || times[0] < start || times[^1] > end) throw new InvalidDataException("Invalid sample times");
                metrics["WarmupSamples"] = warmup; metrics["SteadyStateSamples"] = steady.Length;
                metrics["Thresholds"] = new JsonObject { ["WarmupFraction"]=WarmupFraction,["SteadyWindowMaxSamples"]=SteadySamples,["MemoryRangeMb"] = MemoryRangeMb, ["MemorySlopeMbPerMinute"] = MemorySlopeMbPerMinute, ["HandleRange"] = HandleRange, ["ThreadGrowth"] = ThreadGrowthLimit, ["PositiveThreadSlopePerMinute"] = ThreadSlopeLimit, ["HealthyRatio"] = HealthyRatio };
                // Diagnostic acceptance tolerances, not production limits: after 40% warmup,
                // last <=10 samples, <=50MB range, <=5MB/min slope, <=50 handles.
                foreach (var field in new[] { "WorkingSetMb", "PrivateMemoryMb" })
                {
                    var y = steady.Select(s => Number(s, field)).ToArray();
                    var t = steady.Select(s => (DateTimeOffset.Parse(Read<string>(s, "Time")) - times[0]).TotalMinutes).ToArray();
                    var tm = t.Average(); var ym = y.Average();
                    double slope = t.Zip(y).Sum(v => (v.First - tm) * (v.Second - ym)) / t.Sum(v => (v - tm) * (v - tm));
                    var range = y.Max() - y.Min();
                    metrics[field] = new JsonObject { ["Min"] = y.Min(), ["Max"] = y.Max(), ["Range"] = range, ["SlopeMbPerMinute"] = slope };
                    Check(field + "Stable", new{MaxRange=MemoryRangeMb,MaxAbsSlope=MemorySlopeMbPerMinute}, new { Range = range, Slope = slope }, range <= MemoryRangeMb && Math.Abs(slope) <= MemorySlopeMbPerMinute);
                }
                foreach (var (field, bound) in new[] { ("Handles", HandleRange), ("OwnedProcessCount", 0), ("OwnedRouteCount", 0) })
                {
                    var values = steady.Select(x => Number(x, field)).ToArray();
                    var delta = values[^1] - values[0]; var range = values.Max() - values.Min();
                    metrics[field] = new JsonObject { ["Start"] = values[0], ["End"] = values[^1], ["Delta"] = delta, ["Max"] = values.Max(), ["Range"] = range };
                    Check(field + "Stable", bound, range, range <= bound);
                }
                var threads = steady.Select(s => Number(s, "Threads")).ToArray();
                var threadTimes = steady.Select(s => (DateTimeOffset.Parse(Read<string>(s, "Time")) - times[0]).TotalMinutes).ToArray();
                var meanTime = threadTimes.Average(); var meanThreads = threads.Average();
                var threadSlope = threadTimes.Zip(threads).Sum(v => (v.First - meanTime) * (v.Second - meanThreads)) / threadTimes.Sum(t => (t - meanTime) * (t - meanTime));
                var growth = Math.Max(0, threads[^1] - threads[0]); var positiveSlope = Math.Max(0, threadSlope);
                metrics["Threads"] = new JsonObject { ["Start"]=threads[0], ["End"]=threads[^1], ["Range"]=threads.Max()-threads.Min(), ["ThreadGrowth"]=growth, ["PositiveThreadSlopePerMinute"]=positiveSlope };
                Check("ThreadsStable", new { MaxGrowth=ThreadGrowthLimit, MaxPositiveSlope=ThreadSlopeLimit }, new { Growth=growth, PositiveSlope=positiveSlope }, growth <= ThreadGrowthLimit && positiveSlope <= ThreadSlopeLimit);
                foreach (var field in new[] { "TunHealthy", "OpenVpnReady", "SiteAAvailable" })
                {
                    bool Available(JsonNode x) => field switch { "TunHealthy"=>AuthoritativeTun(Read<string>(x,"TunObservation")),"SiteAAvailable"=>SiteAvailable(x),_=>Read<bool>(x,field) };
                    var ratio = steady.Count(Available) / (double)steady.Length;
                    if(field=="TunHealthy") Check("TunMetricMatchesObservation",true,samples.All(x=>Read<bool>(x,field)==Available(x)),samples.All(x=>Read<bool>(x,field)==Available(x)));
                    if(field=="SiteAAvailable") Check("SiteMetricMatchesObservation",true,samples.All(x=>Read<bool>(x,field)==Available(x)),samples.All(x=>Read<bool>(x,field)==Available(x)));
                    metrics[field + "Ratio"] = ratio; Check(field, HealthyRatio, ratio, ratio >= HealthyRatio);
                }
                var transitions = new JsonArray(); int total = 0;
                foreach (var role in Roles)
                {
                    string? previous = null; int fatalities = 0;
                    foreach (var sample in samples)
                    {
                        var p = sample["Roles"]?[role] ?? throw new InvalidDataException("Missing role: " + role);
                        var active = Read<bool>(p, "Active"); var alive = Read<bool>(p, "Alive");
                        var identity = alive ? Read<int>(p, "Pid") + "@" + Read<string>(p, "CreationTime") : null;
                        if (active && identity == null && previous == null && sample == samples[0]) fatalities++;
                        if (previous != null && identity != previous)
                        {
                            fatalities++; transitions.Add(new JsonObject { ["Role"] = role, ["Time"] = sample["Time"]!.DeepClone(), ["Kind"] = "UnexpectedTermination", ["PreviousIdentity"] = previous });
                        }
                        if (identity != null && identity != previous && sample != samples[0]) transitions.Add(new JsonObject { ["Role"] = role, ["Time"] = sample["Time"]!.DeepClone(), ["Kind"] = "Recovery", ["Identity"] = identity });
                        previous = identity;
                    }
                    metrics["Unexpected" + role + "Terminations"] = fatalities; total += fatalities;
                }
                metrics["LifecycleTransitions"] = transitions;
                metrics["FatalitiesObserved"] = total;
                metrics["FatalityObservationScope"] = "PID/creation-time samples; no claim about unobserved processes between samples";
                metrics["ExpectedRestartPolicy"] = "None during steady-state soak; injected crash restarts belong to the separate crash stage";
                Check("ZeroUnexpectedFatalities", 0, total, total == 0);
                True("OwnershipComplete");
                break;
            }
            case "Crash":
            {
                var engines = Array(input, "Engines");
                Check("ActiveEnginesPresent", ">= 1", engines.Count(x=>Read<bool>(x!,"Active")), engines.Any(x=>Read<bool>(x!,"Active")));
                foreach (var role in Roles.Skip(1))
                {
                    var rows = engines.Where(x => Read<string>(x!, "Role") == role).ToArray();
                    if (rows.Length != 1) throw new InvalidDataException("Missing/duplicate engine");
                    var r = rows[0]!; bool active = Read<bool>(r, "Active");
                    metrics[role + "Status"] = "NotApplicable";
                    if (!active) continue;
                    var seconds = Number(r, "RecoveryDurationSeconds");
                    if(seconds < 0 || seconds > (end-start).TotalSeconds) throw new InvalidDataException("Invalid recovery duration");
                    var allowed = role == "OpenVpn" ? OpenVpnBudget(seconds) : MainBudget(seconds);
                    // Xray and sidecar launches are included in the parent component
                    // lifecycle; Zapret/OpenVpnRoutes use RuntimePlanner component backoff.
                    var attempts = Number(r, "StartAttemptsDelta");
                    Check(role + "RestartBudget", allowed, attempts, attempts >= 1 && attempts <= allowed);
                    var arithmetic=Number(r,"StartAttemptsAfter")-Number(r,"StartAttemptsBefore")==attempts;
                    Check(role+"CounterArithmetic",true,arithmetic,arithmetic);
                    var ledger=Array(input,"HarnessExplicitKillTargets");
                    var targetRecorded=ledger.Count(x=>Read<string>(x!,"Identity")==Read<string>(r,"InitialIdentity"))==1;
                    Check(role+"KillLedgerMatches",true,targetRecorded,targetRecorded);
                    bool pass = Read<bool>(r, "TargetOwned") && Read<bool>(r, "KillIssued") && Read<int>(r, "InitialPid") == Read<int>(r, "KilledPid") && Read<bool>(r, "Recovered") && Read<bool>(r, "FinalHealthy") && Read<string>(r, "InitialIdentity") != Read<string>(r, "RecoveredIdentity");
                    Check(role + "Recovery", true, pass, pass);
                    metrics[role+"Status"]=pass && arithmetic && targetRecorded && attempts>=1 && attempts<=allowed ? "ConfirmedPass" : "ConfirmedFail";
                    if(role=="OpenVpn") { var restarts=Number(r,"OpenVpnProcessRestartCount");Check("OpenVpnReplacementProcessObserved",">=1",restarts,!Read<bool>(r,"Recovered") || restarts>=1); }
                }
                var foreign = Array(input, "ForeignMatchingProcessesBefore").Select(x => Read<string>(x!, "Identity")).ToHashSet();
                var touched = Array(input, "HarnessExplicitKillTargets").Where(x => foreign.Contains(Read<string>(x!, "Identity"))).Count();
                metrics["ExplicitlyTerminatedForeignProcesses"] = touched;
                metrics["ProductionForeignTerminationTelemetry"] = "NotVerified";
                Check("HarnessForeignSafety", 0, touched, touched == 0);
                Zero("LeftoverOwnedProcessesAfterExit"); True("OwnershipComplete");
                break;
            }
            case "Network":
            {
                foreach (var key in new[] { "PhysicalLossObserved", "PhysicalRecoveryObserved", "WaitForPhysicalNetworkObserved", "TunPresentAfterRecovery", "OpenVpnReadyAfterRecovery", "OwnershipComplete" }) True(key);
                var tun = AuthoritativeTun(Read<string>(input, "TunObservation")); Check("TunAuthoritativeHealthyAfterRecovery", true, tun, tun);
                var code = Read<int>(input, "SiteAHttpStatusAfterRecovery"); Check("SiteA", 200, code, code == 200);
                Check("SiteAHttpsValidated",true,SiteAvailable(input),SiteAvailable(input));
                Check("SiteHttpSummaryConsistent",Read<int>(input["SiteA"]!,"HttpStatus"),code,Read<int>(input["SiteA"]!,"HttpStatus")==code);
                Zero("StaleOwnedRouteCount");
                var seconds = Number(input, "RecoveryDurationSeconds");
                if(seconds < 0 || seconds > (end-start).TotalSeconds) throw new InvalidDataException("Invalid recovery duration");
                metrics["RetryPolicy"]="One initial key plus one physical-recovery initial attempt; failed retries use production RuntimePlanner/OpenVpnRetryController schedules";
                foreach (var (field, allowed) in new[] { ("StartMainRouterAttemptCount", MainBudget(seconds) + 1), ("StartOpenVpnAttemptCount", OpenVpnBudget(seconds) + 1), ("OpenVpnSidecarStartCount", MainBudget(seconds) + 1) })
                { var count = Number(input, field); Check(field, allowed, count, count >= 0 && count <= allowed); }
                break;
            }
            case "Updater":
            {
                var modules=Array(input,"ModuleChecks");
                bool metadata=modules.Select(x=>Read<string>(x!,"Module")).Order().SequenceEqual(ModuleUpdater.Keys.Order()) && modules.All(x=>Read<string>(x!,"StatusKind") is "Current" or "UpdateAvailable" or "AutoUpdateUnsupported");
                Check("MetadataChecksPassed",true,metadata,metadata);
                Check("MetadataSummaryConsistent",metadata,Read<bool>(input,"MetadataChecksPassed"),metadata==Read<bool>(input,"MetadataChecksPassed"));
                var ovpn=modules.SingleOrDefault(x=>Read<string>(x!,"Module")=="openvpn");
                bool typed=ovpn!=null && Read<string>(ovpn,"InstalledVersion").TrimStart('v')=="2.6.22" && Read<string>(ovpn,"LatestSupportedVersion").TrimStart('v')=="2.6.22" && Read<string>(ovpn,"LatestUpstreamVersion").TrimStart('v')=="2.6.23" && !Read<bool>(ovpn,"TrustedUpdateArtifactAvailable") && !Read<bool>(ovpn,"AutoUpdateSupported") && Read<string>(ovpn,"StatusKind") is "AutoUpdateUnsupported" or "Current";
                Check("OpenVpnTypedStatusPassed",true,typed,typed);
                Check("OpenVpnSummaryConsistent",typed,Read<bool>(input,"OpenVpnTypedStatusPassed"),typed==Read<bool>(input,"OpenVpnTypedStatusPassed"));
                True("SyntheticRollbackPassed");
                var tx = input["Transaction"] ?? throw new InvalidDataException();
                var check = Read<bool>(tx, "CheckSucceeded"); Check("RealMetadata", true, check, check);
                if (Read<bool>(tx, "UpdateAvailable"))
                {
                    foreach (var f in new[] { "DownloadSucceeded", "ArtifactVerificationSucceeded", "InstallSucceeded", "RuntimeValidationSucceeded", "RollbackAttempted", "RollbackSucceeded" })
                    { var b = Read<bool>(tx, f); Check(f, true, b, b); }
                    var restored = Read<string>(tx, "InstalledHashBefore") == Read<string>(tx, "RestoredHash") && Read<string>(tx, "InstalledVersionBefore") == Read<string>(tx, "RestoredVersion");
                    var changed = Read<string>(tx, "InstalledHashBefore") != Read<string>(tx, "InstalledHashAfter");
                    Check("OriginalRestored", true, restored, restored); Check("NewArtifactInstalled", true, changed, changed);
                    metrics["RealTransactionStatus"] = restored && changed && new[] { "DownloadSucceeded", "ArtifactVerificationSucceeded", "InstallSucceeded", "RuntimeValidationSucceeded", "RollbackAttempted", "RollbackSucceeded" }.All(f=>Read<bool>(tx,f)) ? "ConfirmedPass" : "ConfirmedFail";
                }
                else metrics["RealTransactionStatus"] = check ? "NotApplicable" : "NotVerified";
                break;
            }
            case "Graceful":
                Check("CliExitCode", 0, Read<int>(input, "CliExitCode"), Read<int>(input, "CliExitCode") == 0);
                True("PrimaryProcessExited"); False("ProcessKillUsed"); Zero("OwnedProcessesRemaining");
                True("NetCatTunRemoved"); True("OpenVpnAdapterRemoved"); True("OwnedRoutesRemoved"); True("OwnershipComplete"); break;
            case "Manual":
                True("DirectoryExists"); True("NetCatExeExists");
                var hash = Read<string>(input, "GuestHash"); Check("HashMatch", ReleaseHash, hash, hash.Equals(ReleaseHash, StringComparison.OrdinalIgnoreCase));
                Check("CandidateIdentity", "Candidate25", Read<string>(input, "CandidateIdentity"), Read<string>(input, "CandidateIdentity") == "Candidate25");
                Zero("RunningExactPathInstances"); break;
            case "DomainAccessControl":
                Check("OffHttpStatus", 403, Read<int>(input, "OffHttpStatus"), Read<int>(input, "OffHttpStatus") == 403);
                Check("OnHttpStatus", 200, Read<int>(input, "OnHttpStatus"), Read<int>(input, "OnHttpStatus") == 200);
                True("CorporatePathObserved"); metrics["StrictNetworkFailClosed"] = "NotVerified"; break;
            case "Autostart":
                foreach (var field in new[] { "AutostartFeatureSupported", "AutostartConfigured", "BootChanged", "NetCatAutoStarted", "ExecutablePathMatches", "CandidateIdentityMatches", "DesiredStateRecovered", "TunHealthy", "OpenVpnReady", "OriginalSettingRestored" }) True(field);
                False("ManualLaunchPerformed");
                var autoTun=AuthoritativeTun(Read<string>(input,"TunObservation"));Check("AuthoritativeTun",true,autoTun,autoTun);
                Check("DuplicateCount", 1, Read<int>(input, "DuplicateCount"), Read<int>(input, "DuplicateCount") == 1);
                if (!Read<bool>(input, "InteractiveLogonObserved")) statusOverride = "Blocked";
                break;
            case "Reboot":
                True("BootChanged"); Zero("OrphanCount"); Zero("StaleOwnedRouteCount"); True("OwnershipComplete");
                var scenario = Read<int>(input, "Scenario");
                if (scenario == 1) { False("MainRunning"); False("TunPresent"); False("OpenVpnReady"); False("ZapretRunning"); }
                else if (scenario is >= 2 and <= 5)
                {
                    True("MainRunning"); var healthy = AuthoritativeTun(Read<string>(input, "TunObservation")); Check("AuthoritativeTun", true, healthy, healthy);
                    if (scenario == 2) False("OpenVpnReady");
                    if (scenario == 3) { True("OpenVpnReady"); True("SiteAAvailable"); }
                    if (scenario == 4) { True("ZapretDesiredBeforeReboot"); True("ZapretRunning"); }
                    if (scenario == 5) True("RealTransactionCompletedThisRunBeforeReboot");
                }
                else throw new InvalidDataException("Unknown reboot scenario");
                break;
            default: throw new InvalidDataException("Unknown stage");
        }
        var passed = assertions.Count > 0 && assertions.All(x => Read<bool>(x!, "Passed"));
        var status = statusOverride.Length != 0 ? statusOverride : passed ? "ConfirmedPass" : "ConfirmedFail";
        if (stage == "DomainAccessControl") metrics["DomainAccessControl"] = status;
        return new JsonObject { ["SchemaVersion"] = "25.1", ["Candidate"] = "Candidate25", ["RunId"] = runId, ["Stage"] = stage, ["StartTime"] = start.ToString("O"), ["EndTime"] = end.ToString("O"), ["DurationSeconds"] = (end-start).TotalSeconds, ["Inputs"] = input.DeepClone(), ["Assertions"] = assertions, ["Metrics"] = metrics, ["Status"] = status };
    }
    public static bool Validate(JsonObject evidence)
    {
        try
        {
            if (Read<string>(evidence, "SchemaVersion") != "25.1" || Read<string>(evidence, "Candidate") != "Candidate25") return false;
            var rebuilt = Evaluate(Read<string>(evidence, "Stage"), (JsonObject)evidence["Inputs"]!, DateTimeOffset.Parse(Read<string>(evidence, "StartTime")), DateTimeOffset.Parse(Read<string>(evidence, "EndTime")), Read<string>(evidence, "RunId"));
            return JsonNode.DeepEquals(rebuilt, evidence);
        }
        catch { return false; }
    }
}

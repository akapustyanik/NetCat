using System.Text.Json.Nodes;
using NetCat.Verification;
using Xunit;

namespace NetCat.Tests;

public class VerificationHarnessTests
{
    static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-09-26T00:00:00Z");
    const string Healthy = "TUN_OBSERVE runtimeLifecycle=Running localDataPath=True finalStatus=Healthy";
    static JsonObject Site()=>new(){["DnsSucceeded"]=true,["TcpSucceeded"]=true,["TlsCertificateValid"]=true,["HttpStatus"]=200};
    static JsonObject Evaluate(string stage, JsonObject input)
    {
        return VerificationModel.Evaluate(stage, input, Start, Start.AddMinutes(20), Guid.NewGuid().ToString());
    }
    static void Pass(string stage, JsonObject input) { var e=Evaluate(stage,input); Assert.Equal("ConfirmedPass",e["Status"]!.GetValue<string>()); Assert.True(VerificationModel.Validate(e)); }
    static void Fail(string stage, JsonObject input) => Assert.Equal("ConfirmedFail", Evaluate(stage,input)["Status"]!.GetValue<string>());
    static JsonObject Soak()
    {
        var samples=new JsonArray();
        for(int i=0;i<20;i++)
        {
            var roles=new JsonObject();
            foreach(var (role,index) in VerificationModel.Roles.Select((r,i)=>(r,i))) roles[role]=new JsonObject{["Active"]=true,["Alive"]=true,["Pid"]=100+index,["CreationTime"]=Start.ToString("O")};
            samples.Add(new JsonObject{["Time"]=Start.AddMinutes(i).ToString("O"),["WorkingSetMb"]=i<8?250+i*20:430,["PrivateMemoryMb"]=i<8?200+i*10:280,["Handles"]=100,["Threads"]=10,["OwnedProcessCount"]=6,["OwnedRouteCount"]=4,["TunHealthy"]=true,["TunObservation"]=Healthy,["OpenVpnReady"]=true,["SiteAAvailable"]=true,["Roles"]=roles});
            samples[i]!["SiteA"]=Site();
        }
        return new(){["Samples"]=samples,["OwnershipComplete"]=true,["InitialHealthy"]=true};
    }
    static JsonObject Crash()
    {
        var engines=new JsonArray();
        var ledger=new JsonArray();int pid=10;
        foreach(var role in VerificationModel.Roles.Skip(1)) {
            engines.Add(new JsonObject{["Role"]=role,["Active"]=true,["TargetOwned"]=true,["KillIssued"]=true,["InitialPid"]=pid,["KilledPid"]=pid,["InitialIdentity"]=pid+"@before",["RecoveredIdentity"]=(pid+100)+"@after",["Recovered"]=true,["FinalHealthy"]=true,["RecoveryDurationSeconds"]=15d,["StartAttemptsDelta"]=1d,["StartAttemptsBefore"]=1,["StartAttemptsAfter"]=2,["OpenVpnProcessRestartCount"]=1});
            ledger.Add(new JsonObject{["Identity"]=pid+"@before"});pid++;
        }
        return new(){["InitialHealthy"]=true,["Engines"]=engines,["ForeignMatchingProcessesBefore"]=new JsonArray(new JsonObject{["Identity"]="99@foreign"}),["HarnessExplicitKillTargets"]=ledger,["LeftoverOwnedProcessesAfterExit"]=0d,["OwnershipComplete"]=true};
    }
    static JsonObject Network()=>new(){["SiteA"]=Site(),["InitialHealthy"]=true,["PhysicalLossObserved"]=true,["PhysicalRecoveryObserved"]=true,["WaitForPhysicalNetworkObserved"]=true,["TunPresentAfterRecovery"]=true,["OpenVpnReadyAfterRecovery"]=true,["OwnershipComplete"]=true,["TunObservation"]=Healthy,["SiteAHttpStatusAfterRecovery"]=200,["StaleOwnedRouteCount"]=0d,["RecoveryDurationSeconds"]=15d,["StartMainRouterAttemptCount"]=1d,["StartOpenVpnAttemptCount"]=1d,["OpenVpnSidecarStartCount"]=1d};
    static JsonObject Graceful()=>new(){["InitialHealthy"]=true,["CliExitCode"]=0,["PrimaryProcessExited"]=true,["ProcessKillUsed"]=false,["OwnedProcessesRemaining"]=0d,["NetCatTunRemoved"]=true,["OpenVpnAdapterRemoved"]=true,["OwnedRoutesRemoved"]=true,["OwnershipComplete"]=true};
    [Fact] public void SoakSteadyStateStableAfterWarmup()=>Pass("Soak",Soak());
    static JsonObject Threads(params int[] values) { var s=Soak(); for(int i=0;i<values.Length;i++) s["Samples"]![10+i]!["Threads"]=values[i]; return s; }
    [Fact] public void ThreadCountShrinkingIsStable()=>Pass("Soak",Threads(80,78,77,75,74,73,72,70,69,68));
    [Fact] public void ThreadCountFlatIsStable()=>Pass("Soak",Threads(80,80,80,80,80,80,80,80,80,80));
    [Fact] public void ThreadCountSteadyGrowthFails()=>Fail("Soak",Threads(68,70,72,74,76,78,80,82,84,86));
    [Fact] public void ThreadCountOscillationWithoutGrowthIsDiagnosticNotLeak()=>Pass("Soak",Threads(80,68,80,68,80,68,80,68,80,68));
    [Fact] public void SoakRunawaySlopeFails(){var s=Soak();for(int i=10;i<20;i++)s["Samples"]![i]!["WorkingSetMb"]=430+i*12;Fail("Soak",s);}
    [Fact] public void SoakHandleRunawayFails(){var s=Soak();s["Samples"]![19]!["Handles"]=1000;Fail("Soak",s);}
    [Fact] public void SoakTracksChildFatalityAndRecovery(){var s=Soak();s["Samples"]![15]!["Roles"]!["Xray"]!["Pid"]=999;Fail("Soak",s);Assert.True(Evaluate("Soak",s)["Metrics"]!["FatalitiesObserved"]!.GetValue<int>()>0);}
    [Fact] public void CrashPassRequiresAllActiveEngines(){var s=Crash();Pass("Crash",s);s["Engines"]![1]!["FinalHealthy"]=false;Fail("Crash",s);}
    [Fact] public void CrashForeignOwnedTargetDetection(){var s=Crash();s["HarnessExplicitKillTargets"]!.AsArray().Add(new JsonObject{["Identity"]="99@foreign"});Fail("Crash",s);}
    [Fact] public void CrashRestartStormFails(){var s=Crash();s["Engines"]![0]!["StartAttemptsDelta"]=100d;Fail("Crash",s);}
    [Fact] public void NetworkRecoveryRequiresAuthoritativeTunHealth(){var s=Network();Pass("Network",s);s["TunObservation"]="APP_START TUN Healthy routesPresent=True";Fail("Network",s);}
    [Fact] public void NetworkRecoveryRetryBudgetExceededFails(){var s=Network();s["StartOpenVpnAttemptCount"]=100d;Fail("Network",s);}
    [Fact] public void UpdaterPassRequiresRealTransactionWhenApplicable()
    {
        var tx=new JsonObject{["CheckSucceeded"]=true,["UpdateAvailable"]=true,["DownloadSucceeded"]=true,["ArtifactVerificationSucceeded"]=true,["InstallSucceeded"]=true,["RuntimeValidationSucceeded"]=true,["RollbackAttempted"]=true,["RollbackSucceeded"]=true,["InstalledHashBefore"]="before",["RestoredHash"]="before",["InstalledHashAfter"]="after",["InstalledVersionBefore"]="1",["RestoredVersion"]="1"};
        var s=new JsonObject{["MetadataChecksPassed"]=true,["OpenVpnTypedStatusPassed"]=true,["SyntheticRollbackPassed"]=true,["Transaction"]=tx};
        var modules=new JsonArray();foreach(var key in NetCat.Updater.ModuleUpdater.Keys)modules.Add(new JsonObject{["Module"]=key,["StatusKind"]="Current",["InstalledVersion"]="2.6.22",["LatestSupportedVersion"]="2.6.22",["LatestUpstreamVersion"]="2.6.23",["TrustedUpdateArtifactAvailable"]=false,["AutoUpdateSupported"]=false});s["ModuleChecks"]=modules;
        Pass("Updater",s);tx["InstallSucceeded"]=false;Fail("Updater",s);
    }
    [Fact] public void GracefulExitRequiresOpenVpnAdapterRemoved(){var s=Graceful();Pass("Graceful",s);s["OpenVpnAdapterRemoved"]=false;Fail("Graceful",s);}
    [Fact] public void ManualDeploymentRequiresMatchingHash(){var s=new JsonObject{["DirectoryExists"]=true,["NetCatExeExists"]=true,["GuestHash"]=VerificationModel.ReleaseHash,["CandidateIdentity"]="Candidate25",["RunningExactPathInstances"]=0d};Pass("Manual",s);s["GuestHash"]="wrong";Fail("Manual",s);}
    [Fact] public void ApplicationAccessControlIsNotStrictNetworkFailClosed(){var s=new JsonObject{["OffHttpStatus"]=403,["OnHttpStatus"]=200,["CorporatePathObserved"]=true};Pass("DomainAccessControl",s);Assert.Equal("NotVerified",Evaluate("DomainAccessControl",s)["Metrics"]!["StrictNetworkFailClosed"]!.GetValue<string>());}
    [Fact] public void AutostartManualLaunchCannotCountAsAutostartPass()
    {
        var s=new JsonObject();foreach(var name in new[]{"AutostartFeatureSupported","AutostartConfigured","BootChanged","NetCatAutoStarted","ExecutablePathMatches","CandidateIdentityMatches","DesiredStateRecovered","TunHealthy","OpenVpnReady","OriginalSettingRestored","InteractiveLogonObserved"})s[name]=true;
        s["DuplicateCount"]=1;s["ManualLaunchPerformed"]=false;s["TunObservation"]=Healthy;Pass("Autostart",s);s["ManualLaunchPerformed"]=true;Fail("Autostart",s);
    }
    [Fact] public void EvidencePassCannotContainFailedAssertion(){var s=Graceful();s["ProcessKillUsed"]=true;var e=Evaluate("Graceful",s);e["Status"]="ConfirmedPass";Assert.False(VerificationModel.Validate(e));}
    [Fact] public void EvidenceCannotForgeCalculatedMetric(){var e=Evaluate("Soak",Soak());e["Metrics"]!["FatalitiesObserved"]=10;Assert.False(VerificationModel.Validate(e));}
    [Fact] public void LatestTunObservationMustBeRunningAndHealthyOnSameLine(){Assert.False(VerificationModel.AuthoritativeTun(Healthy+"\nTUN_OBSERVE runtimeLifecycle=Failed finalStatus=Weak"));Assert.False(VerificationModel.AuthoritativeTun("TUN_OBSERVE runtimeLifecycle=Running\nTUN_OBSERVE finalStatus=Healthy"));}
    [Fact] public void LifecycleCountersExcludeStdoutAndDeferredActions(){var c=VerificationModel.Lifecycle("OpenVPN: restart\nACTION StartOpenVpn result=deferred\nACTION StartOpenVpn result=ok\nMAIN_ROUTER lifecycle=Stopped -> Starting reason=retry\n");Assert.Equal(1,c["OpenVpn"]!.GetValue<int>());Assert.Equal(1,c["MainSingBox"]!.GetValue<int>());}
    [Fact] public void GracefulRequiresHealthyScenarioBeforeExit(){var s=Graceful();s["InitialHealthy"]=false;Fail("Graceful",s);}
    [Fact] public void CrashCannotPassWithEveryEngineInactive(){var s=Crash();foreach(var r in s["Engines"]!.AsArray())r!["Active"]=false;Fail("Crash",s);}
    [Fact] public void SoakCannotForgeTunBoolean(){var s=Soak();s["Samples"]![19]!["TunObservation"]="TUN_OBSERVE runtimeLifecycle=Starting finalStatus=Healthy";Fail("Soak",s);}
    [Fact] public void EvidenceRoundTripsThroughJson(){var e=Evaluate("Soak",Soak());Assert.True(VerificationModel.Validate(JsonNode.Parse(e.ToJsonString())!.AsObject()));}
    [Fact] public void EvidenceRejectsNegativeNumericMeasurement(){var s=Network();s["StartMainRouterAttemptCount"]=-1;Assert.Throws<InvalidDataException>(()=>Evaluate("Network",s));}
    [Fact] public void LifecycleCountsZapretPidOnce(){var c=VerificationModel.Lifecycle("ZAPRET_PROCESS state=started pid=15\nZAPRET_PROCESS state=started pid=15\nZAPRET_PROCESS state=started pid=16");Assert.Equal(2,c["Zapret"]!.GetValue<int>());}
    [Fact] public void RetryBudgetsUseProductionSchedules(){Assert.Equal(1,VerificationModel.OpenVpnBudget(4));Assert.Equal(2,VerificationModel.OpenVpnBudget(5));Assert.Equal(4,VerificationModel.OpenVpnBudget(300));Assert.Equal(3,VerificationModel.MainBudget(20));}
    [Fact] public void NetworkRecoveryRequiresValidatedCertificate(){var s=Network();s["SiteA"]!["TlsCertificateValid"]=false;Fail("Network",s);}
    [Fact] public void SoakCannotForgeSiteAvailability(){var s=Soak();s["Samples"]![19]!["SiteA"]!["HttpStatus"]=503;Fail("Soak",s);}
    [Fact] public void CrashCounterDeltaMustMatchBeforeAndAfter(){var s=Crash();s["Engines"]![0]!["StartAttemptsAfter"]=9;Fail("Crash",s);}
    [Fact] public void OpenVpnRecoveryRequiresReplacementProcessEvidence(){var s=Crash();s["Engines"]!.AsArray().Single(x=>x!["Role"]!.GetValue<string>()=="OpenVpn")!["OpenVpnProcessRestartCount"]=0;Fail("Crash",s);}
    [Fact] public async Task WindowsCollectorsUseExactOwnershipAndCurrentLogWindow()
    {
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root!=null && !Directory.Exists(Path.Combine(root.FullName,"scripts","verification")))root=root.Parent;
        Assert.NotNull(root);
        var info=new System.Diagnostics.ProcessStartInfo("powershell.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in new[]{"-NoProfile","-ExecutionPolicy","Bypass","-File",Path.Combine(root.FullName,"tests","NetCat.Tests","VerificationCollectors.Tests.ps1"),"-Repository",root.FullName})info.ArgumentList.Add(arg);
        using var child=System.Diagnostics.Process.Start(info)!;
        var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await child.WaitForExitAsync(timeout.Token);
        Assert.True(child.ExitCode==0,(await output)+(await error));
    }
}

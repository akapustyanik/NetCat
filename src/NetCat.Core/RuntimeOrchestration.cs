using System.Text.Json;

namespace NetCat.Core;

public enum StartupRestoreState
{
    Idle,
    Restoring,
    WaitingForNetwork,
    PendingRetry,
    Completed,
    Cancelled
}

public enum ComponentId
{
    MainRouter,
    Tun,
    Zapret,
    OpenVpnLink,
    OpenVpnRoutes,
    OpenVpn = OpenVpnLink
}

public enum DesiredComponentState
{
    Disabled,
    Enabled
}

public enum ObservedComponentState
{
    Stopped,
    Starting,
    RunningHealthy,
    RunningDegraded,
    Failed
}

public enum ConvergenceStatus
{
    Satisfied,
    Pending,
    Backoff
}

public enum ReconcileReason
{
    Startup,
    UserChangedSettings,
    PhysicalNetworkChanged,
    TunStructuralFailure,
    VpnUpstreamChanged,
    OpenVpnStateChanged,
    ZapretStateChanged,
    ManualRetry,
    UserToggledVpn,
    UserToggledTun,
    UserToggledZapret,
    UserToggledOpenVpn,
    UserStopAll,
    VpnFailover,
    UserSelectedVpnProfile,
    ZapretProcessExited,
    ExternalConditionResolved,
    ZapretConfigurationChanged
}

public enum TunStructuralStatus
{
    Healthy,
    Starting,
    TransientDegraded,
    StructuralFailure,
    Unknown
}

// Physical network observation is intentionally tri-state.  A missing first
// snapshot is not the same thing as a completed capture with no usable NIC.
public enum PhysicalNetworkAvailability
{
    Unknown,
    Ready,
    Unavailable
}

public enum MainRouterLifecycle
{
    StoppedByDesired,
    Starting,
    Running,
    Stopping,
    FailedUnexpectedly
}

public sealed record TunHealthState(
    bool ProcessAlive,
    bool InterfacePresent,
    bool RoutesPresent,
    bool LocalDataPathHealthy,
    TunStructuralStatus StructuralStatus,
    string? FailureEvidence = null,
    DateTimeOffset ObservedAt = default
);

public sealed class ComponentRetryState
{
    public int Failures { get; set; }
    public DateTimeOffset NextRetryAt { get; set; }
    public string? LastErrorClass { get; set; }
    public long Generation { get; set; }
    public Guid? RouteOwnerId { get; set; }
}

public sealed record RuntimeConvergenceState(
    bool PhysicalNetworkReady,
    bool DesiredSatisfied,
    IReadOnlyList<ComponentId> PendingComponents,
    string? LastError,
    ConvergencePhase Phase = ConvergencePhase.Starting,
    string? LastBlockingCondition = null,
    PhysicalNetworkAvailability PhysicalNetworkState = PhysicalNetworkAvailability.Unknown
);

public sealed record ObservedRuntimeState(
    ObservedComponentState MainRouterStatus,
    ObservedComponentState ZapretStatus,
    ObservedComponentState OpenVpnStatus,
    bool TunReady,
    NetworkSnapshot? PhysicalNetwork,
    bool PhysicalUsable,
    Guid? ActiveVpnProfileId = null,
    Guid? ActiveOpenVpnProfileId = null,
    string? ActiveZapretStrategy = null,
    bool TunConfigured = false,
    bool? TunObservedHealthy = null,
    bool StructuralTunFailure = false,
    bool? OpenVpnRoutesInstalled = null,
    ZapretObservedState? ZapretDetail = null,
    bool? VpnUpstreamHealthy = null,
    TunHealthState? TunHealth = null,
    TunStructuralStatus TunStatus = TunStructuralStatus.Healthy,
    PhysicalNetworkAvailability? PhysicalNetworkState = null,
    bool OpenVpnGatewayReady = true
);

public enum PlanActionType
{
    WaitForPhysicalNetwork,
    StartZapret,
    StopZapret,
    StartMainRouter,
    EnsureMainRouterForProfileChange,
    EnsureMainRouterForPhysicalBinding,
    StopMainRouter,
    RestartMainRouterForStructuralTunFailure,
    WaitTunReady,
    StartOpenVpn,
    StopOpenVpn,
    FinalizeRouting,
    FinalizeOpenVpnRoutes,
    BackoffWait
}

public sealed record PlanAction(
    PlanActionType Type,
    string Description,
    ComponentId? TargetComponent = null,
    PlanActionType? Prerequisite = null
)
{
    public override string ToString() => Description;
}

public sealed record RuntimePlan(
    IReadOnlyList<PlanAction> Actions,
    IReadOnlyList<ComponentId> PendingRetryComponents,
    TimeSpan NextRetryDelay
)
{
    public static readonly RuntimePlan Empty = new([], [], TimeSpan.Zero);
}

public sealed record EffectiveRuntimeConfig(
    bool VpnEnabled,
    bool TunEnabled,
    Guid? VpnProfileId,
    bool ZapretEnabled,
    string ZapretStrategy,
    string Scenario,
    bool OpenVpnEnabled,
    Guid? OpenVpnProfileId,
    string LearnedOpenVpnRoutesFingerprint,
    string RoutingRulesFingerprint,
    string DnsPolicyFingerprint,
    string PhysicalBindingFingerprint,
    AppSettings? TargetSettings = null
)
{
    public static readonly EffectiveRuntimeConfig Empty = new(
        false, false, null, false, "", "", false, null, "", "", "", ""
    );
}

public sealed record RuntimeSettingsDiff(
    bool VpnChanged,
    bool TunChanged,
    bool VpnProfileChanged,
    bool OpenVpnChanged,
    bool OpenVpnProfileChanged,
    bool RoutingRulesChanged,
    bool LearnedOpenVpnRoutesChanged,
    bool ZapretChanged,
    bool ZapretProfileChanged,
    bool DnsPolicyChanged,
    bool PhysicalBindingChanged,
    bool MainRouterBaseChanged = false,
    bool RoutingOverlayChanged = false
)
{
    public bool OpenVpnRoutesChanged => LearnedOpenVpnRoutesChanged || OpenVpnChanged || OpenVpnProfileChanged;

    public bool HasRouterChanges =>
        VpnChanged || TunChanged || VpnProfileChanged || RoutingRulesChanged || DnsPolicyChanged || PhysicalBindingChanged || MainRouterBaseChanged;

    public static RuntimeSettingsDiff Compute(AppSettings old, AppSettings next, NetworkSnapshot? oldPhysical, NetworkSnapshot? nextPhysical)
    {
        bool vpnChanged = old.MainProfileId != next.MainProfileId;
        bool tunChanged = old.Tun != next.Tun;
        bool physicalBindingChanged = old.PhysicalInterface != next.PhysicalInterface ||
            (oldPhysical != null && nextPhysical != null &&
            (oldPhysical.Index != nextPhysical.Index || oldPhysical.Address != nextPhysical.Address || oldPhysical.Name != nextPhysical.Name));
        bool zapretChanged = old.ZapretStrategy != next.ZapretStrategy || old.Scenario != next.Scenario || old.YouTube != next.YouTube || old.Discord != next.Discord;
        bool openVpnChanged = old.OpenVpnProfileId != next.OpenVpnProfileId || old.OpenVpnDns != next.OpenVpnDns;
        bool dnsChanged = old.DirectDns != next.DirectDns;

        string oldRules = JsonSerializer.Serialize(old.Rules, JsonSettings.Options);
        string nextRules = JsonSerializer.Serialize(next.Rules, JsonSettings.Options);
        bool rulesChanged = oldRules != nextRules || old.Mode != next.Mode ||
            old.TelegramSocks != next.TelegramSocks || old.TelegramVpnDefault != next.TelegramVpnDefault;

        bool baseChanged = vpnChanged || tunChanged || physicalBindingChanged || dnsChanged || rulesChanged;

        return new RuntimeSettingsDiff(
            VpnChanged: vpnChanged,
            TunChanged: tunChanged,
            VpnProfileChanged: vpnChanged,
            OpenVpnChanged: openVpnChanged,
            OpenVpnProfileChanged: old.OpenVpnProfileId != next.OpenVpnProfileId,
            RoutingRulesChanged: rulesChanged,
            LearnedOpenVpnRoutesChanged: false,
            ZapretChanged: zapretChanged,
            ZapretProfileChanged: old.ZapretStrategy != next.ZapretStrategy,
            DnsPolicyChanged: dnsChanged,
            PhysicalBindingChanged: physicalBindingChanged,
            MainRouterBaseChanged: baseChanged,
            RoutingOverlayChanged: false
        );
    }

    public static RuntimeSettingsDiff Compute(
        EffectiveRuntimeConfig? lastConfig,
        EffectiveRuntimeConfig effective,
        NetworkSnapshot? physical)
    {
        lastConfig ??= EffectiveRuntimeConfig.Empty;

        bool vpnChanged = lastConfig.VpnEnabled != effective.VpnEnabled;
        bool tunChanged = lastConfig.TunEnabled != effective.TunEnabled;
        bool vpnProfileChanged = lastConfig.VpnProfileId != effective.VpnProfileId;
        bool openVpnChanged = lastConfig.OpenVpnEnabled != effective.OpenVpnEnabled;
        bool openVpnProfileChanged = lastConfig.OpenVpnProfileId != effective.OpenVpnProfileId;
        bool rulesChanged = lastConfig.RoutingRulesFingerprint != effective.RoutingRulesFingerprint;
        bool learnedRoutesChanged = lastConfig.LearnedOpenVpnRoutesFingerprint != effective.LearnedOpenVpnRoutesFingerprint;
        bool zapretChanged = lastConfig.ZapretEnabled != effective.ZapretEnabled;
        bool zapretProfileChanged = lastConfig.ZapretStrategy != effective.ZapretStrategy || lastConfig.Scenario != effective.Scenario;
        bool dnsChanged = lastConfig.DnsPolicyFingerprint != effective.DnsPolicyFingerprint;
        bool physicalBindingChanged = lastConfig.PhysicalBindingFingerprint != effective.PhysicalBindingFingerprint;

        bool baseChanged = vpnChanged || tunChanged || vpnProfileChanged || rulesChanged || dnsChanged || physicalBindingChanged;
        bool overlayChanged = learnedRoutesChanged || openVpnChanged || openVpnProfileChanged;

        return new RuntimeSettingsDiff(
            VpnChanged: vpnChanged,
            TunChanged: tunChanged,
            VpnProfileChanged: vpnProfileChanged,
            OpenVpnChanged: openVpnChanged,
            OpenVpnProfileChanged: openVpnProfileChanged,
            RoutingRulesChanged: rulesChanged,
            LearnedOpenVpnRoutesChanged: learnedRoutesChanged,
            ZapretChanged: zapretChanged || zapretProfileChanged,
            ZapretProfileChanged: zapretProfileChanged,
            DnsPolicyChanged: dnsChanged,
            PhysicalBindingChanged: physicalBindingChanged,
            MainRouterBaseChanged: baseChanged,
            RoutingOverlayChanged: overlayChanged
        );
    }

}

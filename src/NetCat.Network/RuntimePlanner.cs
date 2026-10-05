using NetCat.Core;

namespace NetCat.Network;

public static class RuntimePlanner
{
    public static readonly TimeSpan[] DefaultBackoffIntervals =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromMinutes(5)
    ];

    public static TimeSpan GetBackoff(int attempt, TimeSpan[]? intervals = null)
    {
        intervals ??= DefaultBackoffIntervals;
        if (intervals.Length == 0) return TimeSpan.Zero;
        int idx = Math.Max(0, attempt - 1);
        return idx < intervals.Length ? intervals[idx] : intervals[^1];
    }

    public static TimeSpan ComputeBackoff(int attempt, TimeSpan[]? intervals = null) => GetBackoff(attempt, intervals);

    public static RuntimePlan CreatePlan(
        DesiredRuntimeState desired,
        ObservedRuntimeState observed,
        AppSettings settings,
        IReadOnlyDictionary<ComponentId, int> failureCounts,
        TimeSpan[]? backoffIntervals = null,
        RuntimeSettingsDiff? diff = null,
        bool canStartOpenVpn = true,
        TimeSpan? openVpnRetryDelay = null)
    {
        var actions = new List<PlanAction>();
        var pendingComponents = new List<ComponentId>();

        bool anyDesired = desired.RouterEnabled || desired.ZapretEnabled || desired.OpenVpnEnabled;
        if (observed.PhysicalNetworkState == PhysicalNetworkAvailability.Unknown) return RuntimePlan.Empty;

        // 1. Physical Network Gate
        if (anyDesired && !observed.PhysicalUsable)
        {
            if (!desired.ZapretEnabled && observed.ZapretStatus != ObservedComponentState.Stopped)
                actions.Add(new(PlanActionType.StopZapret, "Остановка Zapret", ComponentId.Zapret));
            if (!desired.OpenVpnEnabled && observed.OpenVpnStatus != ObservedComponentState.Stopped)
                actions.Add(new(PlanActionType.StopOpenVpn, "Остановка OpenVPN", ComponentId.OpenVpnLink));
            if (!desired.RouterEnabled && observed.MainRouterStatus != ObservedComponentState.Stopped)
                actions.Add(new(PlanActionType.StopMainRouter, "Остановка VPN", ComponentId.MainRouter));
            actions.Add(new PlanAction(PlanActionType.WaitForPhysicalNetwork, "Ожидание физической сети"));
            return new RuntimePlan(actions, pendingComponents, TimeSpan.Zero);
        }

        // 2. Zapret
        if (desired.ZapretEnabled)
        {
            bool zapretRebindNeeded = (diff != null && diff.PhysicalBindingChanged) ||
                (observed.ZapretDetail != null && observed.PhysicalNetwork != null &&
                 observed.ZapretDetail.BoundPhysicalInterfaceIndex > 0 &&
                 observed.ZapretDetail.BoundPhysicalInterfaceIndex != observed.PhysicalNetwork.Index);

            bool needStart = observed.ZapretStatus != ObservedComponentState.RunningHealthy ||
                             (diff != null && diff.ZapretProfileChanged) ||
                             zapretRebindNeeded;
            if (needStart)
            {
                actions.Add(new PlanAction(PlanActionType.StartZapret, "Запуск Zapret", ComponentId.Zapret));
            }
        }
        else
        {
            if (observed.ZapretStatus != ObservedComponentState.Stopped || failureCounts.GetValueOrDefault(ComponentId.Zapret) > 0)
            {
                actions.Add(new PlanAction(PlanActionType.StopZapret, "Остановка Zapret", ComponentId.Zapret));
            }
        }

        // 3. Main Router / TUN
        if (desired.RouterEnabled)
        {
            bool profileMismatch = desired.MainVpnEnabled && desired.SelectedVpnProfileId.HasValue &&
                                    desired.SelectedVpnProfileId != observed.ActiveVpnProfileId;

            bool profileChanged = profileMismatch || (diff != null && diff.VpnProfileChanged);
            bool physicalChanged = diff != null && diff.PhysicalBindingChanged;
            bool baseConfigChanged = diff != null && (diff.MainRouterBaseChanged || diff.TunChanged || diff.RoutingRulesChanged || diff.DnsPolicyChanged);

            if (desired.TunEnabled && observed.StructuralTunFailure && observed.MainRouterStatus != ObservedComponentState.Stopped)
            {
                // Structural TUN failure requires full router restart and TUN readiness verification
                actions.Add(new PlanAction(PlanActionType.RestartMainRouterForStructuralTunFailure, "Перезапуск маршрутизатора для восстановления TUN", ComponentId.Tun));
                actions.Add(new PlanAction(PlanActionType.WaitTunReady, "Ожидание готовности TUN", ComponentId.Tun, Prerequisite: PlanActionType.RestartMainRouterForStructuralTunFailure));
            }
            else if (observed.MainRouterStatus != ObservedComponentState.RunningHealthy)
            {
                actions.Add(new PlanAction(PlanActionType.StartMainRouter, "Запуск основного VPN-маршрутизатора", ComponentId.MainRouter));
                if (desired.TunEnabled)
                {
                    actions.Add(new PlanAction(PlanActionType.WaitTunReady, "Ожидание готовности TUN", ComponentId.Tun, Prerequisite: PlanActionType.StartMainRouter));
                }
            }
            else if (profileChanged)
            {
                actions.Add(new PlanAction(PlanActionType.EnsureMainRouterForProfileChange, "Смена профиля основного VPN", ComponentId.MainRouter));
                if (desired.TunEnabled)
                {
                    actions.Add(new PlanAction(PlanActionType.WaitTunReady, "Ожидание готовности TUN", ComponentId.Tun, Prerequisite: PlanActionType.EnsureMainRouterForProfileChange));
                }
            }
            else if (physicalChanged)
            {
                actions.Add(new PlanAction(PlanActionType.EnsureMainRouterForPhysicalBinding, "Привязка VPN к новому физическому адаптеру", ComponentId.MainRouter));
                if (desired.TunEnabled)
                {
                    actions.Add(new PlanAction(PlanActionType.WaitTunReady, "Ожидание готовности TUN", ComponentId.Tun, Prerequisite: PlanActionType.EnsureMainRouterForPhysicalBinding));
                }
            }
            else if (baseConfigChanged)
            {
                actions.Add(new PlanAction(PlanActionType.StartMainRouter, "Перезапуск основного VPN-маршрутизатора", ComponentId.MainRouter));
                if (desired.TunEnabled)
                {
                    actions.Add(new PlanAction(PlanActionType.WaitTunReady, "Ожидание готовности TUN", ComponentId.Tun, Prerequisite: PlanActionType.StartMainRouter));
                }
            }
            else if (desired.TunEnabled && observed.TunStatus is not (TunStructuralStatus.TransientDegraded or TunStructuralStatus.Unknown) && (!(observed.TunObservedHealthy ?? observed.TunReady) || !observed.TunReady))
            {
                actions.Add(new PlanAction(PlanActionType.WaitTunReady, "Ожидание готовности TUN", ComponentId.Tun));
            }
        }
        else
        {
            if (observed.MainRouterStatus != ObservedComponentState.Stopped || failureCounts.GetValueOrDefault(ComponentId.MainRouter) > 0)
            {
                actions.Add(new PlanAction(PlanActionType.StopMainRouter, "Остановка основного VPN-маршрутизатора", ComponentId.MainRouter));
            }
        }

        // 4. OpenVPN
        if (desired.OpenVpnEnabled)
        {
            bool profileMismatch = desired.SelectedOpenVpnProfileId.HasValue &&
                                    observed.ActiveOpenVpnProfileId.HasValue &&
                                    desired.SelectedOpenVpnProfileId.Value != observed.ActiveOpenVpnProfileId.Value;

            bool needStart = observed.OpenVpnStatus is not (ObservedComponentState.RunningHealthy or ObservedComponentState.Starting) || profileMismatch;

            if (needStart)
            {
                if (profileMismatch) actions.Add(new(PlanActionType.StopOpenVpn, "Смена профиля OpenVPN: остановка предыдущего", ComponentId.OpenVpnLink));
                if (canStartOpenVpn)
                {
                    actions.Add(new PlanAction(PlanActionType.StartOpenVpn, "Запуск OpenVPN", ComponentId.OpenVpnLink,
                        Prerequisite: profileMismatch ? PlanActionType.StopOpenVpn : null));
                    actions.Add(new PlanAction(PlanActionType.FinalizeRouting, "Финализация маршрутов OpenVPN", ComponentId.OpenVpnRoutes, Prerequisite: PlanActionType.StartOpenVpn));
                }
                else
                {
                    pendingComponents.Add(ComponentId.OpenVpnLink);
                }
            }
            else if (observed.OpenVpnStatus != ObservedComponentState.Starting && (observed.OpenVpnRoutesInstalled != true || (diff != null && (diff.LearnedOpenVpnRoutesChanged || !desired.RouterEnabled && diff.HasRouterChanges)) || failureCounts.GetValueOrDefault(ComponentId.OpenVpnRoutes) > 0))
            {
                actions.Add(new PlanAction(PlanActionType.FinalizeRouting, "Финализация маршрутов OpenVPN", ComponentId.OpenVpnRoutes));
            }
        }
        else
        {
            if (observed.OpenVpnStatus != ObservedComponentState.Stopped || failureCounts.GetValueOrDefault(ComponentId.OpenVpnLink) > 0)
            {
                actions.Add(new PlanAction(PlanActionType.StopOpenVpn, "Остановка OpenVPN", ComponentId.OpenVpnLink));
                actions.Add(new PlanAction(PlanActionType.FinalizeRouting, "Удаление маршрутов OpenVPN", ComponentId.OpenVpnRoutes, Prerequisite: PlanActionType.StopOpenVpn));
            }
            else if (!observed.OpenVpnGatewayReady || observed.MainRouterStatus == ObservedComponentState.RunningHealthy && diff?.LearnedOpenVpnRoutesChanged == true || failureCounts.GetValueOrDefault(ComponentId.OpenVpnRoutes) > 0)
                actions.Add(new(PlanActionType.FinalizeRouting, "Обновление закрытого шлюза OpenVPN", ComponentId.OpenVpnRoutes));
        }

        // Link establishment is independent of the main router. Do not fold
        // OpenVPN finalization into a router start: doing so makes link timeout
        // a prerequisite failure for otherwise healthy Main VPN/TUN startup.
        // Only the OpenVPN overlay depends on its link action succeeding.

        // Calculate pending retry components & backoff
        TimeSpan minBackoff = TimeSpan.MaxValue;
        foreach (var (comp, count) in failureCounts)
        {
            if (count > 0)
            {
                bool isUnsatisfied = comp switch
                {
                    ComponentId.Zapret => (desired.ZapretEnabled && observed.ZapretStatus != ObservedComponentState.RunningHealthy) ||
                                          (!desired.ZapretEnabled && observed.ZapretStatus != ObservedComponentState.Stopped),
                    ComponentId.MainRouter => (desired.RouterEnabled && observed.MainRouterStatus != ObservedComponentState.RunningHealthy) ||
                                              (!desired.RouterEnabled && observed.MainRouterStatus != ObservedComponentState.Stopped),
                    ComponentId.Tun => desired.RouterEnabled && desired.TunEnabled && (!(observed.TunObservedHealthy ?? observed.TunReady) || observed.StructuralTunFailure),
                    ComponentId.OpenVpnLink => (desired.OpenVpnEnabled && observed.OpenVpnStatus != ObservedComponentState.RunningHealthy) ||
                                               (!desired.OpenVpnEnabled && observed.OpenVpnStatus != ObservedComponentState.Stopped),
                    ComponentId.OpenVpnRoutes => (desired.OpenVpnEnabled && observed.OpenVpnRoutesInstalled != true) ||
                                                 (!desired.OpenVpnEnabled && observed.OpenVpnStatus != ObservedComponentState.Stopped),
                    _ => false
                };

                if (isUnsatisfied)
                {
                    if (!pendingComponents.Contains(comp)) pendingComponents.Add(comp);
                    var delay = GetBackoff(count, backoffIntervals);
                    if (delay < minBackoff) minBackoff = delay;
                }
            }
        }

        if (openVpnRetryDelay.HasValue && openVpnRetryDelay.Value > TimeSpan.Zero)
        {
            if (openVpnRetryDelay.Value < minBackoff) minBackoff = openVpnRetryDelay.Value;
        }

        if (minBackoff == TimeSpan.MaxValue) minBackoff = TimeSpan.Zero;

        return new RuntimePlan(actions, pendingComponents, minBackoff);
    }
}

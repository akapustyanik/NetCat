using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NetCat.Core.Enums;
using NetCat.Core.Models;

namespace NetCat.Core.Interfaces
{
    public interface IEngineService
    {
        bool IsRunning { get; }
        Task InitializeAsync();
        Task StartRoutingAsync(ProxyProfile primary, ProxyProfile? backup = null);
        Task StopRoutingAsync();
        Task RestartWithConfigAsync(string configPath);
        event EventHandler<string>? OnEngineLogReceived;
        event EventHandler<NodeStatus>? OnStatusChanged;
    }

    public interface IRoutingService
    {
        IReadOnlyList<RoutingRule> GetRules();
        Task AddRuleAsync(RoutingRule rule);
        Task UpdateRuleAsync(RoutingRule rule);
        Task DeleteRuleAsync(Guid id);
        Task ApplyRulesAsync(RoutingMode mode);
    }

    public interface IFailoverEngine
    {
        void RegisterNodes(ProxyProfile primary, ProxyProfile? backup);
        Task ExecuteFastSwitchAsync();
        Task StartMonitorAsync(CancellationToken cancellationToken);
        void StopMonitor();
        event EventHandler<ProxyProfile>? OnActiveNodeChanged;
    }

    public interface IHealthChecker
    {
        Task<LatencyResult> TestNodeRealDelayAsync(ProxyProfile node, int timeoutMs = 2500);
        Task<int> TestTcpRttAsync(string host, int port, int timeoutMs = 1500);
    }

    public interface IUpdateManager
    {
        Task<ManifestModel> CheckForUpdatesAsync();
        Task<bool> UpdateComponentAsync(string componentKey, IProgress<double>? progress = null);
        Task RollbackComponentAsync(string componentKey);
    }
}

using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using NetCat.Core.Enums;
using NetCat.Core.Interfaces;
using NetCat.Core.Models;
using NetCat.Network.Metrics;

namespace NetCat.Network.Failover
{
    public class RacingStreamRouter
    {
        public static async Task<ProxyProfile?> RaceNodesAsync(ProxyProfile nodeA, ProxyProfile nodeB, int timeoutMs = 2000)
        {
            var tester = new RealDelayTester();
            var taskA = tester.TestTcpRttAsync(nodeA.ServerAddress, nodeA.ServerPort, timeoutMs);
            var taskB = tester.TestTcpRttAsync(nodeB.ServerAddress, nodeB.ServerPort, timeoutMs);

            var firstCompleted = await Task.WhenAny(taskA, taskB);
            int rttA = await taskA;
            int rttB = await taskB;

            nodeA.LastMetrics.TcpPingMs = rttA;
            nodeB.LastMetrics.TcpPingMs = rttB;

            if (rttA > 0 && (rttB <= 0 || rttA <= rttB))
            {
                return nodeA;
            }
            if (rttB > 0)
            {
                return nodeB;
            }

            return rttA > 0 ? nodeA : null;
        }
    }

    public class DualPathFailoverEngine : IFailoverEngine, IDisposable
    {
        private ProxyProfile? _primary;
        private ProxyProfile? _backup;
        private ProxyProfile? _activeNode;
        private CancellationTokenSource? _monitorCts;
        private readonly IHealthChecker _healthChecker;
        private int _consecutiveLosses = 0;
        private const int ConsecutiveThreshold = 2;

        public event EventHandler<ProxyProfile>? OnActiveNodeChanged;
        public ProxyProfile? ActiveNode => _activeNode;

        public DualPathFailoverEngine(IHealthChecker? healthChecker = null)
        {
            _healthChecker = healthChecker ?? new RealDelayTester();
        }

        public void RegisterNodes(ProxyProfile primary, ProxyProfile? backup)
        {
            _primary = primary;
            _backup = backup;
            _activeNode = primary;
            _consecutiveLosses = 0;
            OnActiveNodeChanged?.Invoke(this, _activeNode);
        }

        public async Task ExecuteFastSwitchAsync()
        {
            if (_backup == null || _activeNode == _backup) return;

            Debug.WriteLine($"[Failover] Switching from {_primary?.Name} to {_backup?.Name}");
            _activeNode = _backup;
            if (_activeNode != null)
            {
                _activeNode.Status = NodeStatus.Active;
            }
            if (_primary != null) _primary.Status = NodeStatus.Degraded;

            if (_activeNode != null)
            {
                OnActiveNodeChanged?.Invoke(this, _activeNode);
            }
            await Task.CompletedTask;
        }

        public Task StartMonitorAsync(CancellationToken cancellationToken)
        {
            StopMonitor();
            _monitorCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var token = _monitorCts.Token;

            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await Task.Delay(5000, token);

                        if (_activeNode != null)
                        {
                            var metrics = await _healthChecker.TestNodeRealDelayAsync(_activeNode, 2500);
                            _activeNode.LastMetrics = metrics;

                            if (!metrics.IsSuccess)
                            {
                                _consecutiveLosses++;
                                Debug.WriteLine($"[Failover] Node {_activeNode.Name} probe failed ({_consecutiveLosses}/{ConsecutiveThreshold})");

                                if (_consecutiveLosses >= ConsecutiveThreshold && _backup != null && _activeNode != _backup)
                                {
                                    await ExecuteFastSwitchAsync();
                                }
                            }
                            else
                            {
                                _consecutiveLosses = 0;
                                _activeNode.Status = NodeStatus.Active;
                            }
                        }

                        // Warm up standby backup node
                        if (_backup != null && _activeNode != _backup)
                        {
                            var backupMetrics = await _healthChecker.TestNodeRealDelayAsync(_backup, 2500);
                            _backup.LastMetrics = backupMetrics;
                            _backup.Status = backupMetrics.IsSuccess ? NodeStatus.Standby : NodeStatus.Dead;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[Failover] Monitor error: {ex.Message}");
                    }
                }
            }, token);

            return Task.CompletedTask;
        }

        public void StopMonitor()
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
            _monitorCts = null;
        }

        public void Dispose()
        {
            StopMonitor();
        }
    }
}

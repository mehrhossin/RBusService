// ============================================================
// RBusService.cs
// ارکستراتور اصلی — مدیریت bridge‌ها، polling، discovery
// معادل: service.py
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using static RBusService.Core.RBusProtocol;

namespace RBusService.Core
{
    public class RBusService : IDisposable
    {
        public NodeRegistry Registry { get; } = new();

        private readonly Dictionary<string, BridgeWorker> _bridges = new();
        private readonly object _bridgesLock = new();

        private readonly Action<string, Dictionary<string, object>> _onEvent;

        // ── round-robin polling ───────────────────────────────
        private readonly Dictionary<string, int> _rrIndex = new();
        private readonly Dictionary<string, double> _pollPauseUntil = new();
        private const double POLL_PAUSE_DURATION = 0.150; // 150ms

        private volatile bool _alive = true;
        private readonly Thread _pollThread;

        public RBusService(Action<string, Dictionary<string, object>> onEvent)
        {
            _onEvent = onEvent;
            _pollThread = new Thread(PollLoop)
            {
                Name = "RBusPoller",
                IsBackground = true,
            };
            _pollThread.Start();
        }

        // ── مدیریت bridge‌ها ──────────────────────────────────
        public bool AddBridge(string bridgeId, string host, int port)
        {
            lock (_bridgesLock)
            {
                if (_bridges.ContainsKey(bridgeId)) return false;
                var worker = new BridgeWorker(bridgeId, host, port, Registry, OnEvent);
                _bridges[bridgeId] = worker;
                worker.Start();
                return true;
            }
        }

        public bool ConnectBridge(string bridgeId)
        {
            lock (_bridgesLock)
            {
                if (!_bridges.TryGetValue(bridgeId, out var w)) return false;
                return w.Connect();
            }
        }

        public void DisconnectBridge(string bridgeId)
        {
            lock (_bridgesLock)
            {
                if (_bridges.TryGetValue(bridgeId, out var w))
                    w.Disconnect();
            }
        }

        public void RemoveBridge(string bridgeId)
        {
            lock (_bridgesLock)
            {
                if (!_bridges.TryGetValue(bridgeId, out var w)) return;
                w.Stop();
                w.Disconnect();
                _bridges.Remove(bridgeId);
            }
        }

        // ── ارسال دستور به نود ───────────────────────────────
        public bool SendCmd(string bridgeId, byte addr, byte cmd,
            byte[]? data = null, string tag = "",
            int priority = TX_PRIORITY_GAME)
        {
            BridgeWorker? worker;
            lock (_bridgesLock)
                _bridges.TryGetValue(bridgeId, out worker);

            if (worker == null || !worker.IsConnected) return false;

            // توقف موقت polling برای این bridge
            lock (_pollPauseUntil)
                _pollPauseUntil[bridgeId] = UnixNow() + POLL_PAUSE_DURATION;

            return worker.Send(addr, cmd, data, tag, true, priority);
        }

        // ── Broadcast به همه bridge‌ها ────────────────────────
        public void BroadcastAll(byte cmd, byte[]? data = null)
        {
            List<BridgeWorker> workers;
            lock (_bridgesLock)
                workers = _bridges.Values.ToList();

            foreach (var w in workers)
                if (w.IsConnected)
                    w.Send(ADDR_BROADCAST, cmd, data, "", false, TX_PRIORITY_GAME);
        }

        // ── Discovery ────────────────────────────────────────
        public void BroadcastDiscovery(string bridgeId)
        {
            BridgeWorker? worker;
            lock (_bridgesLock)
                _bridges.TryGetValue(bridgeId, out worker);

            worker?.Send(ADDR_BROADCAST, CMD_DISCOVERY_REQ,
                priority: TX_PRIORITY_SYS);
        }

        // ── آدرس‌دهی نود ─────────────────────────────────────
        public bool AssignAddress(string bridgeId, string mac, byte newAddr)
        {
            BridgeWorker? worker;
            lock (_bridgesLock)
                _bridges.TryGetValue(bridgeId, out worker);

            if (worker == null) return false;

            byte[] macBytes = RBusProtocol.MacBytesFromStr(mac);
            byte[] payload = new byte[7];
            macBytes.CopyTo(payload, 0);
            payload[6] = newAddr;

            return worker.Send(ADDR_BROADCAST, CMD_SET_ADDRESS,
                payload, priority: TX_PRIORITY_SYS);
        }

        // ── حلقه Polling و Discovery ─────────────────────────
        private void PollLoop()
        {
            double lastDiscovery = 0.0;

            while (_alive)
            {
                double now = UnixNow();

                // ── Discovery هر 10 ثانیه ────────────────────
                if (now - lastDiscovery >= DISCOVERY_INTERVAL_S)
                {
                    lastDiscovery = now;
                    List<BridgeWorker> workers;
                    lock (_bridgesLock)
                        workers = _bridges.Values.ToList();

                    foreach (var w in workers)
                        if (w.IsConnected)
                            w.Send(ADDR_BROADCAST, CMD_DISCOVERY_REQ,
                                priority: TX_PRIORITY_SYS);
                }

                // ── Polling round-robin ───────────────────────
                List<(string id, BridgeWorker w)> bridges;
                lock (_bridgesLock)
                    bridges = _bridges.Select(kv => (kv.Key, kv.Value)).ToList();

                foreach (var (bid, worker) in bridges)
                {
                    if (!worker.IsConnected) continue;

                    // بررسی توقف موقت
                    lock (_pollPauseUntil)
                    {
                        if (_pollPauseUntil.TryGetValue(bid, out double until)
                            && UnixNow() < until)
                            continue;
                    }

                    var nodes = Registry.GetByBridge(bid)
                        .Where(n => n.Addr != 0 && n.HasCode && !n.InOta)
                        .ToList();

                    if (nodes.Count == 0) continue;

                    if (!_rrIndex.TryGetValue(bid, out int idx))
                        idx = 0;

                    idx %= nodes.Count;
                    var node = nodes[idx];
                    _rrIndex[bid] = (idx + 1) % nodes.Count;

                    worker.Send((byte)node.Addr, CMD_GET_STATUS,
                        priority: TX_PRIORITY_POLL);
                }

                Thread.Sleep((int)(POLL_INTERVAL_S * 1000));
            }
        }

        private void OnEvent(string kind, Dictionary<string, object> data)
            => _onEvent(kind, data);

        private static double UnixNow()
            => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        public void Dispose()
        {
            _alive = false;
            lock (_bridgesLock)
            {
                foreach (var w in _bridges.Values)
                {
                    w.Stop();
                    w.Disconnect();
                }
                _bridges.Clear();
            }
        }
    }
}

// ============================================================
// NodeRegistry.cs
// رجیستری thread-safe تمام نودهای شناخته‌شده
// معادل: core/node_registry.py
// ============================================================
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace RBusService.Core
{
    public class NodeInfo
    {
        public string Mac { get; set; } = "";
        public string BridgeId { get; set; } = "";
        public int Addr { get; set; } = 0;
        public bool HasCode { get; set; } = false;
        public bool InOta { get; set; } = false;
        public int TouchMask { get; set; } = 0;
        public int Adc { get; set; } = 0;
        public int FwMajor { get; set; } = 0;
        public int FwMinor { get; set; } = 0;
        public double LastSeen { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        public bool DupAddr { get; set; } = false;

        public string Status
        {
            get
            {
                if (InOta) return "OTA";
                if (DupAddr) return "DUP_ADDR";
                if (!HasCode) return "RAW";
                if (Addr == 0) return "NO_ADDR";
                return "ONLINE";
            }
        }

        public string AddrStr => Addr != 0 ? $"0x{Addr:X2}" : "-";
        public string FwStr => (FwMajor != 0 || FwMinor != 0) ? $"v{FwMajor}.{FwMinor}" : "";
    }

    public class NodeRegistry
    {
        private readonly Dictionary<string, NodeInfo> _nodes = new();
        private readonly ReaderWriterLockSlim _lock = new();

        private void RecomputeDups()
        {
            // باید داخل write lock فراخوانی شود
            var seen = new Dictionary<(string, int), List<string>>();
            foreach (var (mac, n) in _nodes)
            {
                n.DupAddr = false;
                if (n.Addr != 0)
                {
                    var key = (n.BridgeId, n.Addr);
                    if (!seen.ContainsKey(key)) seen[key] = new();
                    seen[key].Add(mac);
                }
            }
            foreach (var macs in seen.Values.Where(m => m.Count > 1))
                foreach (var m in macs)
                    _nodes[m].DupAddr = true;
        }

        public NodeInfo UpsertDiscovery(string mac, string bridgeId, int addr, bool hasCode)
        {
            _lock.EnterWriteLock();
            try
            {
                if (!_nodes.TryGetValue(mac, out var n))
                    _nodes[mac] = n = new NodeInfo { Mac = mac, BridgeId = bridgeId };
                n.BridgeId = bridgeId;
                n.Addr = addr;
                n.HasCode = hasCode;
                n.LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                RecomputeDups();
                return n;
            }
            finally { _lock.ExitWriteLock(); }
        }

        public NodeInfo UpsertAddrAck(string mac, string bridgeId, int addr)
        {
            _lock.EnterWriteLock();
            try
            {
                if (!_nodes.TryGetValue(mac, out var n))
                    _nodes[mac] = n = new NodeInfo { Mac = mac, BridgeId = bridgeId };
                n.BridgeId = bridgeId;
                n.Addr = addr;
                n.HasCode = true;
                n.LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                RecomputeDups();
                return n;
            }
            finally { _lock.ExitWriteLock(); }
        }

        public NodeInfo? UpsertStatus(string mac, string bridgeId, int addr,
            bool inOta, int touchMask, int adc, int fwMajor, int fwMinor)
        {
            _lock.EnterWriteLock();
            try
            {
                if (!_nodes.TryGetValue(mac, out var n)) return null;
                n.BridgeId = bridgeId;
                n.Addr = addr;
                n.InOta = inOta;
                n.TouchMask = touchMask;
                n.Adc = adc;
                n.FwMajor = fwMajor;
                n.FwMinor = fwMinor;
                n.LastSeen = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
                return n;
            }
            finally { _lock.ExitWriteLock(); }
        }

        public void SetOtaFlag(string mac, bool value)
        {
            _lock.EnterWriteLock();
            try { if (_nodes.TryGetValue(mac, out var n)) n.InOta = value; }
            finally { _lock.ExitWriteLock(); }
        }

        public NodeInfo? GetByMac(string mac)
        {
            _lock.EnterReadLock();
            try { return _nodes.TryGetValue(mac, out var n) ? n : null; }
            finally { _lock.ExitReadLock(); }
        }

        public NodeInfo? GetByAddr(string bridgeId, int addr)
        {
            _lock.EnterReadLock();
            try
            {
                return _nodes.Values
                    .FirstOrDefault(n => n.BridgeId == bridgeId && n.Addr == addr);
            }
            finally { _lock.ExitReadLock(); }
        }

        public List<NodeInfo> GetAll()
        {
            _lock.EnterReadLock();
            try { return _nodes.Values.ToList(); }
            finally { _lock.ExitReadLock(); }
        }

        public List<NodeInfo> GetByBridge(string bridgeId)
        {
            _lock.EnterReadLock();
            try
            {
                return _nodes.Values
                    .Where(n => n.BridgeId == bridgeId)
                    .ToList();
            }
            finally { _lock.ExitReadLock(); }
        }
    }
}

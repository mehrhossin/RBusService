// ============================================================
// BridgeWorker.cs
// یک thread برای هر مبدل TCP↔RS485 (USR-DR134)
// معادل: core/bridge_worker.py
// ============================================================
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using static RBusService.Core.RBusProtocol;

namespace RBusService.Core
{
    // آیتم صف TX با اولویت
    internal record TxItem(
        int Priority, long Seq,
        byte Addr, byte Cmd,
        byte[] Data, string Tag,
        bool ExpectFeedback
    ) : IComparable<TxItem>
    {
        public int CompareTo(TxItem? other)
        {
            if (other == null) return 1;
            int c = Priority.CompareTo(other.Priority);
            return c != 0 ? c : Seq.CompareTo(other.Seq);
        }
    }

    public class BridgeWorker
    {
        // ── شناسه و endpoint ─────────────────────────────────
        public string BridgeId { get; }
        public string Host { get; private set; }
        public int Port { get; private set; }

        // ── وابستگی‌ها ────────────────────────────────────────
        private readonly NodeRegistry _registry;
        private readonly Action<string, Dictionary<string, object>> _onEvent;

        // ── لایه TCP ─────────────────────────────────────────
        private readonly TcpLink _link = new();
        private byte[] _rxBuf = Array.Empty<byte>();

        // ── صف TX با اولویت ──────────────────────────────────
        // از PriorityQueue داخلی C# استفاده می‌کنیم (thread-safe wrapper)
        private readonly object _queueLock = new();
        private readonly SortedSet<TxItem> _txQueue = new();
        private long _txSeq = 0;

        // ── تایمینگ TX ────────────────────────────────────────
        // @ 115200: فریم 22 بایت = 1.9ms → 2ms کافیه
        private const double TX_INTER_FRAME_DELAY_S = 0.002;  // 10ms → 2ms
        private const double RX_TO_TX_TURNAROUND_S = 0.002;  // 10ms → 2ms
        private const double LOOP_SLEEP_S = 0.0001; // 0.5ms → 0.1ms

        private double _lastRxTime = 0.0;

        // ── کنترل thread ─────────────────────────────────────
        private volatile bool _alive = true;
        private volatile bool _wantConnected = false;
        private double _reconnectAt = 0.0;
        private Thread? _thread;

        // ── نگاشت آدرس ───────────────────────────────────────
        private readonly Dictionary<int, string> _addrToMac = new();
        private readonly HashSet<string> _zeroAddrMacs = new();
        private readonly object _mapLock = new();

        // ── poll ACK ─────────────────────────────────────────
        private readonly ManualResetEventSlim _pollAck = new(false);

        public bool IsConnected => _link.Connected;


        private volatile bool _connectFiredByExplicit = false;


        // ── رویداد اتصال (برای RBusService) ──────────────────
        public event Action<string, bool>? ConnectionChanged;

        public BridgeWorker(
            string bridgeId, string host, int port,
            NodeRegistry registry,
            Action<string, Dictionary<string, object>> onEvent)
        {
            BridgeId = bridgeId;
            Host = host;
            Port = port;
            _registry = registry;
            _onEvent = onEvent;
        }

        // ── مدیریت اتصال ─────────────────────────────────────
        public bool Connect()
        {
            _wantConnected = true;
            if (_link.Connected) return true;

            bool ok = _link.Connect(Host, Port);
            if (ok)
            {
                _connectFiredByExplicit = true;   // ✅ علامت بزن
                _reconnectAt = double.MaxValue;   // ✅ حلقه دیگه retry نکنه
            }
            FireEvent(ok ? "connected" : "conn_error", new()
            {
                ["bridge_id"] = BridgeId,
                ["host"] = Host,
                ["port"] = Port,
            });
            ConnectionChanged?.Invoke(BridgeId, ok);
            return ok;
        }


        public void Disconnect()
        {
            _wantConnected = false;
            _link.Disconnect();
            FireEvent("disconnected", new() { ["bridge_id"] = BridgeId });
            ConnectionChanged?.Invoke(BridgeId, false);
        }

        public void SetEndpoint(string host, int port)
        {
            Host = host;
            Port = port;
        }

        public void Stop() => _alive = false;

        // ── ارسال دستور ──────────────────────────────────────
        public bool Send(byte addr, byte cmd, byte[]? data = null,
            string tag = "", bool expectFeedback = true,
            int priority = TX_PRIORITY_GAME)
        {
            data ??= Array.Empty<byte>();
            if (addr == ADDR_BROADCAST) expectFeedback = false;

            long seq = Interlocked.Increment(ref _txSeq);
            var item = new TxItem(priority, seq, addr, cmd, data, tag, expectFeedback);

            lock (_queueLock)
                _txQueue.Add(item);

            return true;
        }

        public void NotifyPollResponse() => _pollAck.Set();

        public void NotifyCleared(string mac)
        {
            mac = mac.ToUpperInvariant().Trim();
            lock (_mapLock)
            {
                var toDelete = new List<int>();
                foreach (var (a, m) in _addrToMac)
                    if (m == mac) toDelete.Add(a);
                foreach (var a in toDelete)
                    _addrToMac.Remove(a);
                _zeroAddrMacs.Add(mac);
            }
        }

        // ── شروع thread ──────────────────────────────────────
        public void Start()
        {
            _thread = new Thread(Run)
            {
                Name = $"BridgeWorker-{BridgeId}",
                IsBackground = true,
            };
            _thread.Start();
        }

        // ── حلقه اصلی thread ─────────────────────────────────
        private void Run()
        {
            while (_alive)
            {
                // ── reconnect خودکار ──────────────────────────
                if (_wantConnected && !_link.Connected)
                {
                    double now = UnixNow();
                    if (now >= _reconnectAt)
                    {
                        // اگر explicit connect قبلاً زده، این اولین چرخه رو رد کن
                        if (_connectFiredByExplicit)
                        {
                            _connectFiredByExplicit = false;
                            _reconnectAt = now + 2.0;
                            Thread.Sleep((int)(LOOP_SLEEP_S * 1000));
                            continue;
                        }

                        bool ok = _link.Connect(Host, Port);
                        _reconnectAt = now + (ok ? double.MaxValue : 2.0);
                        FireEvent(ok ? "reconnected" : "conn_error", new()
                        {
                            ["bridge_id"] = BridgeId,
                            ["host"] = Host,
                            ["port"] = Port,
                        });
                        ConnectionChanged?.Invoke(BridgeId, ok);
                    }
                    Thread.Sleep((int)(LOOP_SLEEP_S * 1000));
                    continue;
                }


                if (!_link.Connected)
                {
                    Thread.Sleep((int)(LOOP_SLEEP_S * 1000));
                    continue;
                }

                // ── دریافت داده ──────────────────────────────
                byte[] chunk = _link.Read();
                if (chunk.Length > 0)
                {
                    _lastRxTime = UnixNow();
                    // اضافه کردن به بافر
                    var newBuf = new byte[_rxBuf.Length + chunk.Length];
                    _rxBuf.CopyTo(newBuf, 0);
                    chunk.CopyTo(newBuf, _rxBuf.Length);
                    _rxBuf = newBuf;

                    // تجزیه فریم‌ها
                    int offset = 0;
                    while (RBusProtocol.TryParseFrame(
                        _rxBuf, ref offset, _rxBuf.Length,
                        out byte addr, out byte cmd, out byte[] data))
                    {
                        HandleRxFrame(addr, cmd, data);
                    }

                    // باقیمانده بافر
                    if (offset > 0)
                    {
                        var remaining = new byte[_rxBuf.Length - offset];
                        Array.Copy(_rxBuf, offset, remaining, 0, remaining.Length);
                        _rxBuf = remaining;
                    }
                }

                // ── ارسال TX ─────────────────────────────────
                TxItem? item = null;
                lock (_queueLock)
                {
                    if (_txQueue.Count > 0)
                    {
                        item = _txQueue.Min;
                        _txQueue.Remove(item!);
                    }
                }

                if (item != null)
                {
                    // turnaround delay
                    double elapsed = UnixNow() - _lastRxTime;
                    if (elapsed < RX_TO_TX_TURNAROUND_S)
                        Thread.Sleep((int)((RX_TO_TX_TURNAROUND_S - elapsed) * 1000));

                    byte[] frame = RBusProtocol.BuildFrame(item.Addr, item.Cmd, item.Data);
                    _link.Send(frame);

                    Thread.Sleep((int)(TX_INTER_FRAME_DELAY_S * 1000));
                }
                else
                {
                    Thread.Sleep((int)(LOOP_SLEEP_S * 1000));
                }
            }
        }

        // ── پردازش فریم دریافتی ──────────────────────────────
        private void HandleRxFrame(byte frameAddr, byte cmd, byte[] data)
        {
            // ══════════════════════════════════════════════════════
            // cmd=0x14 → STATUS_BROADCAST
            // نودها هر ثانیه این رو می‌فرستن — addr نود در data[0]
            // فرمت: [node_addr][?][fw_major][fw_minor][ts0][ts1][ts2]
            // ══════════════════════════════════════════════════════
            if (cmd == 0x14 && data.Length >= 7)
            {
                byte nodeAddr = data[0];
                byte fwMajor = data[2];
                byte fwMinor = data[3];

                string mac;
                lock (_mapLock)
                {
                    if (!_addrToMac.TryGetValue(nodeAddr, out mac!))
                    {
                        mac = $"RB:{BridgeId}:00:{nodeAddr:X2}";
                        _addrToMac[nodeAddr] = mac;
                    }
                }

                _registry.UpsertDiscovery(mac, BridgeId, nodeAddr, true);
                _registry.UpsertStatus(mac, BridgeId, nodeAddr,
                    false, 0, 0, fwMajor, fwMinor);

                FireEvent("heartbeat", new()
                {
                    ["bridge_id"] = BridgeId,
                    ["mac"] = mac,
                    ["addr"] = (int)nodeAddr,
                    ["fw"] = $"v{fwMajor}.{fwMinor}",
                });
                return;
            }


            // ══════════════════════════════════════════════════════
            // بقیه cmd‌ها — addr فرستنده از _addrToMac
            // ══════════════════════════════════════════════════════
            string? mac2 = null;
            lock (_mapLock) _addrToMac.TryGetValue(frameAddr, out mac2);

            switch (cmd)
            {
                // ── Discovery Response ────────────────────────────
                case CMD_DISCOVERY_RES when data.Length >= 8:
                    {
                        // AA-FF-21-0B [MAC×6][addr][has_code][fw_maj][fw_min][CRC]
                        // data:         0..5    6      7        8       9
                        string macStr = MacStr(data[..6]);
                        int nodeAddr = data[6];
                        bool hasCode = data[7] != 0;

                        string fakeMac = $"RB:{BridgeId}:00:{nodeAddr:X2}";

                        lock (_mapLock)
                        {
                            _addrToMac[nodeAddr] = macStr;
                            if (nodeAddr == 0) _zeroAddrMacs.Add(macStr);
                        }

                        // ✅ MAC ساختگی رو از registry پاک کن
                        _registry.Remove(fakeMac);

                        _registry.UpsertDiscovery(macStr, BridgeId, nodeAddr, hasCode);

                        FireEvent("discovery", new()
                        {
                            ["bridge_id"] = BridgeId,
                            ["mac"] = macStr,
                            ["addr"] = nodeAddr,
                            ["has_code"] = hasCode,
                        });
                        break;
                    }


                // ── Address ACK ───────────────────────────────────
                case RBusProtocol.CMD_ADDRESS_ACK when data.Length >= 7:
                    {
                        string macStr = RBusProtocol.MacStr(data[..6]);
                        int nodeAddr = data[6];
                        lock (_mapLock) _addrToMac[nodeAddr] = macStr;
                        _registry.UpsertAddrAck(macStr, BridgeId, nodeAddr);
                        FireEvent("addr_ack", new()
                        {
                            ["bridge_id"] = BridgeId,
                            ["mac"] = macStr,
                            ["addr"] = nodeAddr,
                        });
                        break;
                    }

                // ── Get Status Response ───────────────────────────
                case RBusProtocol.CMD_GET_STATUS when data.Length >= 6:
                    {
                        if (mac2 == null) break;
                        bool inOta = data[0] != 0;
                        int touchMask = data[1];
                        int adc = (data[2] << 8) | data[3];
                        int fwMajor = data[4];
                        int fwMinor = data[5];
                        _registry.UpsertStatus(mac2, BridgeId, frameAddr,
                            inOta, touchMask, adc, fwMajor, fwMinor);
                        _pollAck.Set();
                        FireEvent("status", new()
                        {
                            ["bridge_id"] = BridgeId,
                            ["mac"] = mac2,
                            ["addr"] = (int)frameAddr,
                            ["touch_mask"] = touchMask,
                            ["adc"] = adc,
                            ["fw"] = $"v{fwMajor}.{fwMinor}",
                        });
                        break;
                    }

                // ── Touch Event ───────────────────────────────────
                case RBusProtocol.CMD_TOUCH_EVENT when data.Length >= 2:
                    {
                        if (mac2 == null) break;
                        FireEvent("touch", new()
                        {
                            ["bridge_id"] = BridgeId,
                            ["mac"] = mac2,
                            ["addr"] = (int)frameAddr,
                            ["key"] = (int)data[0],
                            ["state"] = (int)data[1],
                        });
                        break;
                    }

                // ── Motion Event ──────────────────────────────────
                case RBusProtocol.CMD_MOTION_EVENT when data.Length >= 1:
                    {
                        if (mac2 == null) break;
                        FireEvent("motion", new()
                        {
                            ["bridge_id"] = BridgeId,
                            ["mac"] = mac2,
                            ["addr"] = (int)frameAddr,
                            ["sensor"] = (int)data[0],
                        });
                        break;
                    }

                // ── Game Event ────────────────────────────────────
                case RBusProtocol.CMD_GAME_EVENT when data.Length >= 5:
                    {
                        if (mac2 == null) break;
                        byte evtCode = data[2];
                        RBusProtocol.GEVT_NAMES.TryGetValue(evtCode, out string? evtName);
                        FireEvent("game_event", new()
                        {
                            ["bridge_id"] = BridgeId,
                            ["mac"] = mac2,
                            ["addr"] = (int)frameAddr,
                            ["key"] = (int)data[1],
                            ["evt"] = evtName ?? $"0x{evtCode:X2}",
                            ["value"] = (int)data[3],
                            ["seq"] = (int)data[4],
                        });
                        break;
                    }

                case RBusProtocol.CMD_ACK:
                    FireEvent("ack", new()
                    {
                        ["bridge_id"] = BridgeId,
                        ["addr"] = (int)frameAddr
                    });
                    break;

                case RBusProtocol.CMD_NACK:
                    FireEvent("nack", new()
                    {
                        ["bridge_id"] = BridgeId,
                        ["addr"] = (int)frameAddr
                    });
                    break;
            }
        }


        private void FireEvent(string kind, Dictionary<string, object> data)
            => _onEvent(kind, data);

        private static double UnixNow()
            => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
    }
}

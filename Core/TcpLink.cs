// ============================================================
// TcpLink.cs
// مدیریت TCP socket به مبدل USR-DR134 (RS485↔Ethernet)
// معادل: core/tcp_link.py
// ============================================================
using System;
using System.Net.Sockets;
using System.Threading;

namespace RBusService.Core
{
    public class TcpLink : IDisposable
    {
        private TcpClient? _client;
        private NetworkStream? _stream;
        private readonly object _lock = new();

        public bool Connected { get; private set; } = false;

        // ── اتصال ────────────────────────────────────────────
        public bool Connect(string host, int port, int timeoutMs = 3000)
        {
            Disconnect();
            try
            {
                var client = new TcpClient();
                client.NoDelay = true;  // غیرفعال کردن Nagle — کاهش تأخیر RS485

                // KeepAlive — جلوگیری از قطع توسط USR-DR134 هر 2 ثانیه
                client.Client.SetSocketOption(
                    SocketOptionLevel.Socket,
                    SocketOptionName.KeepAlive, true);

                // ویندوز: SIO_KEEPALIVE_VALS
                if (OperatingSystem.IsWindows())
                {
                    // onoff=1, keepaliveTime=30000ms, keepaliveInterval=10000ms
                    byte[] keepAlive = new byte[12];
                    BitConverter.GetBytes(1u).CopyTo(keepAlive, 0);
                    BitConverter.GetBytes(30000u).CopyTo(keepAlive, 4);
                    BitConverter.GetBytes(10000u).CopyTo(keepAlive, 8);
                    client.Client.IOControl(IOControlCode.KeepAliveValues, keepAlive, null);
                }

                var result = client.BeginConnect(host, port, null, null);
                bool success = result.AsyncWaitHandle.WaitOne(timeoutMs);

                if (!success || !client.Connected)
                {
                    client.Close();
                    return false;
                }

                client.EndConnect(result);
                client.ReceiveTimeout = 50; // 50ms — non-blocking read

                lock (_lock)
                {
                    _client = client;
                    _stream = client.GetStream();
                    Connected = true;
                }
                return true;
            }
            catch
            {
                lock (_lock) { Connected = false; }
                return false;
            }
        }

        // ── قطع اتصال ────────────────────────────────────────
        public void Disconnect()
        {
            lock (_lock)
            {
                try { _stream?.Close(); } catch { }
                try { _client?.Close(); } catch { }
                _stream = null;
                _client = null;
                Connected = false;
            }
        }

        // ── ارسال داده ───────────────────────────────────────
        public bool Send(byte[] data)
        {
            lock (_lock)
            {
                if (!Connected || _stream == null) return false;
                try
                {
                    _stream.Write(data, 0, data.Length);
                    // ✅ لاگ موقت برای دیباگ
                    Console.WriteLine($"[TX] {data.Length} bytes: {BitConverter.ToString(data)}");
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[TX ERROR] {ex.Message}");
                    Connected = false;
                    _stream = null;
                    _client = null;
                    return false;
                }
            }
        }


        // ── دریافت داده (non-blocking) ────────────────────────
        public byte[] Read(int maxLen = 4096)
        {
            lock (_lock)
            {
                if (!Connected || _stream == null) return Array.Empty<byte>();
                try
                {
                    if (!_stream.DataAvailable) return Array.Empty<byte>();
                    var buf = new byte[maxLen];
                    int n = _stream.Read(buf, 0, maxLen);
                    if (n == 0)
                    {
                        Connected = false;
                        _stream = null;
                        _client = null;
                        return Array.Empty<byte>();
                    }
                    var result = new byte[n];
                    Array.Copy(buf, result, n);
                    // ✅ لاگ موقت برای دیباگ
                    Console.WriteLine($"[RX] {n} bytes: {BitConverter.ToString(result)}");
                    return result;
                }
                catch (System.IO.IOException) { return Array.Empty<byte>(); }
                catch (Exception ex)
                {
                    Console.WriteLine($"[RX ERROR] {ex.Message}");
                    Connected = false;
                    _stream = null;
                    _client = null;
                    return Array.Empty<byte>();
                }
            }
        }


        public void Dispose() => Disconnect();
    }
}

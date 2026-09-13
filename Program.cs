// ============================================================
// Program.cs
// نقطه ورود برنامه — راه‌اندازی سرویس RBUS و نمایش وضعیت نودها
// ============================================================
using System;
using System.Linq;
using System.Threading;
using RBusService.Core;

namespace RBusService
{
    class Program
    {
        static void Main(string[] args)
        {
            // ── ساخت سرویس اصلی ──────────────────────────────────
            // callback رویدادها: هر اتفاقی روی باس اینجا میاد
            var service = new RBusService.Core.RBusService((kind, data) =>
            {
                // heartbeat هر ثانیه از همه نودها میاد — نمایشش شلوغ‌کاریه
                if (kind == "heartbeat") return;

                // بقیه رویدادها (discovery، touch، game_event و...) چاپ میشن
                Console.Write($"[EVENT] {kind}");
                foreach (var kv in data)
                    Console.Write($"  {kv.Key}={kv.Value}");
                Console.WriteLine();
            });

            // ── اتصال به مبدل TCP↔RS485 ──────────────────────────
            service.AddBridge("bridge_1", "192.168.1.215", 5000);
            bool connected = service.ConnectBridge("bridge_1");

            if (!connected)
            {
                Console.WriteLine("[ERROR] اتصال به bridge_1 ناموفق بود.");
                Console.WriteLine("        آدرس IP و پورت را بررسی کنید.");
                Environment.Exit(1);
            }

            Console.WriteLine("[INFO] متصل شد. در حال دریافت اطلاعات نودها...");

            // ── صبر برای دریافت اولین heartbeat‌ها ──────────────
            // نودها هر ثانیه heartbeat می‌فرستن — 3 ثانیه کافیه
            Thread.Sleep(3000);

            // ── نمایش اولیه لیست نودها ───────────────────────────
            PrintNodes(service);

            // ── تایمر refresh — هر 5 ثانیه یک بار ───────────────
            var timer = new Timer(_ => PrintNodes(service),
                null,
                dueTime: TimeSpan.FromSeconds(5),
                period: TimeSpan.FromSeconds(5));

            // ── انتظار برای Ctrl+C ────────────────────────────────
            Console.WriteLine("\nPress Ctrl+C to exit");
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;      // برنامه رو بلافاصله kill نکن
                timer.Dispose();      // تایمر رو ببند
                service.Dispose();    // TCP و thread‌ها رو تمیز ببند
                Console.WriteLine("\n[INFO] سرویس متوقف شد.");
                Environment.Exit(0);
            };

            // ── نگه داشتن برنامه تا Ctrl+C ───────────────────────
            Thread.Sleep(Timeout.Infinite);
        }

        // ── نمایش جدول وضعیت نودها ───────────────────────────────
        static void PrintNodes(RBusService.Core.RBusService service)
        {
            var nodes = service.Registry.GetAll()
                .OrderBy(n => n.Addr)
                .ToList();

            Console.WriteLine($"\n{"─",40}");
            Console.WriteLine($"  Nodes: {nodes.Count}  @ {DateTime.Now:HH:mm:ss}");
            Console.WriteLine($"{"─",40}");

            foreach (var n in nodes)
                Console.WriteLine(
                    $"  Addr={n.AddrStr,-6} FW={n.FwStr,-8} Status={n.Status}");

            Console.WriteLine($"{"─",40}");
        }
    }
}

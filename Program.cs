using System;
using System.Threading;
using RBusService.Core;

namespace RBusService
{
    class Program
    {
        static ushort Crc16_Zero(ReadOnlySpan<byte> data)
        {
            ushort crc = 0x0000;   // ← Init=0
            foreach (byte b in data)
            {
                crc ^= (ushort)(b << 8);
                for (int i = 0; i < 8; i++)
                    crc = (crc & 0x8000) != 0
                        ? (ushort)((crc << 1) ^ 0x1021)
                        : (ushort)(crc << 1);
            }
            return crc;
        }

        static ushort Crc16Modbus(ReadOnlySpan<byte> data)
        {
            ushort crc = 0xFFFF;
            foreach (byte b in data)
            {
                crc ^= b;
                for (int i = 0; i < 8; i++)
                    crc = (crc & 0x0001) != 0
                        ? (ushort)((crc >> 1) ^ 0xA001)
                        : (ushort)(crc >> 1);
            }
            return crc;
        }



        static void Main(string[] args)
        {
            // ── تست CRC روی فریم واقعی ──────────────────────────
            byte[] testFrame = new byte[]
                        {
                0xAA, 0x00, 0x14, 0x07,
                0x04, 0x01, 0x07, 0x0E, 0x00, 0x00, 0xD5,
                0xDB, 0x27   // CRC دریافتی
                        };

            // تست ۱: CRC روی کل هدر+data (بدون CRC bytes)
            ushort crc1 = RBusProtocol.Crc16(testFrame.AsSpan(0, 11));
            Console.WriteLine($"CRC1 (AA..D5) = {crc1:X4}  expect=DB27  match={crc1 == 0xDB27}");

            // تست ۲: CRC بدون 0xAA
            ushort crc2 = RBusProtocol.Crc16(testFrame.AsSpan(1, 10));
            Console.WriteLine($"CRC2 (00..D5) = {crc2:X4}  expect=DB27  match={crc2 == 0xDB27}");

            // تست ۳: CRC با Init=0x0000
            ushort crc3 = Crc16_Zero(testFrame.AsSpan(0, 11));
            Console.WriteLine($"CRC3 init=0   = {crc3:X4}  expect=DB27  match={crc3 == 0xDB27}");

            // تست ۴: byte swap
            ushort crc4 = (ushort)((0x27 << 8) | 0xDB);
            Console.WriteLine($"CRC4 swapped  = {crc4:X4}  expect=DB27");

            // تست ۵: CRC16-MODBUS (Poly=0x8005, Init=0xFFFF)
            ushort crc5 = Crc16Modbus(testFrame.AsSpan(0, 11));
            Console.WriteLine($"CRC5 MODBUS   = {crc5:X4}  expect=DB27  match={crc5 == 0xDB27}");


            var service = new RBusService.Core.RBusService((kind, data) =>
            {
                // فقط رویدادهای مهم رو نشون بده (heartbeat رو فیلتر کن)
                if (kind == "heartbeat") return;
                Console.Write($"[EVENT] {kind}");
                foreach (var kv in data)
                    Console.Write($"  {kv.Key}={kv.Value}");
                Console.WriteLine();
            });

            service.AddBridge("bridge_1", "192.168.1.215", 5000);
            service.ConnectBridge("bridge_1");

            // صبر کن نودها از heartbeat ثبت بشن
            Console.WriteLine("Waiting for nodes...");
            Thread.Sleep(3000);

            // نمایش نودها
            PrintNodes(service);

            // هر 5 ثانیه refresh
            var timer = new System.Threading.Timer(_ => PrintNodes(service),
                null, 5000, 5000);

            Console.WriteLine("\nPress Ctrl+C to exit");
            Console.CancelKeyPress += (_, e) =>
            {
                e.Cancel = true;
                timer.Dispose();
                service.Dispose();
                Environment.Exit(0);
            };
            Thread.Sleep(Timeout.Infinite);
        }

        static void PrintNodes(RBusService.Core.RBusService service)
        {
            var nodes = service.Registry.GetAll()
                .OrderBy(n => n.Addr)
                .ToList();

            Console.WriteLine($"\n{'─',40}");
            Console.WriteLine($"  Nodes: {nodes.Count}  @ {DateTime.Now:HH:mm:ss}");
            Console.WriteLine($"{'─',40}");
            foreach (var n in nodes)
                Console.WriteLine(
                    $"  Addr={n.AddrStr,-6} FW={n.FwStr,-8} Status={n.Status}");
            Console.WriteLine($"{'─',40}");
        }

    }
}

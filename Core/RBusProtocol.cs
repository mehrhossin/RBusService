// ============================================================
// RBusProtocol.cs
// ثابت‌های پروتکل RBUS، CRC16، ساخت و تجزیه فریم
// معادل: core/protocol.py
// ============================================================
using System;
using System.Collections.Generic;

namespace RBusService.Core
{
    public static class RBusProtocol
    {
        // ── ثابت‌های پروتکل ──────────────────────────────────
        public const byte FRAME_START = 0xAA;
        public const byte ADDR_BROADCAST = 0xFF;
        public const byte ADDR_MASTER = 0x00;

        // ── دستورات عمومی ────────────────────────────────────
        public const byte CMD_SET_COLOR = 0x01;
        public const byte CMD_RESET = 0x02;
        public const byte CMD_GET_STATUS = 0x03;
        public const byte CMD_PING = 0x04;
        public const byte CMD_SET_BRIGHTNESS = 0x05;
        public const byte CMD_CLEAR_ALL = 0x06;
        public const byte CMD_IDENTIFY = 0x07;

        // ── رویدادها (نود → مستر) ────────────────────────────
        public const byte CMD_TOUCH_EVENT = 0x10;
        public const byte CMD_ADC_EVENT = 0x11;
        public const byte CMD_MOTION_EVENT = 0x12;
        public const byte CMD_GAME_EVENT = 0x13;

        // ── Discovery ────────────────────────────────────────
        public const byte CMD_DISCOVERY_REQ = 0x20;
        public const byte CMD_DISCOVERY_RES = 0x21;
        public const byte CMD_SET_ADDRESS = 0x22;
        public const byte CMD_ADDRESS_ACK = 0x23;
        public const byte CMD_CLEAR_ADDRESS = 0x24;

        // ── LED / افکت ───────────────────────────────────────
        public const byte CMD_SET_PIXEL_COLOR = 0x25;
        public const byte CMD_SET_CHANNEL_COLOR = 0x26;
        public const byte CMD_PIXEL_EFFECT = 0x27;
        public const byte CMD_STAGE_LOCK = 0x28;
        public const byte CMD_STAGE_UNLOCK = 0x29;

        // ── بوت‌لودر ─────────────────────────────────────────
        public const byte CMD_ENTER_BOOT = 0x30;
        public const byte CMD_BOOT_ACK = 0x31;

        // ── پیکربندی نود ─────────────────────────────────────
        public const byte CMD_SET_PIXEL_COUNT = 0x40;

        // ── بلوک اکشن (0x4x) ─────────────────────────────────
        public const byte CMD_ACTION_RING = 0x42;
        public const byte CMD_ACTION_RING_OFF = 0x43;
        public const byte CMD_ACTION_ALL_OFF = 0x43;
        public const byte CMD_ACTION_FOOT_MODE = 0x44;
        public const byte CMD_ACTION_STAGE_RGB = 0x45;

        // ── بلوک هاید (0x5x) ─────────────────────────────────
        public const byte CMD_SET_KEY_DISPLAY = 0x50;
        public const byte CMD_GAME_ARM = 0x51;
        public const byte CMD_GAME_START = 0x52;
        public const byte CMD_GAME_FAIL = 0x53;
        public const byte CMD_MOTION_MONITOR_1 = 0x54;
        public const byte CMD_MOTION_MONITOR_2 = 0x55;
        public const byte CMD_GAME_CANCEL = 0x56;
        public const byte CMD_GAME_WAIT = 0x57;
        public const byte CMD_GAME_STATUS = 0x58;
        public const byte CMD_SELF_TEST = 0x59;

        // ── بلوک گرید (0x6x) ─────────────────────────────────
        public const byte CMD_GRID_ERROR = 0x60;
        public const byte CMD_GRID_SELECT = 0x61;
        public const byte CMD_GRID_RAIN = 0x62;
        public const byte CMD_GRID_STOP = 0x63;
        public const byte CMD_VIBRON_RING = 0x64;
        public const byte CMD_TIMER_KEY_START = 0x65;
        public const byte CMD_TIMER_STOP = 0x66;
        public const byte CMD_SET_TIMER_COUNT = 0x67;

        // ── بلوک تسلا/قطره (0x7x) ────────────────────────────
        public const byte CMD_TESLA_START = 0x70;
        public const byte CMD_TESLA_STOP = 0x71;
        public const byte CMD_DROP_ARM = 0x72;
        public const byte CMD_DROP_STATUS = 0x73;
        public const byte CMD_DROP_CANCEL = 0x74;

        // ── OTA ──────────────────────────────────────────────
        public const byte CMD_UPDATE_START = 0xA0;
        public const byte CMD_UPDATE_DATA = 0xA1;
        public const byte CMD_UPDATE_END = 0xA2;
        public const byte CMD_UPDATE_ACK = 0xA3;
        public const byte CMD_BOOT_ERROR = 0xA4;
        public const byte CMD_ABORT_BOOT = 0xA5;

        // ── ACK / NACK ────────────────────────────────────────
        public const byte CMD_ACK = 0xAA;
        public const byte CMD_NACK = 0xBB;

        // ── افکت‌های PIXEL_EFFECT ─────────────────────────────
        public const byte EFFECT_NONE = 0x00;
        public const byte EFFECT_RAINBOW = 0x01;
        public const byte EFFECT_TESLA_IDLE = 0x08;

        // ── اولویت TX ────────────────────────────────────────
        public const int TX_PRIORITY_GAME = 0;
        public const int TX_PRIORITY_SYS = 1;
        public const int TX_PRIORITY_POLL = 2;

        // ── تایمینگ ───────────────────────────────────────────
        public const double POLL_INTERVAL_S = 1.0;
        public const double POLL_TIMEOUT_S = 0.05;
        public const double DISCOVERY_INTERVAL_S = 10.0;

        // ── OTA Password ──────────────────────────────────────
        public static readonly byte[] OTA_PASSWORD = { 0x52, 0x41, 0x42, 0x49, 0x4E };

        // ── نام دستورات ───────────────────────────────────────
        public static readonly Dictionary<byte, string> CMD_NAMES = new()
        {
            { CMD_SET_COLOR,       "SET_COLOR"       },
            { CMD_RESET,           "RESET"           },
            { CMD_GET_STATUS,      "GET_STATUS"      },
            { CMD_PING,            "PING"            },
            { CMD_DISCOVERY_REQ,   "DISCOVERY_REQ"   },
            { CMD_DISCOVERY_RES,   "DISCOVERY_RES"   },
            { CMD_SET_ADDRESS,     "SET_ADDRESS"      },
            { CMD_ADDRESS_ACK,     "ADDRESS_ACK"     },
            { CMD_TOUCH_EVENT,     "TOUCH_EVENT"     },
            { CMD_ADC_EVENT,       "ADC_EVENT"       },
            { CMD_MOTION_EVENT,    "MOTION_EVENT"    },
            { CMD_GAME_EVENT,      "GAME_EVENT"      },
            { CMD_ACK,             "ACK"             },
            { CMD_NACK,            "NACK"            },
            { CMD_ACTION_RING,     "ACTION_RING"     },
            { CMD_ACTION_STAGE_RGB,"ACTION_STAGE_RGB"},
            { CMD_GAME_ARM,        "GAME_ARM"        },
            { CMD_GAME_START,      "GAME_START"      },
            { CMD_GAME_FAIL,       "GAME_FAIL"       },
            { CMD_GAME_CANCEL,     "GAME_CANCEL"     },
        };

        // ── نام رویدادهای بازی ────────────────────────────────
        public static readonly Dictionary<byte, string> GEVT_NAMES = new()
        {
            { 0x01, "KEY_HIT"      }, { 0x02, "TIMEOUT"      },
            { 0x03, "WRONG_KEY"    }, { 0x04, "SUCCESS_END"  },
            { 0x05, "FAIL_END"     }, { 0x06, "ARMED"        },
            { 0x07, "STARTED"      }, { 0x08, "CANCELED"     },
            { 0x10, "DROP_ARMED"   }, { 0x11, "DROP_ACTIVE"  },
            { 0x12, "DROP_HIT"     }, { 0x13, "DROP_MISS"    },
            { 0x14, "DROP_TIMEOUT" }, { 0x15, "DROP_END"     },
            { 0x16, "DROP_CANCELED"}, { 0x17, "GRID_KEY_DONE"},
        };

        // ============================================================
        // CRC16 — الگوریتم CCITT (Poly=0x1021, Init=0xFFFF)
        // باید دقیقاً با firmware/rbus_node.h یکسان باشد
        // ============================================================
        public static ushort Crc16(ReadOnlySpan<byte> data)
        {
            ushort crc = 0xFFFF;
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

        // ============================================================
        // BuildFrame — ساخت فریم RBUS
        // ساختار: [0xAA][addr][cmd][len][data...][CRC_HI][CRC_LO]
        // ============================================================
        public static byte[] BuildFrame(byte addr, byte cmd, byte[] data)
        {
            data ??= Array.Empty<byte>();
            int frameLen = 4 + data.Length + 2; // header(4) + data + crc(2)
            byte[] frame = new byte[frameLen];

            frame[0] = FRAME_START;
            frame[1] = addr;
            frame[2] = cmd;
            frame[3] = (byte)data.Length;

            Array.Copy(data, 0, frame, 4, data.Length);

            ushort crc = Crc16(frame.AsSpan(0, 4 + data.Length));
            frame[4 + data.Length] = (byte)(crc & 0xFF);   // LO اول
            frame[4 + data.Length + 1] = (byte)(crc >> 8);     // HI دوم

            return frame;
        }

        // ============================================================
        // ParseFrame — تجزیه فریم RBUS از بافر
        // خروجی: (addr, cmd, data) یا null اگر فریم ناقص/خراب باشد
        // بافر را تا بعد از فریم پیشروی می‌کند (ref offset)
        // ============================================================
        public static bool TryParseFrame(
            byte[] buf, ref int offset, int count,
            out byte addr, out byte cmd, out byte[] data)
        {
            addr = 0; cmd = 0; data = Array.Empty<byte>();

            // پیدا کردن FRAME_START
            while (offset < count && buf[offset] != FRAME_START)
                offset++;

            // حداقل ۶ بایت لازم است: [AA][addr][cmd][len][CRC_HI][CRC_LO]
            if (offset + 6 > count)
                return false;

            int dataLen = buf[offset + 3];

            // بررسی اینکه کل فریم در بافر موجود است
            if (offset + 4 + dataLen + 2 > count)
                return false;

            // بررسی CRC
            int frameEnd = offset + 4 + dataLen;
            ushort calcCrc = Crc16(buf.AsSpan(offset, 4 + dataLen));
            ushort recvCrc = (ushort)((buf[frameEnd + 1] << 8) | buf[frameEnd]);

            if (calcCrc != recvCrc)
            {
                offset++; // بایت خراب را رد کن
                return false;
            }

            addr = buf[offset + 1];
            cmd = buf[offset + 2];
            data = new byte[dataLen];
            Array.Copy(buf, offset + 4, data, 0, dataLen);

            offset += 4 + dataLen + 2; // پیشروی به بعد از فریم
            return true;
        }

        // ── کمک‌کننده MAC ────────────────────────────────────
        public static string MacStr(byte[] macBytes)
        {
            if (macBytes == null || macBytes.Length < 6)
                return "00:00:00:00:00:00";
            return BitConverter.ToString(macBytes, 0, 6).Replace('-', ':');
        }

        public static byte[] MacBytesFromStr(string mac)
        {
            var parts = mac.Split(':');
            var result = new byte[6];
            for (int i = 0; i < 6; i++)
                result[i] = Convert.ToByte(parts[i], 16);
            return result;
        }
    }
}

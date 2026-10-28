// MessagePack, as the Colyseus server packs room messages (msgpackr with
// useRecords: false: standard maps, arrays, strings and numbers). Values read
// back as Dictionary<string, object>, List<object>, double, long, string, bool
// or null; writing takes the same shapes.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;

namespace Porchlight.Net
{
    public static class MsgPack
    {
        public static object Read(byte[] b, ref int i)
        {
            byte p = b[i++];
            if (p <= 0x7f) return (long)p;
            if (p >= 0xe0) return (long)(sbyte)p;
            if ((p & 0xf0) == 0x80) return Map(b, ref i, p & 0x0f);
            if ((p & 0xf0) == 0x90) return Array(b, ref i, p & 0x0f);
            if ((p & 0xe0) == 0xa0) return Str(b, ref i, p & 0x1f);
            switch (p)
            {
                case 0xc0: return null;
                case 0xc2: return false;
                case 0xc3: return true;
                case 0xc4: return Bin(b, ref i, b[i++]);
                case 0xc5: return Bin(b, ref i, U16(b, ref i));
                case 0xc6: return Bin(b, ref i, (int)U32(b, ref i));
                case 0xca:
                {
                    var t = new byte[4];
                    for (int k = 0; k < 4; k++) t[3 - k] = b[i + k];
                    i += 4;
                    if (!BitConverter.IsLittleEndian) System.Array.Reverse(t);
                    return (double)BitConverter.ToSingle(t, 0);
                }
                case 0xcb:
                {
                    var t = new byte[8];
                    for (int k = 0; k < 8; k++) t[7 - k] = b[i + k];
                    i += 8;
                    if (!BitConverter.IsLittleEndian) System.Array.Reverse(t);
                    return BitConverter.ToDouble(t, 0);
                }
                case 0xcc: return (long)b[i++];
                case 0xcd: return (long)U16(b, ref i);
                case 0xce: return (long)U32(b, ref i);
                case 0xcf:
                {
                    ulong v = 0;
                    for (int k = 0; k < 8; k++) v = (v << 8) | b[i++];
                    return (long)v;
                }
                case 0xd0: return (long)(sbyte)b[i++];
                case 0xd1: return (long)(short)U16(b, ref i);
                case 0xd2: return (long)(int)U32(b, ref i);
                case 0xd3:
                {
                    long v = 0;
                    for (int k = 0; k < 8; k++) v = (v << 8) | b[i++];
                    return v;
                }
                case 0xd9: return Str(b, ref i, b[i++]);
                case 0xda: return Str(b, ref i, U16(b, ref i));
                case 0xdb: return Str(b, ref i, (int)U32(b, ref i));
                case 0xdc: return Array(b, ref i, U16(b, ref i));
                case 0xdd: return Array(b, ref i, (int)U32(b, ref i));
                case 0xde: return Map(b, ref i, U16(b, ref i));
                case 0xdf: return Map(b, ref i, (int)U32(b, ref i));
                case 0xd4: i += 2; return null;
                case 0xd5: i += 3; return null;
                case 0xd6: i += 5; return null;
                case 0xd7: i += 9; return null;
                case 0xd8: i += 17; return null;
                case 0xc7:
                {
                    int n = b[i++];
                    i += 1 + n;
                    return null;
                }
                default: throw new FormatException($"msgpack: unknown prefix 0x{p:x2}");
            }
        }

        static int U16(byte[] b, ref int i)
        {
            int v = (b[i] << 8) | b[i + 1];
            i += 2;
            return v;
        }

        static uint U32(byte[] b, ref int i)
        {
            uint v = ((uint)b[i] << 24) | ((uint)b[i + 1] << 16) | ((uint)b[i + 2] << 8) | b[i + 3];
            i += 4;
            return v;
        }

        static string Str(byte[] b, ref int i, int n)
        {
            var s = Encoding.UTF8.GetString(b, i, n);
            i += n;
            return s;
        }

        static byte[] Bin(byte[] b, ref int i, int n)
        {
            var o = new byte[n];
            Buffer.BlockCopy(b, i, o, 0, n);
            i += n;
            return o;
        }

        static List<object> Array(byte[] b, ref int i, int n)
        {
            var o = new List<object>(n);
            for (int k = 0; k < n; k++) o.Add(Read(b, ref i));
            return o;
        }

        static Dictionary<string, object> Map(byte[] b, ref int i, int n)
        {
            var o = new Dictionary<string, object>(n);
            for (int k = 0; k < n; k++)
            {
                var key = Read(b, ref i);
                o[key?.ToString() ?? ""] = Read(b, ref i);
            }
            return o;
        }

        public static void Write(List<byte> o, object v)
        {
            switch (v)
            {
                case null: o.Add(0xc0); break;
                case bool b: o.Add(b ? (byte)0xc3 : (byte)0xc2); break;
                case string s: WriteString(o, s); break;
                case int n: WriteInt(o, n); break;
                case long n: WriteInt(o, n); break;
                case double d:
                    if (d == Math.Floor(d) && Math.Abs(d) < 4294967296.0 && !(d == 0 && double.IsNegative(d))) WriteInt(o, (long)d);
                    else WriteDouble(o, d);
                    break;
                case float f: Write(o, (double)f); break;
                case byte[] bytes:
                    if (bytes.Length < 0x100)
                    {
                        o.Add(0xc4);
                        o.Add((byte)bytes.Length);
                    }
                    else if (bytes.Length < 0x10000)
                    {
                        o.Add(0xc5);
                        o.Add((byte)(bytes.Length >> 8));
                        o.Add((byte)bytes.Length);
                    }
                    else
                    {
                        o.Add(0xc6);
                        for (int sh = 24; sh >= 0; sh -= 8) o.Add((byte)(bytes.Length >> sh));
                    }
                    o.AddRange(bytes);
                    break;
                case IDictionary<string, object> m:
                    WriteHeader(o, m.Count, 0x80, 0xde, 0xdf);
                    foreach (var kv in m)
                    {
                        WriteString(o, kv.Key);
                        Write(o, kv.Value);
                    }
                    break;
                case IList l:
                    WriteHeader(o, l.Count, 0x90, 0xdc, 0xdd);
                    foreach (var x in l) Write(o, x);
                    break;
                default: WriteString(o, v.ToString()); break;
            }
        }

        static void WriteHeader(List<byte> o, int n, byte fix, byte p16, byte p32)
        {
            if (n < 16) o.Add((byte)(fix | n));
            else if (n < 0x10000)
            {
                o.Add(p16);
                o.Add((byte)(n >> 8));
                o.Add((byte)n);
            }
            else
            {
                o.Add(p32);
                for (int s = 24; s >= 0; s -= 8) o.Add((byte)(n >> s));
            }
        }

        public static void WriteString(List<byte> o, string s)
        {
            var bytes = Encoding.UTF8.GetBytes(s);
            int n = bytes.Length;
            if (n < 32) o.Add((byte)(0xa0 | n));
            else if (n < 0x100)
            {
                o.Add(0xd9);
                o.Add((byte)n);
            }
            else if (n < 0x10000)
            {
                o.Add(0xda);
                o.Add((byte)(n >> 8));
                o.Add((byte)n);
            }
            else
            {
                o.Add(0xdb);
                for (int sh = 24; sh >= 0; sh -= 8) o.Add((byte)(n >> sh));
            }
            o.AddRange(bytes);
        }

        static void WriteInt(List<byte> o, long n)
        {
            if (n >= 0)
            {
                if (n < 0x80) o.Add((byte)n);
                else if (n < 0x100)
                {
                    o.Add(0xcc);
                    o.Add((byte)n);
                }
                else if (n < 0x10000)
                {
                    o.Add(0xcd);
                    o.Add((byte)(n >> 8));
                    o.Add((byte)n);
                }
                else if (n < 0x100000000)
                {
                    o.Add(0xce);
                    for (int s = 24; s >= 0; s -= 8) o.Add((byte)(n >> s));
                }
                else
                {
                    o.Add(0xcf);
                    for (int s = 56; s >= 0; s -= 8) o.Add((byte)(n >> s));
                }
                return;
            }
            if (n >= -32) o.Add((byte)(sbyte)n);
            else if (n >= -128)
            {
                o.Add(0xd0);
                o.Add((byte)(sbyte)n);
            }
            else if (n >= -32768)
            {
                o.Add(0xd1);
                o.Add((byte)(n >> 8));
                o.Add((byte)n);
            }
            else if (n >= int.MinValue)
            {
                o.Add(0xd2);
                for (int s = 24; s >= 0; s -= 8) o.Add((byte)(n >> s));
            }
            else
            {
                o.Add(0xd3);
                for (int s = 56; s >= 0; s -= 8) o.Add((byte)(n >> s));
            }
        }

        static void WriteDouble(List<byte> o, double d)
        {
            var t = BitConverter.GetBytes(d);
            if (BitConverter.IsLittleEndian) System.Array.Reverse(t);
            o.Add(0xcb);
            o.AddRange(t);
        }

        public static double Num(object v) => v switch
        {
            double d => d,
            long l => l,
            int n => n,
            float f => f,
            _ => double.NaN,
        };
    }
}

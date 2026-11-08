// A picture of a town's streets with a race course on them (CourseCheck):
// roads grey, narrow streets lighter, the course's middle blue, its ribbon red,
// the start green and the finish black. Written as a PNG with a tiny encoder of
// its own, to look at.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Porchlight.Core;
using Porchlight.Explore;
using Porchlight.Model;

static class CoursePicture
{
    public static void Write(string path, IReadOnlyList<RoadStroke> roads, Course course, double x0, double y0, double x1, double y1, int scale)
    {
        int w = (int)Math.Ceiling((x1 - x0) * scale), h = (int)Math.Ceiling((y1 - y0) * scale);
        var px = new byte[w * h * 3];
        for (int i = 0; i < px.Length; i++) px[i] = 246;
        void Dot(double x, double y, double r, int rgb)
        {
            double cx = (x - x0) * scale, cy = (y - y0) * scale, rr = Math.Max(0.6, r * scale);
            for (int py = (int)(cy - rr); py <= (int)(cy + rr); py++)
                for (int qx = (int)(cx - rr); qx <= (int)(cx + rr); qx++)
                {
                    if (qx < 0 || py < 0 || qx >= w || py >= h) continue;
                    if ((qx - cx) * (qx - cx) + (py - cy) * (py - cy) > rr * rr) continue;
                    int k = (py * w + qx) * 3;
                    px[k] = (byte)(rgb >> 16);
                    px[k + 1] = (byte)(rgb >> 8);
                    px[k + 2] = (byte)rgb;
                }
        }
        void Line(IReadOnlyList<Vec2> pts, double r, int rgb)
        {
            for (int i = 1; i < pts.Count; i++)
            {
                double l = Geo.Dist(pts[i - 1], pts[i]);
                int n = Math.Max(1, (int)(l * scale / Math.Max(0.5, r * scale * 0.7)));
                for (int k = 0; k <= n; k++)
                {
                    var p = Geo.Lerp(pts[i - 1], pts[i], (double)k / n);
                    Dot(p.x, p.y, r, rgb);
                }
            }
        }
        foreach (var r in roads)
            if (r.kind != RoadKind.Path) Line(r.points, r.width / 2, r.kind == RoadKind.Main ? 0xb4b4b4 : 0xd6d6d6);
        if (course != null)
        {
            Line(course.points, 0.12, 0x2f6fd6);
            foreach (int side in new[] { 1, -1 }) Line(course.Side(side, 0.04).ConvertAll(x => x.p), 0.06, 0xd8342b);
            Dot(course.Start.x, course.Start.y, 1.2, 0x22aa44);
            Dot(course.Finish.x, course.Finish.y, 1.2, 0x111111);
        }
        File.WriteAllBytes(path, Png(px, w, h));
    }

    static byte[] Png(byte[] rgb, int w, int h)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < h; y++)
        {
            raw.WriteByte(0);
            raw.Write(rgb, y * w * 3, w * 3);
        }
        var z = new MemoryStream();
        using (var zs = new ZLibStream(z, CompressionLevel.Optimal, true)) raw.WriteTo(zs);
        var o = new MemoryStream();
        o.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        void Chunk(string type, byte[] data)
        {
            var len = BitConverter.GetBytes(data.Length);
            Array.Reverse(len);
            o.Write(len);
            var t = System.Text.Encoding.ASCII.GetBytes(type);
            o.Write(t);
            o.Write(data);
            uint crc = Crc(t, data);
            var c = BitConverter.GetBytes(crc);
            Array.Reverse(c);
            o.Write(c);
        }
        var ihdr = new byte[13];
        void Be(int at, int v) { ihdr[at] = (byte)(v >> 24); ihdr[at + 1] = (byte)(v >> 16); ihdr[at + 2] = (byte)(v >> 8); ihdr[at + 3] = (byte)v; }
        Be(0, w);
        Be(4, h);
        ihdr[8] = 8;
        ihdr[9] = 2;
        Chunk("IHDR", ihdr);
        Chunk("IDAT", z.ToArray());
        Chunk("IEND", new byte[0]);
        return o.ToArray();
    }

    static uint Crc(byte[] a, byte[] b)
    {
        uint c = 0xffffffff;
        foreach (var buf in new[] { a, b })
            foreach (byte x in buf)
            {
                c ^= x;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xedb88320 ^ (c >> 1) : c >> 1;
            }
        return ~c;
    }
}

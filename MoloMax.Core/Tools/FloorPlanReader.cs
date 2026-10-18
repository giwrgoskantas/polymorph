using System.Numerics;
using MoloMax.Core.Gta.Export;

namespace MoloMax.Core.Tools;

/// <summary>
/// Reads a floor plan picture (scan, photo of a plan, screenshot): dark lines are walls, every closed light area
/// is a room. Door gaps up to <see cref="Settings.DoorMax"/> are closed to find the rooms, then found again as doors.
/// Gives rectangles in metres (the picture's real width is given), walls on shared lines so the room builder joins them.
/// </summary>
public static class FloorPlanReader
{
    public sealed class Settings
    {
        /// <summary>Real width of the whole picture, metres.</summary>
        public float WidthMetres = 12f;
        /// <summary>0..1, 0 = automatic (Otsu).</summary>
        public float Threshold;
        public float DoorMax = 1.0f, MinRoomArea = 2f, Snap = 0.25f;
        public float RoomHeight = 3f;
    }

    public sealed class Result
    {
        public List<PlanRoom> Rooms = new();
        public List<PlanOpening> Doors = new();
        public List<string> Notes = new();
        public int Width, Height;
        public float MetresPerPixel;
    }

    public static Result Read(RgbaImage src, Settings s)
    {
        var res = new Result();
        // work at most 600 px on the long side
        float k = MathF.Min(1f, 600f / MathF.Max(src.Width, src.Height));
        int W = Math.Max(8, (int)(src.Width * k)), H = Math.Max(8, (int)(src.Height * k));
        res.Width = W; res.Height = H;
        float mpp = s.WidthMetres / W; res.MetresPerPixel = mpp;
        var lum = new float[W * H];
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                // box filter down-sampling
                int x0 = (int)(x / k), x1 = Math.Max(x0 + 1, (int)((x + 1) / k)), y0 = (int)(y / k), y1 = Math.Max(y0 + 1, (int)((y + 1) / k));
                float sum = 0; int n = 0;
                for (int yy = y0; yy < Math.Min(y1, src.Height); yy++)
                    for (int xx = x0; xx < Math.Min(x1, src.Width); xx++)
                    {
                        int i = (yy * src.Width + xx) * 4;
                        float a = src.Pixels[i + 3] / 255f;
                        float l = (0.299f * src.Pixels[i] + 0.587f * src.Pixels[i + 1] + 0.114f * src.Pixels[i + 2]) / 255f;
                        sum += l * a + (1 - a); n++;
                    }
                lum[y * W + x] = n > 0 ? sum / n : 1;
            }
        float th = s.Threshold > 0 ? s.Threshold : Otsu(lum);
        var wall = new bool[W * H];
        for (int i = 0; i < wall.Length; i++) wall[i] = lum[i] < th;

        // close the door gaps: grow the walls by half a door
        int r = Math.Max(1, (int)MathF.Round(s.DoorMax * 0.5f / mpp));
        var grown = Dilate(wall, W, H, r);

        // flood fill the free areas; the one touching the border is outside
        var label = new int[W * H];
        var regions = new List<(int MinX, int MinY, int MaxX, int MaxY, int Area, bool Border)>();
        var stack = new Stack<int>();
        for (int start = 0; start < W * H; start++)
        {
            if (grown[start] || label[start] != 0) continue;
            int id = regions.Count + 1;
            int mnx = W, mny = H, mxx = -1, mxy = -1, area = 0; bool border = false;
            stack.Push(start); label[start] = id;
            while (stack.Count > 0)
            {
                int p = stack.Pop(); int x = p % W, y = p / W;
                area++; mnx = Math.Min(mnx, x); mxx = Math.Max(mxx, x); mny = Math.Min(mny, y); mxy = Math.Max(mxy, y);
                if (x == 0 || y == 0 || x == W - 1 || y == H - 1) border = true;
                void Try(int q) { if (!grown[q] && label[q] == 0) { label[q] = id; stack.Push(q); } }
                if (x > 0) Try(p - 1); if (x < W - 1) Try(p + 1); if (y > 0) Try(p - W); if (y < H - 1) Try(p + W);
            }
            regions.Add((mnx, mny, mxx, mxy, area, border));
        }

        // rooms: grow back by r, to metres (Y up), snapped
        var rects = new List<(Vector2 Min, Vector2 Max, float Fill)>();
        foreach (var g in regions)
        {
            if (g.Border) continue;
            float areaM = g.Area * mpp * mpp;
            if (areaM < s.MinRoomArea * 0.5f) continue;
            int x0 = g.MinX - r, x1 = g.MaxX + 1 + r, y0 = g.MinY - r, y1 = g.MaxY + 1 + r;
            var mn = new Vector2(x0 * mpp, (H - y1) * mpp); var mx = new Vector2(x1 * mpp, (H - y0) * mpp);
            float boxArea = (g.MaxX - g.MinX + 1) * (g.MaxY - g.MinY + 1);
            if ((mx.X - mn.X) * (mx.Y - mn.Y) < s.MinRoomArea) continue;
            rects.Add((mn, mx, g.Area / boxArea));
        }
        // neighbouring rooms share one wall line (middle of the wall)
        for (int i = 0; i < rects.Count; i++)
            for (int j = 0; j < rects.Count; j++)
            {
                if (i == j) continue;
                var a = rects[i]; var b = rects[j];
                float overlapY = MathF.Min(a.Max.Y, b.Max.Y) - MathF.Max(a.Min.Y, b.Min.Y);
                float overlapX = MathF.Min(a.Max.X, b.Max.X) - MathF.Max(a.Min.X, b.Min.X);
                float gx = b.Min.X - a.Max.X, gy = b.Min.Y - a.Max.Y;
                if (overlapY > 0.5f && gx > -0.6f && gx < 0.8f) { float m = (a.Max.X + b.Min.X) * 0.5f; a.Max.X = m; b.Min.X = m; }
                if (overlapX > 0.5f && gy > -0.6f && gy < 0.8f) { float m = (a.Max.Y + b.Min.Y) * 0.5f; a.Max.Y = m; b.Min.Y = m; }
                rects[i] = a; rects[j] = b;
            }
        float Sn(float v) => s.Snap > 0 ? MathF.Round(v / s.Snap) * s.Snap : v;
        int idx = 0;
        foreach (var (mn, mx, fill) in rects.OrderByDescending(t => t.Max.Y).ThenBy(t => t.Min.X))
        {
            var room = new PlanRoom { Name = $"room_{++idx}", X0 = Sn(mn.X), Y0 = Sn(mn.Y), X1 = Sn(mx.X), Y1 = Sn(mx.Y), Height = s.RoomHeight };
            if (room.Max.X - room.Min.X < 1 || room.Max.Y - room.Min.Y < 1) continue;
            res.Rooms.Add(room);
            if (fill < 0.7f) res.Notes.Add($"{room.Name} is not rectangular (L shape?): check it.");
        }

        // doors: a free run in the real walls on a shared wall line between two rooms
        float Lum(float wx, float wy)
        {
            int x = (int)(wx / mpp), y = H - 1 - (int)(wy / mpp);
            if (x < 0 || y < 0 || x >= W || y >= H) return 1;
            return wall[y * W + x] ? 0 : 1;
        }
        var rooms = res.Rooms;
        for (int i = 0; i < rooms.Count; i++)
            for (int j = i + 1; j < rooms.Count; j++)
            {
                var a = rooms[i]; var b = rooms[j];
                foreach (bool vertical in new[] { true, false })
                {
                    float line; float lo, hi;
                    if (vertical)
                    {
                        if (MathF.Abs(a.Max.X - b.Min.X) < 0.05f) line = a.Max.X; else if (MathF.Abs(b.Max.X - a.Min.X) < 0.05f) line = a.Min.X; else continue;
                        lo = MathF.Max(a.Min.Y, b.Min.Y) + 0.2f; hi = MathF.Min(a.Max.Y, b.Max.Y) - 0.2f;
                    }
                    else
                    {
                        if (MathF.Abs(a.Max.Y - b.Min.Y) < 0.05f) line = a.Max.Y; else if (MathF.Abs(b.Max.Y - a.Min.Y) < 0.05f) line = a.Min.Y; else continue;
                        lo = MathF.Max(a.Min.X, b.Min.X) + 0.2f; hi = MathF.Min(a.Max.X, b.Max.X) - 0.2f;
                    }
                    if (hi - lo < 0.6f) continue;
                    // walk along the wall; a door is where every sample across the wall is free
                    float step = MathF.Max(mpp, 0.02f), runStart = -1;
                    for (float t = lo; t <= hi + step; t += step)
                    {
                        bool free = t <= hi;
                        if (free)
                            for (float c = -0.25f; c <= 0.25f; c += step)
                                if ((vertical ? Lum(line + c, t) : Lum(t, line + c)) < 0.5f) { free = false; break; }
                        if (free && runStart < 0) runStart = t;
                        if (!free && runStart >= 0)
                        {
                            float len = t - runStart;
                            if (len >= 0.6f)
                            {
                                float mid = (runStart + t) * 0.5f;
                                res.Doors.Add(new PlanOpening { X = vertical ? line : mid, Y = vertical ? mid : line, Width = MathF.Min(MathF.Round(len * 10) / 10, 1.6f), Height = 2.3f });
                            }
                            runStart = -1;
                        }
                    }
                }
            }
        if (res.Rooms.Count == 0) res.Notes.Add("No closed room found: try another threshold or a cleaner picture (dark walls on a light background).");
        return res;
    }

    static bool[] Dilate(bool[] m, int W, int H, int r)
    {
        // separable square dilation
        var tmp = new bool[m.Length]; var outp = new bool[m.Length];
        for (int y = 0; y < H; y++)
        {
            int last = -100000;
            for (int x = 0; x < W; x++) { if (m[y * W + x]) last = x; tmp[y * W + x] = x - last <= r; }
            last = 100000;
            for (int x = W - 1; x >= 0; x--) { if (m[y * W + x]) last = x; if (last - x <= r) tmp[y * W + x] = true; }
        }
        for (int x = 0; x < W; x++)
        {
            int last = -100000;
            for (int y = 0; y < H; y++) { if (tmp[y * W + x]) last = y; outp[y * W + x] = y - last <= r; }
            last = 100000;
            for (int y = H - 1; y >= 0; y--) { if (tmp[y * W + x]) last = y; if (last - y <= r) outp[y * W + x] = true; }
        }
        return outp;
    }

    static float Otsu(float[] v)
    {
        var hist = new int[256];
        foreach (var x in v) hist[Math.Clamp((int)(x * 255), 0, 255)]++;
        long total = v.Length; double sum = 0; for (int i = 0; i < 256; i++) sum += i * (double)hist[i];
        double sumB = 0, best = -1; long wB = 0; double cut = 128;
        for (int i = 0; i < 256; i++)
        {
            wB += hist[i]; if (wB == 0) continue;
            long wF = total - wB; if (wF == 0) break;
            sumB += i * (double)hist[i];
            double mB = sumB / wB, mF = (sum - sumB) / wF;
            double between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > best) { best = between; cut = (mB + mF) * 0.5; } // halfway between the two classes
        }
        return MathF.Min((float)(cut / 255.0), 0.6f);
    }
}

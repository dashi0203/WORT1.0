using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>二维网格工具：模糊、形态学、连通域、距离变换。索引 i = z * W + x。</summary>
    public static class Grid
    {
        public static readonly int[] DX4 = { 0, 1, 0, -1 };
        public static readonly int[] DZ4 = { 1, 0, -1, 0 };

        /// <summary>可分离盒式模糊，边缘夹取。</summary>
        public static float[] BoxBlur(float[] src, int w, int h, int r)
        {
            var tmp = new float[src.Length];
            var dst = new float[src.Length];
            float inv = 1f / (2 * r + 1);
            for (int z = 0; z < h; z++)
            {
                double sum = 0;
                for (int k = -r; k <= r; k++) sum += src[z * w + Clamp(k, 0, w - 1)];
                for (int x = 0; x < w; x++)
                {
                    tmp[z * w + x] = (float)(sum * inv);
                    sum += src[z * w + Clamp(x + r + 1, 0, w - 1)] - src[z * w + Clamp(x - r, 0, w - 1)];
                }
            }
            for (int x = 0; x < w; x++)
            {
                double sum = 0;
                for (int k = -r; k <= r; k++) sum += tmp[Clamp(k, 0, h - 1) * w + x];
                for (int z = 0; z < h; z++)
                {
                    dst[z * w + x] = (float)(sum * inv);
                    sum += tmp[Clamp(z + r + 1, 0, h - 1) * w + x] - tmp[Clamp(z - r, 0, h - 1) * w + x];
                }
            }
            return dst;
        }

        public static int Clamp(int v, int lo, int hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>方形结构元的膨胀（半径 r）。区域外视为 false。</summary>
        public static bool[] Dilate(bool[] m, int w, int h, int r) => MinMax(m, w, h, r, true);

        /// <summary>方形结构元的腐蚀（半径 r）。区域外视为 true（不在边界处侵蚀）。</summary>
        public static bool[] Erode(bool[] m, int w, int h, int r) => MinMax(m, w, h, r, false);

        static bool[] MinMax(bool[] m, int w, int h, int r, bool dilate)
        {
            if (r <= 0) return (bool[])m.Clone();
            // 膨胀：窗口内有 true 即 true；腐蚀：窗口内（区域内部分）没有 false 才为 true。
            bool v = dilate;
            var tmp = new bool[m.Length];
            var dst = new bool[m.Length];
            for (int z = 0; z < h; z++)
            {
                int count = 0;
                for (int x = 0; x <= r && x < w; x++) if (m[z * w + x] == v) count++;
                for (int x = 0; x < w; x++)
                {
                    tmp[z * w + x] = dilate ? count > 0 : count == 0;
                    int xa = x + r + 1, xr = x - r;
                    if (xa < w && m[z * w + xa] == v) count++;
                    if (xr >= 0 && m[z * w + xr] == v) count--;
                }
            }
            for (int x = 0; x < w; x++)
            {
                int count = 0;
                for (int z = 0; z <= r && z < h; z++) if (tmp[z * w + x] == v) count++;
                for (int z = 0; z < h; z++)
                {
                    dst[z * w + x] = dilate ? count > 0 : count == 0;
                    int za = z + r + 1, zr = z - r;
                    if (za < h && tmp[za * w + x] == v) count++;
                    if (zr >= 0 && tmp[zr * w + x] == v) count--;
                }
            }
            return dst;
        }

        public static bool[] Open(bool[] m, int w, int h, int r) => Dilate(Erode(m, w, h, r), w, h, r);
        public static bool[] Close(bool[] m, int w, int h, int r) => Erode(Dilate(m, w, h, r), w, h, r);

        /// <summary>
        /// 4 连通标记。connected(a, b) 判断相邻两格是否连通；include(i) 为 false 的格子标记为 -1。
        /// 返回每格的连通域编号，sizes 为各连通域面积。
        /// </summary>
        public static int[] Label(int w, int h, Func<int, bool> include, Func<int, int, bool> connected, out List<int> sizes)
        {
            var label = new int[w * h];
            for (int i = 0; i < label.Length; i++) label[i] = -1;
            sizes = new List<int>();
            var stack = new Stack<int>();
            for (int s = 0; s < label.Length; s++)
            {
                if (label[s] >= 0 || !include(s)) continue;
                int id = sizes.Count;
                int size = 0;
                label[s] = id;
                stack.Push(s);
                while (stack.Count > 0)
                {
                    int i = stack.Pop();
                    size++;
                    int x = i % w, z = i / w;
                    for (int d = 0; d < 4; d++)
                    {
                        int nx = x + DX4[d], nz = z + DZ4[d];
                        if (nx < 0 || nz < 0 || nx >= w || nz >= h) continue;
                        int n = nz * w + nx;
                        if (label[n] >= 0 || !include(n) || !connected(i, n)) continue;
                        label[n] = id;
                        stack.Push(n);
                    }
                }
                sizes.Add(size);
            }
            return label;
        }

        /// <summary>移除面积小于 minArea 的 true 连通块。</summary>
        public static bool[] RemoveSmall(bool[] m, int w, int h, int minArea)
        {
            var lab = Label(w, h, i => m[i], (a, b) => true, out var sizes);
            var dst = new bool[m.Length];
            for (int i = 0; i < m.Length; i++) dst[i] = m[i] && sizes[lab[i]] >= minArea;
            return dst;
        }

        /// <summary>倒角距离变换（3-4 近似欧氏，单位米）：到最近 false 格的距离。</summary>
        public static float[] DistanceInside(bool[] m, int w, int h)
        {
            const float big = 1e9f;
            var d = new float[m.Length];
            for (int i = 0; i < m.Length; i++) d[i] = m[i] ? big : 0;
            for (int z = 0; z < h; z++)
                for (int x = 0; x < w; x++)
                {
                    int i = z * w + x;
                    if (d[i] == 0) continue;
                    float v = d[i];
                    if (x > 0) v = Math.Min(v, d[i - 1] + 1);
                    if (z > 0) v = Math.Min(v, d[i - w] + 1);
                    if (x > 0 && z > 0) v = Math.Min(v, d[i - w - 1] + 1.4142f);
                    if (x < w - 1 && z > 0) v = Math.Min(v, d[i - w + 1] + 1.4142f);
                    d[i] = v;
                }
            for (int z = h - 1; z >= 0; z--)
                for (int x = w - 1; x >= 0; x--)
                {
                    int i = z * w + x;
                    if (d[i] == 0) continue;
                    float v = d[i];
                    if (x < w - 1) v = Math.Min(v, d[i + 1] + 1);
                    if (z < h - 1) v = Math.Min(v, d[i + w] + 1);
                    if (x < w - 1 && z < h - 1) v = Math.Min(v, d[i + w + 1] + 1.4142f);
                    if (x > 0 && z < h - 1) v = Math.Min(v, d[i + w - 1] + 1.4142f);
                    d[i] = v;
                }
            return d;
        }
    }
}

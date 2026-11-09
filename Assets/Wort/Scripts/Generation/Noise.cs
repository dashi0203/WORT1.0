using System;

namespace Wort.Generation
{
    /// <summary>
    /// 确定性哈希与噪声。只用整数运算和 IEEE double 的加减乘，
    /// 保证 .NET / Mono / IL2CPP 下结果一致（不使用 System.Random、Math.Sin 等）。
    /// </summary>
    public static class Hash
    {
        public static uint Mix(uint x)
        {
            x ^= x >> 16; x *= 0x7feb352dU;
            x ^= x >> 15; x *= 0x846ca68bU;
            x ^= x >> 16;
            return x;
        }

        public static uint Of(int x, int y, uint seed)
        {
            return Mix(unchecked((uint)x * 0x8da6b343U) ^ Mix(unchecked((uint)y * 0xd8163841U) ^ seed));
        }

        public static uint Of(uint a, uint b) => Mix(a ^ Mix(b + 0x9e3779b9U));

        /// <summary>[0,1) 均匀值。</summary>
        public static double Unit(uint h) => (h >> 8) * (1.0 / 16777216.0);

        /// <summary>由字符串派生子种子（FNV-1a），各用途互不相关。</summary>
        public static uint Derive(uint seed, string name)
        {
            uint h = 2166136261U;
            for (int i = 0; i < name.Length; i++) { h ^= name[i]; h *= 16777619U; }
            return Of(seed, h);
        }
    }

    /// <summary>二维梯度噪声（Perlin 风格），输出约在 [-1, 1]。</summary>
    public sealed class Perlin2
    {
        static readonly double[] GX = { 1, -1, 0, 0, 0.70710678118654752, -0.70710678118654752, 0.70710678118654752, -0.70710678118654752 };
        static readonly double[] GY = { 0, 0, 1, -1, 0.70710678118654752, 0.70710678118654752, -0.70710678118654752, -0.70710678118654752 };
        readonly uint seed;

        public Perlin2(uint seed) { this.seed = seed; }

        double Grad(int ix, int iy, double dx, double dy)
        {
            int g = (int)(Hash.Of(ix, iy, seed) & 7);
            return GX[g] * dx + GY[g] * dy;
        }

        static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

        public double Sample(double x, double y)
        {
            int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
            double fx = x - x0, fy = y - y0;
            double u = Fade(fx), v = Fade(fy);
            double a = Grad(x0, y0, fx, fy);
            double b = Grad(x0 + 1, y0, fx - 1, fy);
            double c = Grad(x0, y0 + 1, fx, fy - 1);
            double d = Grad(x0 + 1, y0 + 1, fx - 1, fy - 1);
            double ab = a + (b - a) * u;
            double cd = c + (d - c) * u;
            return (ab + (cd - ab) * v) * 1.41421356237;
        }
    }

    /// <summary>分形噪声：wavelength 为第一层波长（米），输出约在 [-1, 1]。</summary>
    public sealed class Fbm
    {
        readonly Perlin2[] layers;
        readonly double frequency;
        readonly double norm;

        public Fbm(uint seed, double wavelength, int octaves)
        {
            layers = new Perlin2[octaves];
            for (int i = 0; i < octaves; i++) layers[i] = new Perlin2(Hash.Of(seed, (uint)i + 1));
            frequency = 1.0 / wavelength;
            double amp = 1, sum = 0;
            for (int i = 0; i < octaves; i++) { sum += amp; amp *= 0.5; }
            norm = 1.0 / sum;
        }

        public double Sample(double x, double z)
        {
            double f = frequency, amp = 1, sum = 0;
            for (int i = 0; i < layers.Length; i++)
            {
                // 每层错开一点，避免各层格点对齐
                sum += amp * layers[i].Sample(x * f + i * 17.31, z * f - i * 9.77);
                f *= 2; amp *= 0.5;
            }
            return sum * norm;
        }
    }

    /// <summary>
    /// 噪声分位数表：在固定的大范围世界坐标上采样噪声，得到经验分布。
    /// 用于“按覆盖率取阈值”（覆盖率 c → 阈值 Quantile(1 - c)），与具体区域无关。
    /// </summary>
    public sealed class QuantileTable
    {
        readonly double[] sorted;

        public QuantileTable(Func<double, double, double> field, int samplesPerAxis = 160, double spacing = 29.37, double origin = -2300)
        {
            sorted = new double[samplesPerAxis * samplesPerAxis];
            int k = 0;
            for (int j = 0; j < samplesPerAxis; j++)
                for (int i = 0; i < samplesPerAxis; i++)
                    sorted[k++] = field(origin + i * spacing, origin + j * spacing);
            Array.Sort(sorted);
        }

        public double Quantile(double p)
        {
            if (p <= 0) return double.NegativeInfinity;
            if (p >= 1) return double.PositiveInfinity;
            double pos = p * (sorted.Length - 1);
            int i = (int)pos;
            double t = pos - i;
            return i + 1 < sorted.Length ? sorted[i] + (sorted[i + 1] - sorted[i]) * t : sorted[i];
        }
    }
}

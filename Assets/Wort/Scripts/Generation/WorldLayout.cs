using System;
using System.Collections.Generic;

namespace Wort.Generation
{
    /// <summary>区域类型（Docs/WorldGen.md 5.2）。</summary>
    public enum RegionKind : byte
    {
        Open = 0,     // 开放区：自己的边界不成崖
        Plateau = 1,  // 台地区：一圈崖，只在出入口有坡道
        Basin = 2,    // 盆地：一圈崖，跳进去原路出不来
        Canyon = 3,   // 峡谷：两侧长崖，谷底有自己的出入口
        Highland = 4, // 高原：外缘可以 7 层以上
    }

    public enum GateType : byte { Ramp = 0, Stairs = 1 }
    public enum SpanType : byte { Bridge = 0, Tunnel = 1 }

    /// <summary>
    /// 有机形状：椭圆、平滑闭合曲线（blob）、粗线条（stroke）、全图（fill）。
    /// 边缘叠一层世界坐标噪声扰动（Docs/WorldGen.md 第 1 节：大于 5×5m 的形状一律有机）。
    /// </summary>
    public sealed class LayoutShape
    {
        public string Type = "fill";
        public double CX, CZ, RX, RZ;          // ellipse
        public double[] PX, PZ;                // blob：平滑后的闭合折线；stroke：折线
        public double Width;                   // stroke 宽度（米）
        public double Warp = -1;               // 边缘扰动幅度（米），-1 = 按大小自动
        public double MinX, MinZ, MaxX, MaxZ;  // 外接框（含扰动余量）

        /// <summary>近似有符号距离（米）：内部为负。不含扰动。</summary>
        public double Distance(double x, double z)
        {
            switch (Type)
            {
                case "fill": return -1e9;
                case "ellipse":
                {
                    double dx = (x - CX) / RX, dz = (z - CZ) / RZ;
                    double r = Math.Sqrt(dx * dx + dz * dz);
                    return (r - 1) * Math.Min(RX, RZ);
                }
                case "blob":
                {
                    double d = SegmentsDistance(x, z, true);
                    return Inside(x, z) ? -d : d;
                }
                default: // stroke
                    return SegmentsDistance(x, z, false) - Width * 0.5;
            }
        }

        double SegmentsDistance(double x, double z, bool closed)
        {
            int n = PX.Length;
            double best = double.MaxValue;
            int segs = closed ? n : n - 1;
            for (int k = 0; k < segs; k++)
            {
                int k2 = (k + 1) % n;
                double ax = PX[k], az = PZ[k], bx = PX[k2], bz = PZ[k2];
                double vx = bx - ax, vz = bz - az;
                double len2 = vx * vx + vz * vz;
                double t = len2 > 0 ? ((x - ax) * vx + (z - az) * vz) / len2 : 0;
                if (t < 0) t = 0; else if (t > 1) t = 1;
                double qx = ax + vx * t - x, qz = az + vz * t - z;
                double d2 = qx * qx + qz * qz;
                if (d2 < best) best = d2;
            }
            return Math.Sqrt(best);
        }

        bool Inside(double x, double z)
        {
            bool inside = false;
            int n = PX.Length;
            for (int k = 0, j = n - 1; k < n; j = k++)
            {
                if ((PZ[k] > z) != (PZ[j] > z) &&
                    x < (PX[j] - PX[k]) * (z - PZ[k]) / (PZ[j] - PZ[k]) + PX[k])
                    inside = !inside;
            }
            return inside;
        }

        /// <summary>形状的特征尺寸（米），用来定扰动幅度和波长。</summary>
        public double Size
        {
            get
            {
                switch (Type)
                {
                    case "ellipse": return Math.Min(RX, RZ) * 2;
                    case "stroke": return Width;
                    case "blob": return Math.Min(MaxX - MinX, MaxZ - MinZ);
                    default: return 1e9;
                }
            }
        }

        public double WarpAmp => Warp >= 0 ? Warp : Math.Max(2.5, Math.Min(20.0, Size * 0.18));

        // ---- 有机化：坐标扭曲（大尺度凸起 / 凹湾 + 小尺度缺口）、线条宽度变化、崖线粗糙 ----
        Fbm bigX, bigZ, smallX, smallZ, rough, widthVar;
        double aBig, aSmall;
        bool inited;

        static double Clamp(double v, double lo, double hi) => v < lo ? lo : (v > hi ? hi : v);

        /// <summary>按种子建好扰动场，并把外接框放大到扰动能到达的范围。</summary>
        public void Init(uint seed)
        {
            if (Type == "fill") return;
            bool first = !inited;
            inited = true;
            bool stroke = Type == "stroke";
            double size = Size;
            aBig = stroke ? (Warp >= 0 ? Warp : Clamp(Width * 0.8, 4, 16)) : WarpAmp;
            double wlBig = stroke ? Clamp(Width * 5, 40, 120) : Clamp(size * 0.7, 16, 160);
            aSmall = Clamp(size * 0.05, 1, 4);
            double wlSmall = Clamp(size * 0.15, 5, 18);
            bigX = new Fbm(Hash.Of(seed, 1), wlBig, 3);
            bigZ = new Fbm(Hash.Of(seed, 2), wlBig, 3);
            smallX = new Fbm(Hash.Of(seed, 3), wlSmall, 2);
            smallZ = new Fbm(Hash.Of(seed, 4), wlSmall, 2);
            rough = new Fbm(Hash.Of(seed, 5), 7, 2);
            if (stroke) widthVar = new Fbm(Hash.Of(seed, 6), Clamp(Width * 3, 24, 80), 2);
            // Fbm 输出约在 ±1，典型 ±0.5：外接框按 2 倍幅度放大
            if (!first) return;
            double m = 2 * (aBig + aSmall) + (stroke ? Width * 0.6 : 0) + 2;
            MinX -= m; MinZ -= m; MaxX += m; MaxZ += m;
        }

        /// <summary>有机化后的有符号距离（米），内部为负。x, z 为 layout 坐标（米）。</summary>
        public double Organic(double x, double z)
        {
            if (Type == "fill") return -1e9;
            // Fbm 的典型幅度约 0.5，乘 2 让 aBig / aSmall 接近实际位移
            double wx = x + 2 * aBig * bigX.Sample(x, z) + 2 * aSmall * smallX.Sample(x, z);
            double wz = z + 2 * aBig * bigZ.Sample(x, z) + 2 * aSmall * smallZ.Sample(x, z);
            double d;
            if (Type == "stroke")
            {
                // 宽度沿长度变化 0.55–1.6 倍
                double k = 1 + 1.3 * widthVar.Sample(x, z);
                if (k < 0.55) k = 0.55; else if (k > 1.6) k = 1.6;
                d = SegmentsDistance(wx, wz, false) - Width * 0.5 * k;
            }
            else d = Distance(wx, wz);
            return d + 0.6 * rough.Sample(x, z);
        }

        /// <summary>Chaikin 细分：把控制点围成的多边形磨成平滑曲线。</summary>
        public static void Chaikin(List<double> xs, List<double> zs, bool closed, int iterations)
        {
            for (int it = 0; it < iterations; it++)
            {
                var nx = new List<double>();
                var nz = new List<double>();
                int n = xs.Count;
                int segs = closed ? n : n - 1;
                if (!closed) { nx.Add(xs[0]); nz.Add(zs[0]); }
                for (int k = 0; k < segs; k++)
                {
                    int k2 = (k + 1) % n;
                    nx.Add(xs[k] * 0.75 + xs[k2] * 0.25); nz.Add(zs[k] * 0.75 + zs[k2] * 0.25);
                    nx.Add(xs[k] * 0.25 + xs[k2] * 0.75); nz.Add(zs[k] * 0.25 + zs[k2] * 0.75);
                }
                if (!closed) { nx.Add(xs[n - 1]); nz.Add(zs[n - 1]); }
                xs.Clear(); xs.AddRange(nx);
                zs.Clear(); zs.AddRange(nz);
            }
        }
    }

    public sealed class LayoutRegion
    {
        public string Id;
        public string Parent;
        public RegionKind Kind;
        /// <summary>基准层级；有父区域时相对父区域。</summary>
        public int Level;
        public BiomeId Biome;
        public LayoutShape Shape;

        // 解析后填写
        public int Index;
        public int AbsLevel;
        public int Depth;
        public bool IsOpen => Kind == RegionKind.Open;
    }

    public sealed class LayoutGate
    {
        public string Id;
        public GateType Type;
        /// <summary>坡脚附近的一点（低的一侧）。</summary>
        public int X, Z;
        /// <summary>上坡方向（Grid.DX4/DZ4 索引：0=北 1=东 2=南 3=西）。</summary>
        public int Up;
        public int Width = 3;
        /// <summary>坡道通往的区域（高的一侧）。为空时取离 at 最近的崖。</summary>
        public string To;
    }

    public sealed class SiteTemplate
    {
        public string Id;
        /// <summary>外接半径（米），占地是带扰动的椭圆。</summary>
        public double RX, RZ;
        public double Buffer = 4;
        /// <summary>flat = 平整；cliffBehind = 背后要有一面崖（CliffDir 方向，至少 CliffMin 层）。</summary>
        public string Terrain = "flat";
        public int CliffDir = -1;
        public int CliffMin;
        public string Prefab;
    }

    public sealed class LayoutSite
    {
        public string Id;
        public string Template;
        public int X, Z;
        public SiteTemplate T;
    }

    public sealed class LayoutRoad
    {
        public string Id;
        public double[] PX, PZ;
        public double Width = 3;
        /// <summary>小径不压平微起伏以外的地形，只是更窄。</summary>
        public bool Trail;
    }

    public sealed class LayoutSpan
    {
        public string Id;
        public SpanType Type;
        public int X0, Z0, X1, Z1;
        public int Width = 3;
        /// <summary>只给检查用：true = 桥边有护栏，从桥面跳不下去。</summary>
        public bool Railing = true;
        /// <summary>隧道净空（层）。</summary>
        public int Clear = 3;
    }

    public sealed class LayoutConstraint
    {
        public string Type; // minWalk
        public string A, B;
        public double Meters;
    }

    /// <summary>世界布局（Docs/WorldGen.md 第 5 节）。拓扑只由它决定，生成器只填细节。</summary>
    public sealed class WorldLayout
    {
        public string Name;
        public uint Seed = 1;
        public int ChunksX = 8, ChunksZ = 8;
        public string Home;
        public readonly List<LayoutRegion> Regions = new List<LayoutRegion>();
        public readonly List<LayoutGate> Gates = new List<LayoutGate>();
        public readonly List<LayoutSite> Sites = new List<LayoutSite>();
        public readonly List<LayoutRoad> Roads = new List<LayoutRoad>();
        public readonly List<LayoutSpan> Spans = new List<LayoutSpan>();
        public readonly List<LayoutConstraint> Constraints = new List<LayoutConstraint>();
        public readonly Dictionary<string, SiteTemplate> Templates = new Dictionary<string, SiteTemplate>();

        public LayoutRegion Region(string id)
        {
            foreach (var r in Regions) if (r.Id == id) return r;
            return null;
        }

        public LayoutSite Site(string id)
        {
            foreach (var s in Sites) if (s.Id == id) return s;
            return null;
        }

        // ---------------- 解析 ----------------

        public static WorldLayout Parse(string layoutJson, string templatesJson)
        {
            var L = new WorldLayout();
            if (templatesJson != null)
            {
                var t = (Dictionary<string, object>)Json.Parse(templatesJson);
                foreach (var o in Arr(t, "templates")) L.AddTemplate((Dictionary<string, object>)o);
            }
            var d = (Dictionary<string, object>)Json.Parse(layoutJson);
            L.Name = Str(d, "name", "layout");
            L.Seed = (uint)Num(d, "seed", 1);
            if (d.TryGetValue("chunks", out var ch))
            {
                var a = (List<object>)ch;
                L.ChunksX = (int)(double)a[0]; L.ChunksZ = (int)(double)a[1];
            }
            L.Home = Str(d, "home", null);
            foreach (var o in Arr(d, "templates")) L.AddTemplate((Dictionary<string, object>)o);
            foreach (var o in Arr(d, "regions"))
            {
                var r = (Dictionary<string, object>)o;
                L.Regions.Add(new LayoutRegion
                {
                    Id = Str(r, "id", null),
                    Parent = Str(r, "parent", null),
                    Kind = ParseKind(Str(r, "kind", "open")),
                    Level = (int)Num(r, "level", 0),
                    Biome = ParseBiome(Str(r, "biome", "mixedForest")),
                    Shape = ParseShape(r.TryGetValue("shape", out var sh) ? (Dictionary<string, object>)sh : null),
                });
            }
            foreach (var o in Arr(d, "gates"))
            {
                var g = (Dictionary<string, object>)o;
                var at = (List<object>)g["at"];
                L.Gates.Add(new LayoutGate
                {
                    Id = Str(g, "id", null),
                    Type = Str(g, "type", "ramp") == "stairs" ? GateType.Stairs : GateType.Ramp,
                    X = (int)(double)at[0], Z = (int)(double)at[1],
                    Up = ParseDir(Str(g, "up", "N")),
                    Width = (int)Num(g, "width", 3),
                    To = Str(g, "to", null),
                });
            }
            foreach (var o in Arr(d, "sites"))
            {
                var s = (Dictionary<string, object>)o;
                var at = (List<object>)s["at"];
                var site = new LayoutSite
                {
                    Id = Str(s, "id", null),
                    Template = Str(s, "template", null),
                    X = (int)(double)at[0], Z = (int)(double)at[1],
                };
                if (!L.Templates.TryGetValue(site.Template, out site.T))
                    throw new FormatException($"地点 {site.Id} 的模板 {site.Template} 不存在");
                L.Sites.Add(site);
            }
            foreach (var o in Arr(d, "roads"))
            {
                var r = (Dictionary<string, object>)o;
                Points(r, "points", out var px, out var pz);
                L.Roads.Add(new LayoutRoad
                {
                    Id = Str(r, "id", null),
                    PX = px, PZ = pz,
                    Trail = Str(r, "kind", "road") == "trail",
                    Width = Num(r, "width", Str(r, "kind", "road") == "trail" ? 2 : 3),
                });
            }
            foreach (var o in Arr(d, "spans"))
            {
                var s = (Dictionary<string, object>)o;
                var a = (List<object>)s["from"];
                var b = (List<object>)s["to"];
                L.Spans.Add(new LayoutSpan
                {
                    Id = Str(s, "id", null),
                    Type = Str(s, "type", "bridge") == "tunnel" ? SpanType.Tunnel : SpanType.Bridge,
                    X0 = (int)(double)a[0], Z0 = (int)(double)a[1],
                    X1 = (int)(double)b[0], Z1 = (int)(double)b[1],
                    Width = (int)Num(s, "width", 3),
                    Railing = !s.TryGetValue("railing", out var rl) || (bool)rl,
                    Clear = (int)Num(s, "clear", 3),
                });
            }
            foreach (var o in Arr(d, "constraints"))
            {
                var c = (Dictionary<string, object>)o;
                L.Constraints.Add(new LayoutConstraint
                {
                    Type = Str(c, "type", null),
                    A = Str(c, "a", null), B = Str(c, "b", null),
                    Meters = Num(c, "meters", 0),
                });
            }
            L.Resolve();
            return L;
        }

        void AddTemplate(Dictionary<string, object> t)
        {
            var size = (List<object>)t["size"];
            var tp = new SiteTemplate
            {
                Id = Str(t, "id", null),
                RX = (double)size[0] * 0.5,
                RZ = (double)size[1] * 0.5,
                Buffer = Num(t, "buffer", 4),
                Terrain = Str(t, "terrain", "flat"),
                Prefab = Str(t, "prefab", null),
            };
            if (tp.Terrain == "cliffBehind")
            {
                tp.CliffDir = ParseDir(Str(t, "cliffDir", "N"));
                tp.CliffMin = (int)Num(t, "cliffMin", 3);
            }
            Templates[tp.Id] = tp;
        }

        void Resolve()
        {
            for (int i = 0; i < Regions.Count; i++) Regions[i].Index = i;
            foreach (var r in Regions)
            {
                int depth = 0, abs = r.Level;
                var p = r.Parent;
                var seen = new HashSet<string> { r.Id };
                while (p != null)
                {
                    var pr = Region(p) ?? throw new FormatException($"区域 {r.Id} 的父区域 {p} 不存在");
                    if (!seen.Add(pr.Id)) throw new FormatException($"区域 {r.Id} 的父区域成环");
                    abs += pr.Level;
                    depth++;
                    p = pr.Parent;
                }
                r.Depth = depth;
                r.AbsLevel = abs;
            }
            if (Home != null && Site(Home) == null) throw new FormatException($"home 地点 {Home} 不存在");
        }

        static LayoutShape ParseShape(Dictionary<string, object> s)
        {
            var shape = new LayoutShape();
            if (s == null) return shape;
            shape.Type = Str(s, "type", "fill");
            shape.Warp = Num(s, "warp", -1);
            switch (shape.Type)
            {
                case "fill":
                    shape.MinX = shape.MinZ = -1e9; shape.MaxX = shape.MaxZ = 1e9;
                    break;
                case "ellipse":
                {
                    var c = (List<object>)s["center"];
                    var r = (List<object>)s["radius"];
                    shape.CX = (double)c[0]; shape.CZ = (double)c[1];
                    shape.RX = (double)r[0]; shape.RZ = (double)r[1];
                    shape.MinX = shape.CX - shape.RX; shape.MaxX = shape.CX + shape.RX;
                    shape.MinZ = shape.CZ - shape.RZ; shape.MaxZ = shape.CZ + shape.RZ;
                    break;
                }
                case "blob":
                case "stroke":
                {
                    Points(s, "points", out var px, out var pz);
                    var xs = new List<double>(px);
                    var zs = new List<double>(pz);
                    LayoutShape.Chaikin(xs, zs, shape.Type == "blob", 3);
                    shape.PX = xs.ToArray(); shape.PZ = zs.ToArray();
                    shape.Width = Num(s, "width", 10);
                    shape.MinX = shape.MinZ = double.MaxValue; shape.MaxX = shape.MaxZ = double.MinValue;
                    for (int k = 0; k < xs.Count; k++)
                    {
                        shape.MinX = Math.Min(shape.MinX, xs[k]); shape.MaxX = Math.Max(shape.MaxX, xs[k]);
                        shape.MinZ = Math.Min(shape.MinZ, zs[k]); shape.MaxZ = Math.Max(shape.MaxZ, zs[k]);
                    }
                    if (shape.Type == "stroke")
                    {
                        double h = shape.Width * 0.5;
                        shape.MinX -= h; shape.MinZ -= h; shape.MaxX += h; shape.MaxZ += h;
                    }
                    break;
                }
                default: throw new FormatException("未知形状类型 " + shape.Type);
            }
            return shape;
        }

        static RegionKind ParseKind(string s)
        {
            switch (s)
            {
                case "open": return RegionKind.Open;
                case "plateau": return RegionKind.Plateau;
                case "basin": return RegionKind.Basin;
                case "canyon": return RegionKind.Canyon;
                case "highland": return RegionKind.Highland;
                default: throw new FormatException("未知区域类型 " + s);
            }
        }

        static BiomeId ParseBiome(string s)
        {
            switch (s)
            {
                case "human": return BiomeId.Human;
                case "mixedForest": return BiomeId.MixedForest;
                case "conifer": return BiomeId.Conifer;
                case "swamp": return BiomeId.Swamp;
                case "mountain": return BiomeId.Mountain;
                case "moor": return BiomeId.Moor;
                case "coast": return BiomeId.Coast;
                default: throw new FormatException("未知 Biome " + s);
            }
        }

        static int ParseDir(string s)
        {
            switch (s)
            {
                case "N": return 0;
                case "E": return 1;
                case "S": return 2;
                case "W": return 3;
                default: throw new FormatException("方向只能是 N/E/S/W：" + s);
            }
        }

        static void Points(Dictionary<string, object> d, string key, out double[] px, out double[] pz)
        {
            var a = (List<object>)d[key];
            px = new double[a.Count]; pz = new double[a.Count];
            for (int i = 0; i < a.Count; i++)
            {
                var p = (List<object>)a[i];
                px[i] = (double)p[0]; pz[i] = (double)p[1];
            }
        }

        static List<object> Arr(Dictionary<string, object> d, string key) =>
            d.TryGetValue(key, out var v) && v is List<object> l ? l : new List<object>();

        static string Str(Dictionary<string, object> d, string key, string def) =>
            d.TryGetValue(key, out var v) && v is string s ? s : def;

        static double Num(Dictionary<string, object> d, string key, double def) =>
            d.TryGetValue(key, out var v) && v is double x ? x : def;
    }
}

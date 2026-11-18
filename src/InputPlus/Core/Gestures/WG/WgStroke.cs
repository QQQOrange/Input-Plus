using System;
using System.Collections.Generic;

namespace InputPlus.Core.Gestures.WG;

/// <summary>
/// 一笔：一条按采样点顺序记下来的折线，外加它"形状签名"的惰性缓存。
/// 对应反编译源码 A/P.cs 里的 <c>a.p</c>（Stroke 步骤）。
///
/// 这里做的两件事，是整套识别的地基：
/// 1. 采点时把"方向几乎没变"的中间点吃掉（见 <see cref="Add"/>），
///    否则鼠标每移动 1 像素就一个点，形状里全是抖动；
/// 2. 把折线换成 (s, θ) 序列：s = 归一化累计弧长、θ = 方向角 / π。
///    换成这条曲线以后，缩放、平移、采样密度都被抹平，只剩"形状"。
/// </summary>
public sealed class WgStroke
{
    /// <summary>
    /// 合并门限：新一段的方向与上一段相差小于这个值（单位 π，即 18°）时，
    /// 不新增顶点，而是把末点挪到新点上。同源码 A/P.cs:239 的
    /// <c>Math.Abs(g.A_m(A_f5, num)) &lt; 0.10000000149011612</c>。
    ///
    /// 源码里那个 0.10000000149 是 float 0.1 提升成 double 的结果
    /// （0.1f → 0.100000001490116…），说明作者写的是 float 字面量。
    /// 这里直接写 0.1（double）：两者相差 1.5e-9，只在恰好压线的角度上
    /// 可能翻转一次合并判定，实际采样数据碰不到。
    /// </summary>
    public const double MergeAngle = 0.1;

    /// <summary>参数侧（模板侧）点数上限，超过直接判不可比。同源码 A/P.cs:307。</summary>
    public const int MaxPoints = 128;

    /// <summary>两侧点数比上限，超过直接判不可比。同源码 A/P.cs:313。</summary>
    public const double MaxPointRatio = 10.0;

    /// <summary>合并后的折点（已取整）。</summary>
    private readonly List<WgVec2> _pts = new List<WgVec2>(64);

    /// <summary>每个顶点的出边方向角（π 单位），末顶点固定为 0。与 <see cref="_pts"/> 等长。</summary>
    private readonly List<WgVec2> _sig = new List<WgVec2>(64);

    /// <summary>累计弧长（未归一化）。与 <see cref="_sig"/> 等长。</summary>
    private readonly List<double> _cum = new List<double>(64);

    /// <summary>上一段的方向角，18° 合并的基准。同源码的 A_f5。</summary>
    private double _lastDir;

    private WgVec2[]? _sigCache;

    public WgStroke()
    {
    }

    /// <summary>合并后的折点数。</summary>
    public int Count => _pts.Count;

    /// <summary>合并后的折点。只读视图，别往下转型去改。</summary>
    public IReadOnlyList<WgVec2> Points => _pts;

    /// <summary>折线总长（像素）。不足两点时为 0。</summary>
    public double Length => _cum.Count > 0 ? _cum[_cum.Count - 1] : 0.0;

    /// <summary>
    /// 增量采点：把鼠标位置喂进来。逐字移植 A/P.cs:214 的 <c>p.A(Vec2)</c>。
    ///
    /// 顺序很重要：先取整、再算与末点的方向、再和"上一段方向"比。
    /// 差值 &lt; 18° 就认为还在同一条直线上，把末点替换掉（顶点数不变）；
    /// 否则追加顶点。前两个点特殊处理：它们同时建立方向表与弧长表的初值。
    /// </summary>
    public void Add(double x, double y)
    {
        WgVec2 p = new WgVec2(x, y).Rounded();
        _sigCache = null;

        if (_pts.Count < 2)
        {
            _pts.Add(p);
            if (_pts.Count == 2)
            {
                WgVec2 v = _pts[1] - _pts[0];
                _sig.Add(new WgVec2(0.0, WgMath.Direction(v)));
                _cum.Add(0.0);
                _sig.Add(new WgVec2(0.0, 0.0));
                _cum.Add(v.Length);
            }

            return;
        }

        // 只有刚凑够两点时才需要回填基准方向；之后由下面的分支自己维护。
        if (_pts.Count == 2)
        {
            _lastDir = WgMath.Direction(_pts[1] - _pts[0]);
        }

        int last = _pts.Count - 1;
        WgVec2 delta = p - _pts[last];
        double dir = WgMath.Direction(delta);

        if (Math.Abs(WgMath.WrapPi(_lastDir, dir)) < MergeAngle)
        {
            // 还在上一条直线上：末点挪过去，重算"倒数第二个点 → 末点"这一段。
            _pts[last] = p;
            WgVec2 seg = p - _pts[last - 1];
            double prevCum = _cum[last - 1];
            _sig[last - 1] = new WgVec2(0.0, WgMath.Direction(seg));
            _sig[last] = new WgVec2(0.0, 0.0);
            _cum[last] = prevCum + seg.Length;
        }
        else
        {
            // 拐了：旧末点长出边、新点成为新末点。
            _lastDir = dir;
            double cumLast = _cum[last];
            _sig[last] = new WgVec2(0.0, dir);
            _pts.Add(p);
            _sig.Add(new WgVec2(0.0, 0.0));
            _cum.Add(cumLast + delta.Length);
        }
    }

    /// <summary>清空，回到"什么都没画"的状态。同源码 p.b()。</summary>
    public void Clear()
    {
        _pts.Clear();
        _sig.Clear();
        _cum.Clear();
        _sigCache = null;
    }

    /// <summary>
    /// 形状签名：(s, θ) 序列，s ∈ [0,1] 是归一化累计弧长，θ ∈ [-1,1] 是方向角 / π。
    /// 同源码 A/P.cs:194 的 <c>p.B()</c>，含惰性缓存（Add/Clear 后失效）。
    ///
    /// 返回的是**内部缓存数组**，调用方只读；需要留存请自己复制。
    /// 缓存把每次比较的两次归一化省掉了——一次匹配要对几十个模板求距离。
    /// </summary>
    public WgVec2[] Signature()
    {
        WgVec2[]? cached = _sigCache;
        if (cached != null)
        {
            return cached;
        }

        int n = _sig.Count;
        WgVec2[] result = new WgVec2[n];
        double total = n > 0 ? _cum[n - 1] : 0.0;
        if (total > 0.0)
        {
            for (int i = 0; i < n; i++)
            {
                result[i] = new WgVec2(_cum[i] / total, _sig[i].Y);
            }
        }
        else
        {
            // 零长度（所有点重合）：s 全给 0，别让 0/0 的 NaN 漏进 DP。
            for (int i = 0; i < n; i++)
            {
                result[i] = new WgVec2(0.0, _sig[i].Y);
            }
        }

        _sigCache = result;
        return result;
    }

    /// <summary>深拷贝（含缓存状态）。同源码 p.A() 的克隆语义。</summary>
    public WgStroke Clone()
    {
        WgStroke copy = new WgStroke();
        copy._pts.AddRange(_pts);
        copy._sig.AddRange(_sig);
        copy._cum.AddRange(_cum);
        copy._lastDir = _lastDir;
        return copy;
    }

    /// <summary>
    /// 把工程里既有的 0~1000 归一化点序列还原成一笔。
    ///
    /// 走的是和实时采点同一条路（<see cref="Add"/>），所以 18° 合并、
    /// 取整都会照常发生——模板与实时手势必须经过同一套前端，
    /// 否则比较的是两条不同加工流程的产物。
    /// </summary>
    public static WgStroke FromNormalized(IReadOnlyList<GestureMath.Pt> pts)
    {
        WgStroke s = new WgStroke();
        for (int i = 0; i < pts.Count; i++)
        {
            s.Add(pts[i].X, pts[i].Y);
        }

        return s;
    }

    /// <summary>
    /// 两笔的形状距离，不可比时返回 +∞。逐字移植 A/P.cs:301 的 <c>p.A(o)</c>。
    ///
    /// 三道上限的顺序和方向都照抄：
    /// 1. 两侧点数都必须 ≥ 2；
    /// 2. **参数侧**（<paramref name="other"/>）点数 &gt; 128 直接不可比——
    ///    上限是单侧的，不是两边都限；
    /// 3. 两侧点数比 &gt; 10 倍不可比。
    ///
    /// 参数侧就是源码里的 receiver 之外的另一个：实时匹配时
    /// <c>模板.A(手绘)</c>，被卡的 128 点是**手绘**那一侧（手绘点由
    /// 30px 采样出来，天然只有几十个点）。反着调用会得到不同的结果，
    /// <see cref="WgEngine.BestMatch"/> 用的是源码那个方向。
    /// </summary>
    public double DistanceTo(WgStroke other)
    {
        if (other == null)
        {
            return double.PositiveInfinity;
        }

        if (_pts.Count < 2 || other._pts.Count < 2 || other._pts.Count > MaxPoints)
        {
            return double.PositiveInfinity;
        }

        // 零长度的"一笔"没有形状可言（源码这里会走到除零、得到 NaN）。
        if (Length <= 0.0 || other.Length <= 0.0)
        {
            return double.PositiveInfinity;
        }

        if (_pts.Count > other._pts.Count * MaxPointRatio ||
            other._pts.Count > _pts.Count * MaxPointRatio)
        {
            return double.PositiveInfinity;
        }

        WgVec2[] a = Signature();
        WgVec2[] b = other.Signature();
        return WgStrokeDistance.Compute(a, a.Length, b, b.Length);
    }
}

/// <summary>
/// (s, θ) 空间里的耦合距离。逐字移植 A/G.cs:300 的
/// <c>g.A(Vec2[], Vec2[], int, int)</c>——原版是 unsafe 指针，这里换成下标访问，
/// 算术与遍历顺序不变。
///
/// 它和教科书 Fréchet / DTW 都不太一样，写下来免得后来人"优化"：
/// · 起点 (0,0) 代价 0，其余格子初值 <see cref="WgEngine.MaxDistance"/>（0.2）当 ∞；
/// · 只从 &lt; 0.2 的格子出发，向右/向下推进，一次最多推 4 步（每次给格子上松弛都算一步）；
/// · 推进时不是只走一格，而是把"新覆盖到的整行/整列"都松弛一遍——
///   Fréchet 那种"可以停在对面任意一点"的语义就在这个循环里；
/// · 每段代价是一条/一对区段上的加权积分（见 <see cref="SegmentCost"/>）。
/// </summary>
internal static class WgStrokeDistance
{
    public static double Compute(WgVec2[] p, int pn, WgVec2[] q, int qn)
    {
        const double Infinite = WgEngine.MaxDistance;
        double[] cell = new double[pn * qn];
        for (int i = 0; i < cell.Length; i++)
        {
            cell[i] = Infinite;
        }

        cell[0] = 0.0;
        int lastP = pn - 1;
        int lastQ = qn - 1;
        for (int i = 0; i < lastP; i++)
        {
            for (int j = 0; j < lastQ; j++)
            {
                if (cell[i * qn + j] >= Infinite)
                {
                    continue;
                }

                double pStart = p[i].X;
                double qStart = q[j].X;
                int pi = i;
                int qi = j;
                double baseCost = cell[i * qn + j];
                int steps = 0;
                while (steps < 4)
                {
                    if (p[pi + 1].X - pStart > q[qi + 1].X - qStart)
                    {
                        qi++;
                        if (qi == lastQ)
                        {
                            Relax(ref cell[lastP * qn + lastQ],
                                baseCost + SegmentCost(p, q, i, j, ref steps, lastP, lastQ));
                            break;
                        }

                        for (int k = i + 1; k <= pi; k++)
                        {
                            Relax(ref cell[k * qn + qi],
                                baseCost + SegmentCost(p, q, i, j, ref steps, k, qi));
                        }
                    }
                    else
                    {
                        pi++;
                        if (pi == lastP)
                        {
                            Relax(ref cell[lastP * qn + lastQ],
                                baseCost + SegmentCost(p, q, i, j, ref steps, lastP, lastQ));
                            break;
                        }

                        for (int k = j + 1; k <= qi; k++)
                        {
                            Relax(ref cell[pi * qn + k],
                                baseCost + SegmentCost(p, q, i, j, ref steps, pi, k));
                        }
                    }
                }
            }
        }

        return cell[pn * qn - 1];
    }

    private static void Relax(ref double cell, double cost)
    {
        cell = Math.Min(cell, cost);
    }

    /// <summary>
    /// 一对区段 [i..endP] × [j..endQ] 的代价：把两条曲线的参数都压到 [0,1]，
    /// 在并起来的断点上逐段累加 (Δθ)² × 段长，最后乘上两段弧长之和。
    ///
    /// 乘 (pSpan + qSpan) 是让它保持"弧长量纲"：s 是无量纲的，
    /// 而外层这些代价是直接相加、再和 0.05 比的，不乘回去长度信息就丢了。
    /// </summary>
    private static double SegmentCost(
        WgVec2[] p, WgVec2[] q, int i0, int j0, ref int steps, int endP, int endQ)
    {
        double pStart = p[i0].X;
        double qStart = q[j0].X;
        double pSpan = p[endP].X - pStart;
        double qSpan = q[endQ].X - qStart;
        steps++;

        // 零长区段（相邻两点 s 相同）本该是 0 权重；源码会在这里除以 0 得到 NaN，
        // 而 NaN 会顺着 Math.Min 污染整张表。这里直接返回 0，等价于"这段没形状"。
        if (pSpan <= 0.0 || qSpan <= 0.0)
        {
            return 0.0;
        }

        int i = i0;
        int j = j0;
        double pNext = (p[i + 1].X - pStart) / pSpan;
        double qNext = (q[j + 1].X - qStart) / qSpan;
        double pT = pNext;
        double prev = 1E-09;
        double acc = 0.0;
        while (true)
        {
            double dTheta = WgMath.WrapPi(p[i].Y, q[j].Y);
            double t = Math.Min(pT, qNext);
            bool done = t >= 0.999999;
            if (done)
            {
                t = 1.0;
            }

            acc += (t - prev) * (dTheta * dTheta);
            if (done)
            {
                break;
            }

            prev = t;
            if (pT < qNext)
            {
                i++;
                pNext = (p[i + 1].X - pStart) / pSpan;
                pT = pNext;
            }
            else
            {
                j++;
                qNext = (q[j + 1].X - qStart) / qSpan;
            }
        }

        return acc * (pSpan + qSpan);
    }
}

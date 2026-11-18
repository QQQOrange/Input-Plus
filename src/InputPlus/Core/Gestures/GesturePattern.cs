using System;
using System.Collections.Generic;
using System.Text;

namespace InputPlus.Core.Gestures;

/// <summary>
/// 方向识别：把鼠标轨迹压成 <c>↑→↓←↖↗↙↘</c> 这样的方向串。
///
/// 这套算法照着 FlowMouse 的 <c>js/gesture-recognizer.js</c> 实现，原因很实在：
/// 手势是"画方向"，不是"描形状"。按形状做最小二乘匹配，用户画得快一点、
/// 歪一点、长短差一倍就容易判不中；按方向串匹配，只要拐的弯对就一定中，
/// 而且方向串还能直接显示在界面上（↑→），用户一眼就知道自己配的是什么。
///
/// 三段阈值：
///  · 起手距离：走出这么多像素才算"开始画"，避免右键单击被当成手势；
///  · 自适应转折：换方向要越过 <c>min(最大转折, 起手距离 + 上一段长度 × 转折系数)</c>，
///    上一段越长越不容易被一个抖动带偏（长笔画上的小抖动不该多出一段）；
///  · 最大转折：防止长笔画怎么抖都切不了方向。
/// </summary>
public static class GesturePattern
{
    /// <summary>通配：任何方向都算命中（FlowMouse 里的 <c>*</c>）。</summary>
    public const string Wildcard = "*";

    public const double DefaultActivation = 20;
    public const double DefaultTurnTolerance = 0.10;
    public const double DefaultMaxTurn = 120;

    /// <summary>
    /// 把位移归到八个方向：上下左右 + 四个斜角。
    /// 轴占优很多（约 29° 以内）才算正方向，中间一段算斜线；
    /// 这样“向下滑再向左滑”的直角手势不会被读成一条斜线。
    /// </summary>
    public static string Direction(double dx, double dy)
    {
        double ax = Math.Abs(dx), ay = Math.Abs(dy);
        if (ax > ay * 1.8) return dx > 0 ? "→" : "←";
        if (ay > ax * 1.8) return dy > 0 ? "↓" : "↑";
        return (dx > 0, dy > 0) switch
        {
            (true, true) => "↘",
            (true, false) => "↗",
            (false, true) => "↙",
            _ => "↖",
        };
    }

    public static bool IsDiagonal(string? direction) =>
        direction is "↖" or "↗" or "↙" or "↘";

    /// <summary>
    /// 斜线对应的两个正交分量：↗ → (↑, →)。
    /// 不是斜线时返回空串（调用方据此判断"这段没有分量可比"）。
    /// </summary>
    public static (string V, string H) Components(string? diagonal) => diagonal switch
    {
        "↗" => ("↑", "→"),
        "↘" => ("↓", "→"),
        "↙" => ("↓", "←"),
        "↖" => ("↑", "←"),
        _ => ("", ""),
    };

    /// <summary>
    /// 两个方向是不是"同一笔"：
    /// 完全相同，或者一个是斜线、另一个正好是它的分量（↗ 与 ↑ / →）。
    ///
    /// 为什么需要它：斜着一划过去，角度在 45° 上下抖动，<see cref="Direction"/>
    /// 就会在 ↗ 与 ↑ 之间来回跳，一段斜线被记成 ↗↑↗↑↗↑↗ 七个笔画。
    /// 有了这个判定，两种方向就按"继续同一段"处理。
    /// </summary>
    public static bool SameStroke(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.Ordinal)) return true;
        var (v, h) = Components(a);
        if (v.Length > 0) return b == v || b == h;
        (v, h) = Components(b);
        return v.Length > 0 && (a == v || a == h);
    }

    /// <summary>
    /// 把方向串里"同一笔被拆成好几段"的情况合并掉，例如：
    /// <c>↗↑↗↑↗</c> → <c>↗</c>；<c>→↘→</c> → <c>↘</c>；<c>↑→</c> 保持不变（真的是两笔）。
    ///
    /// 老存档里已有的坏方向串也走这里，所以配置不需要重画。
    ///
    /// 合并时**保留先出现的那个方向**（不做"斜线优先"的升级）：真·直角手势
    /// 在拐角处也会短暂报一次斜线（↑↗→），升级成斜线会把 ↑→ 误伤成 ↗。
    /// 保留第一个方向则 ↑↗→ 仍然是 ↑→，而 ↗↑↗ 仍然是 ↗ —— 两边都对。
    /// </summary>
    public static string Clean(string? pattern)
    {
        var arrows = Arrows(pattern);
        if (arrows.Length == 0) return "";
        var kept = new List<string>(arrows.Length);
        foreach (string arrow in arrows)
        {
            if (kept.Count == 0)
            {
                kept.Add(arrow);
                continue;
            }

            string last = kept[^1];
            if (!SameStroke(last, arrow)) 
            {
                kept.Add(arrow);
                continue;
            }

        }
        return string.Concat(kept);
    }

    /// <summary>把方向串拆成箭头数组，界面上排版用。</summary>
    public static string[] Arrows(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return Array.Empty<string>();
        var list = new List<string>(pattern.Length);
        foreach (char c in pattern)
        {
            if (c is '↑' or '↓' or '←' or '→' or '↖' or '↗' or '↙' or '↘') list.Add(c.ToString());
            else if (c == '*') list.Add("*");
        }
        return list.ToArray();
    }

    /// <summary>方向串的可读描述，例如“上 → 右”。空串返回空。</summary>
    public static string Describe(string? pattern)
    {
        var arrows = Arrows(pattern);
        if (arrows.Length == 0) return "";
        var sb = new StringBuilder();
        foreach (var a in arrows)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(a switch
            {
                "↑" => "↑",
                "↓" => "↓",
                "←" => "←",
                "→" => "→",
                "↖" => "↖",
                "↗" => "↗",
                "↙" => "↙",
                "↘" => "↘",
                _ => "*",
            });
        }
        return sb.ToString();
    }

    /// <summary>从一整条轨迹一次性算方向串（导入、老数据迁移用）。</summary>
    public static string FromPoints(
        IReadOnlyList<GestureMath.Pt> points,
        double activation = DefaultActivation,
        double turnTolerance = DefaultTurnTolerance,
        double maxTurn = DefaultMaxTurn)
    {
        if (points == null || points.Count == 0) return "";
        var tracker = new Tracker(activation, turnTolerance, maxTurn);
        tracker.Start(points[0].X, points[0].Y);
        for (int i = 1; i < points.Count; i++)
            tracker.Move(points[i].X, points[i].Y);
        return tracker.Pattern;
    }

    /// <summary>
    /// 从"已归一化到 1000×1000"的存档路径推算方向串。
    /// 这类路径的最大边固定是 1000，所以阈值按比例给：起手 50、最大转折 300，
    /// 对应真实屏幕上一笔 400px 左右的手势（20px / 120px）。
    /// </summary>
    public static string FromNormalized(IReadOnlyList<GestureMath.Pt> path)
        => FromPoints(path, activation: 50, turnTolerance: DefaultTurnTolerance, maxTurn: 300);

    /// <summary>
    /// 把方向串画成一条折线（每段等长），给「只有方向串、没存过轨迹」的存量手势
    /// 合成几何模板用 —— 识别只剩 <see cref="GestureEngine"/> 一套之后，
    /// 这类手势也得有个形状才能进引擎比。
    ///
    /// 为什么这样合出来的模板能和手绘比：引擎先把一笔按 18° 合并成折线，
    /// 再压成 (累计弧长 s, 方向角 θ) 曲线比 ∫(Δθ)²·ds。一条 "↑→" 折线合并后是
    /// 3 个点，用户手画同样拐两个弯的一笔合并后也是 3~5 个点 —— 两侧落在同一个
    /// 量纲上，点数比也在引擎 10 倍闸门以内。段长取一样长，因为方向串本来只记
    /// "往哪拐"，不记"这一拐有多长"；返回值与存档轨迹同坐标（归一化到 1000）。
    ///
    /// 通配 <c>*</c> 和空串返回空列表：它们没有形状，由调用方单独兜底。
    /// </summary>
    public static List<GestureMath.Pt> Sketch(string? pattern)
    {
        var arrows = Arrows(Clean(pattern));
        var pts = new List<GestureMath.Pt>(arrows.Length + 1);
        if (arrows.Length == 0) return pts;

        double x = 0, y = 0;
        pts.Add(new GestureMath.Pt(x, y));
        foreach (string arrow in arrows)
        {
            double angle = ArrowAngle(arrow);
            if (double.IsNaN(angle)) return new List<GestureMath.Pt>();   // 串里混了 "*" 之类
            x += Math.Cos(angle);
            y -= Math.Sin(angle);      // ArrowAngle 用数学习惯（↑ = +π/2），屏幕坐标 y 向下
            pts.Add(new GestureMath.Pt(x, y));
        }
        return GestureMath.Normalize(pts);
    }

    /// <summary>方向字符换成角度（屏幕坐标：x 向右、y 向下，所以 ↑ 是 +π/2）。</summary>
    private static double ArrowAngle(string arrow) => arrow switch
    {
        "→" => 0,
        "↗" => Math.PI / 4,
        "↑" => Math.PI / 2,
        "↖" => Math.PI * 3 / 4,
        "←" => Math.PI,
        "↙" => -Math.PI * 3 / 4,
        "↓" => -Math.PI / 2,
        "↘" => -Math.PI / 4,
        _ => double.NaN
    };

    /// <summary>
    /// 实时追踪器：每来一个鼠标位置喂一次，内部维护当前方向串。
    /// 只在钩子采样线程里用；不要跨线程共享。
    /// </summary>
    public sealed class Tracker
    {
        private readonly double _activation;
        private readonly double _turnTolerance;
        private readonly double _maxTurn;
        private readonly List<string> _pattern = new();
        private readonly List<(double X, double Y)> _history = new();

        private bool _active;
        private double _startX, _startY;
        private double _anchorX, _anchorY;
        private double _lastX, _lastY;
        private double _segmentLength;

        public Tracker(
            double activation = DefaultActivation,
            double turnTolerance = DefaultTurnTolerance,
            double maxTurn = DefaultMaxTurn)
        {
            _activation = Math.Clamp(activation, 4, 400);
            _turnTolerance = Math.Clamp(turnTolerance, 0, 1);
            _maxTurn = Math.Clamp(maxTurn, _activation, 2000);
        }

        public bool Active => _active;

        /// <summary>
        /// 当前方向串，例如 <c>↑→</c>；没起手时是空串。
        ///
        /// 出口一律过 <see cref="Clean"/>：内部每变一次方向就记一段，斜线在 45°
        /// 附近抖一下就会记成 ↗↑↗↑↗ —— 显示、保存、匹配都必须用合并后的结果，
        /// 否则"录了一条斜线，界面却显示七个箭头"。
        /// </summary>
        public string Pattern => Clean(string.Concat(_pattern));

        /// <summary>最后一段的方向；没起手时是空串。</summary>
        public string LastDirection => _pattern.Count > 0 ? _pattern[^1] : "";

        public double TotalDistance { get; private set; }

        public void Start(double x, double y)
        {
            _active = false;
            _startX = _anchorX = _lastX = x;
            _startY = _anchorY = _lastY = y;
            _segmentLength = 0;
            TotalDistance = 0;
            _pattern.Clear();
            _history.Clear();
            _history.Add((x, y));
        }

        /// <summary>
        /// 喂一个采样点。返回 true 表示这一下让方向串变长了（界面可以据此刷新）。
        /// </summary>
        public bool Move(double x, double y)
        {
            _lastX = x;
            _lastY = y;
            if (_history.Count < 600) _history.Add((x, y));

            double dxTotal = x - _startX, dyTotal = y - _startY;
            TotalDistance = Math.Sqrt(dxTotal * dxTotal + dyTotal * dyTotal);

            bool changed = false;

            if (!_active)
            {
                if (TotalDistance <= _activation) return false;

                _active = true;

                /* 起手前那一段也要算进方向串，否则"先斜着甩出去再拐弯"会丢第一段。
                 * 重放阈值比起手距离略小（FlowMouse 用 0.7 / 0.8），避免第一段被吞。 */
                double replay = _activation * (_activation < 15 ? 0.8 : 0.7);
                _pattern.Clear();
                _anchorX = _startX;
                _anchorY = _startY;
                _segmentLength = 0;

                foreach (var (hx, hy) in _history)
                {
                    double dx = hx - _anchorX, dy = hy - _anchorY;
                    double dist = Math.Sqrt(dx * dx + dy * dy);
                    if (dist <= replay) continue;

                    string dir = Direction(dx, dy);
                    if (_pattern.Count == 0 || dir != _pattern[^1])
                    {
                        Push(dir);
                        _anchorX = hx;
                        _anchorY = hy;
                        _segmentLength = dist;
                    }
                    else
                    {
                        _segmentLength += dist;
                        _anchorX = hx;
                        _anchorY = hy;
                    }
                }

                if (_pattern.Count == 0)
                {
                    Push(Direction(dxTotal, dyTotal));
                    _anchorX = x;
                    _anchorY = y;
                    _segmentLength = TotalDistance;
                }

                return true;
            }

            double ddx = x - _anchorX, ddy = y - _anchorY;
            double distance = Math.Sqrt(ddx * ddx + ddy * ddy);
            if (distance <= _activation) return false;

            string direction = Direction(ddx, ddy);
            if (_pattern.Count > 0 && direction == _pattern[^1])
            {
                _segmentLength += distance;
                _anchorX = x;
                _anchorY = y;
                return false;
            }

            double threshold = Math.Min(_maxTurn, _activation + _segmentLength * _turnTolerance);
            if (distance <= threshold) return false;

            Push(direction);
            _segmentLength = distance;
            _anchorX = x;
            _anchorY = y;
            changed = true;
            return changed;
        }

        /// <summary>
        /// 追加一段方向。斜线如果只是直角拐弯中间的过渡（↓ ↙ ←），
        /// 就把它删掉，避免“先下再左”被误读成三段、也避免斜线抢走直角手势。
        /// 真正的一整条斜线仍然保留。
        /// </summary>
        private void Push(string direction)
        {
            _pattern.Add(direction);
            if (_pattern.Count < 3) return;

            string a = _pattern[^3], b = _pattern[^2], c = _pattern[^1];
            if (!IsDiagonal(b)) return;

            (string vertical, string horizontal) = b switch
            {
                "↘" => ("↓", "→"),
                "↗" => ("↑", "→"),
                "↙" => ("↓", "←"),
                "↖" => ("↑", "←"),
                _ => ("", ""),
            };
            if ((a == vertical && c == horizontal) || (a == horizontal && c == vertical))
                _pattern.RemoveAt(_pattern.Count - 2);
        }
    }
}

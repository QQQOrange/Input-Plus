using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace InputPlus.UI.Windows;

/// <summary>
/// 手势轨迹几何 —— WGestures 2 的 `A/U.cs`（轨迹数据 + 三支笔的路径）与
/// `A/G.cs` 里的 Chaikin 切角，按原语义搬过来。
///
/// 三处必须照抄的地方，少一处手感就变：
/// <list type="number">
/// <item>**采样距离 20 设备像素**（`A/U.cs` 的 `C_f = 20f`）：
///     相邻两点距离不到 20px 直接丢弃，所以一条轨迹只有几十个点，
///     不会跟着 8ms 轮询一路抖成锯齿。</item>
/// <item>**≤128 点做一轮切角平滑**（`A/U.cs` 的 `F()`：`count <= 128 ? 1 : 0`，
///     再交给 `G.A(points, 0.5, n)`）：t=0.5 时每个拐角切掉 25%
///     （`num = 0.05 + t * 0.4`），直角变成两段 45° 折线，看起来是弧。</item>
/// <item>**超过 128 点不平滑**：长轨迹平滑的收益远小于开销，WG2 直接跳过。</item>
/// </list>
///
/// 坐标一律是**物理像素**：WG2 的轨迹窗口本来就画在设备坐标里，
/// 换算成 DIP 只发生在画的那一步（见 <see cref="GestureOverlayWindow"/>）。
/// </summary>
internal sealed class GestureTrail
{
    /// <summary>相邻采样点的最小距离（设备像素，<c>A/U.cs</c> 的 <c>C_f = 20f</c>）。</summary>
    internal const double MinDistance = 20;

    /// <summary>平滑强度 t（<c>A/U.cs</c> 调 <c>G.A(points, 0.5, n)</c>）。</summary>
    internal const double SmoothT = 0.5;

    /// <summary>点数不超过它才平滑（<c>A/U.cs</c> 的 <c>F()</c>）。</summary>
    internal const int SmoothPointLimit = 128;

    private readonly List<Point> _points = new(256);

    /// <summary>
    /// 「笔尖」：鼠标最后一次移动的位置（不进采样表）。
    ///
    /// 采样点之间隔 20px，线头就停在最后一个采样点上 —— 手感上"线跟不上手"。
    /// 每次移动只把笔尖挪到光标处（<see cref="Tail"/>），<see cref="Build"/> 时
    /// 接在末尾，画面就一直是通的；采样表本身不变，识别仍按 20px 均匀点算。
    /// </summary>
    private Point? _tail;

    /// <summary>已收下的采样点（设备像素，未平滑）。</summary>
    internal IReadOnlyList<Point> Points => _points;

    internal int Count => _points.Count;

    /// <summary>清空整条轨迹（WG2 的 <c>U.E()</c>：手势结束一瞬间就没了）。</summary>
    internal void Clear()
    {
        _points.Clear();
        _tail = null;
    }

    /// <summary>把线头挪到光标处（不满足 20px 采样距离时也画得出来）。</summary>
    internal void Tail(double x, double y) => _tail = new Point(x, y);

    /// <summary>
    /// 收一个点。离上一个点不到 <see cref="MinDistance"/> 就丢弃并返回 false
    /// —— WG2 的 <c>U.A(Vec2)</c> 用 <c>DistanceSquared &lt; C_f * C_f</c> 判，这里一模一样。
    /// </summary>
    internal bool Add(double x, double y)
    {
        if (_points.Count > 0)
        {
            Point last = _points[^1];
            double dx = x - last.X;
            double dy = y - last.Y;
            if (dx * dx + dy * dy < MinDistance * MinDistance) return false;
        }
        _points.Add(new Point(x, y));
        _tail = null;
        return true;
    }

    /// <summary>覆盖最后一个点（自检注入用；正常采样只走 <see cref="Add"/>）。</summary>
    internal void ReplaceLast(double x, double y)
    {
        _tail = null;
        if (_points.Count == 0) { _points.Add(new Point(x, y)); return; }
        _points[^1] = new Point(x, y);
    }

    /// <summary>
    /// 交给画笔之前的点列。
    ///
    /// 先把"笔尖"接在采样点后面（这样线头始终跟着光标，不会卡在 20px 一格上），
    /// 再按 <paramref name="smooth"/> 决定要不要切角 —— 它就是
    /// 「手势外观 → 笔画平滑」那个开关：
    ///   开：≤128 点的笔画做两轮 Chaikin（拐角变圆弧，看得出变化）；
    ///   关：原样画采样点，锯齿保持原样。
    /// 识别用的点列一直是未平滑的原始采样点，开关不影响认手势。
    /// </summary>
    internal List<Point> Build(bool smooth = true)
    {
        var points = new List<Point>(_points.Count + 1);
        points.AddRange(_points);
        if (_tail is Point tail)
        {
            if (points.Count == 0)
            {
                points.Add(tail);
            }
            else
            {
                Point last = points[^1];
                double dx = tail.X - last.X;
                double dy = tail.Y - last.Y;
                // 笔尖和最后一个采样点几乎重合时不必重复（避免零长度线段）
                if (dx * dx + dy * dy > 4) points.Add(tail);
            }
        }
        if (points.Count == 0) return points;
        if (!smooth) return points;
        if (points.Count > SmoothPointLimit) return points;
        return Chaikin(points, 1);
    }

    /// <summary>
    /// Chaikin 切角（<c>A/G.cs:251-281</c>）：每段按 <c>t = 0.05 + smoothT * 0.4</c>
    /// 插入两个点，首尾点原样保留，迭代 <paramref name="iterations"/> 轮。
    /// t=0.5 → 0.25：拐角被切掉 25%，直角变两段 45° 折线。
    /// </summary>
    internal static List<Point> Chaikin(IReadOnlyList<Point> source, int iterations)
    {
        if (source.Count < 3 || iterations <= 0) return new List<Point>(source);

        double t = 0.05 + SmoothT * 0.4;
        List<Point> current = new(source);
        for (int pass = 0; pass < iterations && current.Count > 2; pass++)
        {
            List<Point> next = new(current.Count * 2) { current[0] };
            for (int i = 0; i + 1 < current.Count; i++)
            {
                Point a = current[i];
                Point b = current[i + 1];
                next.Add(new Point((1 - t) * a.X + t * b.X, (1 - t) * a.Y + t * b.Y));
                next.Add(new Point(t * a.X + (1 - t) * b.X, t * a.Y + (1 - t) * b.Y));
            }
            next.Add(current[^1]);
            current = next;
        }
        return current;
    }

    /// <summary>
    /// 点列 → 一条不闭合的折线几何（等价 <c>GraphicsPath.AddLine</c> 串联）。
    /// <paramref name="map"/> 负责物理像素 → 画布 DIP 的换算。
    /// 点数不足 2 时返回 null（GDI+ 同样画不出东西）。
    /// </summary>
    internal static Geometry? ToGeometry(IReadOnlyList<Point> points, Func<Point, Point> map)
    {
        if (points.Count < 2) return null;

        var geometry = new StreamGeometry();
        using (StreamGeometryContext context = geometry.Open())
        {
            context.BeginFigure(map(points[0]), isFilled: false, isClosed: false);
            var rest = new List<Point>(points.Count - 1);
            for (int i = 1; i < points.Count; i++) rest.Add(map(points[i]));
            context.PolyLineTo(rest, isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        return geometry;
    }
}

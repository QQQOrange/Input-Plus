using System;
using System.Globalization;

namespace InputPlus.Core.Gestures.WG;

/// <summary>
/// 二维点/向量。对应反编译源码里的 YingDev.CGMath.Vec2，只保留手势识别用得到的那部分。
///
/// 为什么不用 WPF 的 Point 或 <see cref="GestureMath.Pt"/>：识别核心是纯算术
/// （方向角、弧长、带权 DP），与界面、与工程里已有的 0~1000 归一化点都没有关系。
/// 做成独立的小结构体，这四个文件就可以原样搬进任何控制台工程做数值验证。
/// </summary>
public readonly struct WgVec2 : IEquatable<WgVec2>
{
    public readonly double X;

    public readonly double Y;

    public WgVec2(double x, double y)
    {
        X = x;
        Y = y;
    }

    /// <summary>
    /// 取整到整像素（源码 p.A(Vec2) 的第一步）。
    ///
    /// **必须是 Math.Round 的默认银行家舍入**，不能换成"四舍五入"：
    /// 采点前先取整决定了哪些点会重合、方向角算出来是 0 还是 ±π，
    /// 进而决定 18° 合并把哪些顶点吃掉。换舍入方式 = 换签名点数 = 换距离。
    /// </summary>
    public WgVec2 Rounded() => new WgVec2(Math.Round(X), Math.Round(Y));

    public double Length => Math.Sqrt(X * X + Y * Y);

    public static WgVec2 operator +(WgVec2 a, WgVec2 b) => new WgVec2(a.X + b.X, a.Y + b.Y);

    public static WgVec2 operator -(WgVec2 a, WgVec2 b) => new WgVec2(a.X - b.X, a.Y - b.Y);

    public static WgVec2 operator *(WgVec2 a, double k) => new WgVec2(a.X * k, a.Y * k);

    public static WgVec2 operator /(WgVec2 a, double k) => new WgVec2(a.X / k, a.Y / k);

    public bool Equals(WgVec2 other) => X.Equals(other.X) && Y.Equals(other.Y);

    public override bool Equals(object? obj) => obj is WgVec2 other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y);

    public override string ToString() =>
        string.Format(CultureInfo.InvariantCulture, "({0:0.##}, {1:0.##})", X, Y);
}

/// <summary>
/// 识别核心里反复用到的两个角度工具。单独抽出来是因为它们同时被
/// 采点（18° 合并判定）和距离 DP（角度差加权）使用，语义必须完全一致。
/// </summary>
internal static class WgMath
{
    /// <summary>向量方向角，单位是 π（返回值 ∈ [-1,1]）。同源码 p.a(Vec2)。</summary>
    public static double Direction(WgVec2 v) => Math.Atan2(v.Y, v.X) / Math.PI;

    /// <summary>
    /// 两个方向角之差，环绕到 [-1,1]（π 单位）。
    ///
    /// 逐字照抄 A/G.cs:378 的 g.A_m：只在 |差值| &gt; 1 时补一次 ∓2，
    /// 所以 ±1 这条边界并不对称（-1 仍是 -1、+1 仍是 +1）。
    /// 这是原实现的行为，别"顺手修正"——它同时是 18° 合并的判定函数。
    /// </summary>
    public static double WrapPi(double a, double b)
    {
        double d = a - b;
        if (d < -1.0) return d + 2.0;
        if (d > 1.0) return d - 2.0;
        return d;
    }
}

namespace JinJi.Domain.Workplace;

/// <summary>
/// 簡易フロアマップの形の検査（外接矩形・点の多角形内判定・自己交差）。PostGIS は入れず、取込の照合（承認の前）でアプリが検査する（仕様 §2.3・§2.5）。
/// </summary>
/// <remarks>座標は整数のミリメートル（最大 500,000）なので、外積は long で誤差なく計算できる。</remarks>
public static class FloorGeometry
{
    /// <summary>多角形の形の誤り（頂点数・座標の範囲・自己交差）。問題が無ければ null。</summary>
    public static string? Validate(IReadOnlyList<LayoutPoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        if (polygon.Count is < WorkplaceCodes.MinPolygonPoints or > WorkplaceCodes.MaxPolygonPoints)
        {
            return $"多角形の頂点は {WorkplaceCodes.MinPolygonPoints}〜{WorkplaceCodes.MaxPolygonPoints} 個にしてください。";
        }

        if (polygon.Any(p => p.X is < 0 or > WorkplaceCodes.MaxCoordinateMm || p.Y is < 0 or > WorkplaceCodes.MaxCoordinateMm))
        {
            return $"座標は 0〜{WorkplaceCodes.MaxCoordinateMm} mm（外接矩形の 1 辺 500 m）の整数にしてください。";
        }

        if (Area2(polygon) == 0)
        {
            return "多角形の面積が 0 です（頂点が一直線に並んでいます）。";
        }

        return SelfIntersects(polygon) ? "多角形の辺が交差しています（自己交差）。" : null;
    }

    /// <summary>点が多角形の内側か（辺の上も内側とみなす）。</summary>
    public static bool Contains(IReadOnlyList<LayoutPoint> polygon, LayoutPoint point)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        var inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var a = polygon[i];
            var b = polygon[j];
            if (OnSegment(a, b, point))
            {
                return true;
            }

            if ((a.Y > point.Y) != (b.Y > point.Y))
            {
                // 点を通る水平線と辺の交点の x（有理数のまま比較する）
                var lhs = (long)(point.X - a.X) * (b.Y - a.Y);
                var rhs = (long)(b.X - a.X) * (point.Y - a.Y);
                if (b.Y - a.Y > 0 ? lhs < rhs : lhs > rhs)
                {
                    inside = !inside;
                }
            }
        }

        return inside;
    }

    /// <summary>多角形のすべての頂点が外形の内側か（外形と辺が交わる凹な形は見落とすことがある。簡易の検査）。</summary>
    public static bool ContainsAll(IReadOnlyList<LayoutPoint> outline, IReadOnlyList<LayoutPoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        return polygon.All(p => Contains(outline, p));
    }

    /// <summary>外接矩形（最小 x・最小 y・最大 x・最大 y）。</summary>
    public static (int MinX, int MinY, int MaxX, int MaxY) Bounds(IReadOnlyList<LayoutPoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        return (polygon.Min(p => p.X), polygon.Min(p => p.Y), polygon.Max(p => p.X), polygon.Max(p => p.Y));
    }

    /// <summary>隣り合わない辺同士が交わるか（頂点を共有する隣の辺は除く）。</summary>
    public static bool SelfIntersects(IReadOnlyList<LayoutPoint> polygon)
    {
        ArgumentNullException.ThrowIfNull(polygon);
        var n = polygon.Count;
        for (var i = 0; i < n; i++)
        {
            var a1 = polygon[i];
            var a2 = polygon[(i + 1) % n];
            for (var j = i + 1; j < n; j++)
            {
                if (j == i || (j + 1) % n == i || (i + 1) % n == j)
                {
                    continue;
                }

                if (SegmentsIntersect(a1, a2, polygon[j], polygon[(j + 1) % n]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static long Area2(IReadOnlyList<LayoutPoint> polygon)
    {
        long sum = 0;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            sum += ((long)polygon[j].X * polygon[i].Y) - ((long)polygon[i].X * polygon[j].Y);
        }

        return sum;
    }

    private static long Cross(LayoutPoint o, LayoutPoint a, LayoutPoint b) =>
        ((long)(a.X - o.X) * (b.Y - o.Y)) - ((long)(a.Y - o.Y) * (b.X - o.X));

    private static bool OnSegment(LayoutPoint a, LayoutPoint b, LayoutPoint p) =>
        Cross(a, b, p) == 0 && Math.Min(a.X, b.X) <= p.X && p.X <= Math.Max(a.X, b.X) && Math.Min(a.Y, b.Y) <= p.Y && p.Y <= Math.Max(a.Y, b.Y);

    private static bool SegmentsIntersect(LayoutPoint p1, LayoutPoint p2, LayoutPoint q1, LayoutPoint q2)
    {
        var d1 = Math.Sign(Cross(q1, q2, p1));
        var d2 = Math.Sign(Cross(q1, q2, p2));
        var d3 = Math.Sign(Cross(p1, p2, q1));
        var d4 = Math.Sign(Cross(p1, p2, q2));
        if (d1 != d2 && d3 != d4 && d1 != 0 && d2 != 0 && d3 != 0 && d4 != 0)
        {
            return true;
        }

        return (d1 == 0 && OnSegment(q1, q2, p1)) || (d2 == 0 && OnSegment(q1, q2, p2))
            || (d3 == 0 && OnSegment(p1, p2, q1)) || (d4 == 0 && OnSegment(p1, p2, q2));
    }
}

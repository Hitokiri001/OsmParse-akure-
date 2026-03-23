using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Clips 2D polygons and polylines to a rectangular boundary.
/// Fixed: ClipPolygon now tests both winding orders and uses axis-aligned
/// edge tests instead of directed edge cross products, which is more robust
/// for mixed winding polygons from different data sources (OSM vs WorldCover).
/// </summary>
public static class Clipper
{
    private const float Epsilon = 0.001f;

    // --- Polygon Clipping (Sutherland-Hodgman, axis-aligned) ---

    /// <summary>
    /// Clips a polygon to the given rectangular bounds.
    /// Handles both CW and CCW wound polygons correctly.
    /// Returns null if the polygon is entirely outside.
    /// </summary>
    public static List<Vector2> ClipPolygon(List<Vector2> polygon, Vector2 min, Vector2 max)
    {
        if (polygon == null || polygon.Count < 3)
            return null;

        List<Vector2> output = new List<Vector2>(polygon);

        // Clip against each of the four axis-aligned edges using
        // direct inside/outside tests rather than cross products
        // This avoids winding order sensitivity entirely

        // Left edge: x >= min.x
        output = ClipLeft(output, min.x);
        if (output == null || output.Count < 3) return null;

        // Right edge: x <= max.x
        output = ClipRight(output, max.x);
        if (output == null || output.Count < 3) return null;

        // Bottom edge: y >= min.y
        output = ClipBottom(output, min.y);
        if (output == null || output.Count < 3) return null;

        // Top edge: y <= max.y
        output = ClipTop(output, max.y);
        if (output == null || output.Count < 3) return null;

        return output;
    }

    /// <summary>
    /// Convenience overload accepting ChunkBounds.
    /// </summary>
    public static List<Vector2> ClipPolygon(List<Vector2> polygon, ChunkBounds chunk)
    {
        return ClipPolygon(polygon, chunk.WorldMin, chunk.WorldMax);
    }

    // --- Polyline Clipping (Cohen-Sutherland) ---

    /// <summary>
    /// Clips a polyline to the given rectangular bounds.
    /// Returns multiple segments if the line enters and exits more than once.
    /// </summary>
    public static List<List<Vector2>> ClipPolyline(List<Vector2> line, Vector2 min, Vector2 max)
    {
        var result = new List<List<Vector2>>();
        if (line == null || line.Count < 2) return result;

        List<Vector2> current = new List<Vector2>();

        for (int i = 0; i < line.Count - 1; i++)
        {
            Vector2 a = line[i];
            Vector2 b = line[i + 1];

            bool accepted = ClipSegment(ref a, ref b, min, max);

            if (accepted)
            {
                if (current.Count == 0)
                {
                    current.Add(a);
                }
                else if (Vector2.Distance(current[current.Count - 1], a) > Epsilon)
                {
                    if (current.Count >= 2) result.Add(current);
                    current = new List<Vector2> { a };
                }
                current.Add(b);
            }
            else
            {
                if (current.Count >= 2) result.Add(current);
                current = new List<Vector2>();
            }
        }

        if (current.Count >= 2)
            result.Add(current);

        return result;
    }

    /// <summary>
    /// Convenience overload accepting ChunkBounds.
    /// </summary>
    public static List<List<Vector2>> ClipPolyline(List<Vector2> line, ChunkBounds chunk)
    {
        return ClipPolyline(line, chunk.WorldMin, chunk.WorldMax);
    }

    // --- Private: axis-aligned Sutherland-Hodgman edge clippers ---
    // Each one clips the polygon against one infinite axis-aligned half-plane.
    // These are winding-order independent because they test coordinate values
    // directly rather than using cross products.

    private static List<Vector2> ClipLeft(List<Vector2> poly, float minX)
    {
        var output = new List<Vector2>();
        int n      = poly.Count;

        for (int i = 0; i < n; i++)
        {
            Vector2 curr = poly[i];
            Vector2 prev = poly[(i + n - 1) % n];

            bool currInside = curr.x >= minX - Epsilon;
            bool prevInside = prev.x >= minX - Epsilon;

            if (currInside)
            {
                if (!prevInside)
                    output.Add(IntersectX(prev, curr, minX));
                output.Add(curr);
            }
            else if (prevInside)
            {
                output.Add(IntersectX(prev, curr, minX));
            }
        }

        return output.Count >= 3 ? output : null;
    }

    private static List<Vector2> ClipRight(List<Vector2> poly, float maxX)
    {
        var output = new List<Vector2>();
        int n      = poly.Count;

        for (int i = 0; i < n; i++)
        {
            Vector2 curr = poly[i];
            Vector2 prev = poly[(i + n - 1) % n];

            bool currInside = curr.x <= maxX + Epsilon;
            bool prevInside = prev.x <= maxX + Epsilon;

            if (currInside)
            {
                if (!prevInside)
                    output.Add(IntersectX(prev, curr, maxX));
                output.Add(curr);
            }
            else if (prevInside)
            {
                output.Add(IntersectX(prev, curr, maxX));
            }
        }

        return output.Count >= 3 ? output : null;
    }

    private static List<Vector2> ClipBottom(List<Vector2> poly, float minY)
    {
        var output = new List<Vector2>();
        int n      = poly.Count;

        for (int i = 0; i < n; i++)
        {
            Vector2 curr = poly[i];
            Vector2 prev = poly[(i + n - 1) % n];

            bool currInside = curr.y >= minY - Epsilon;
            bool prevInside = prev.y >= minY - Epsilon;

            if (currInside)
            {
                if (!prevInside)
                    output.Add(IntersectY(prev, curr, minY));
                output.Add(curr);
            }
            else if (prevInside)
            {
                output.Add(IntersectY(prev, curr, minY));
            }
        }

        return output.Count >= 3 ? output : null;
    }

    private static List<Vector2> ClipTop(List<Vector2> poly, float maxY)
    {
        var output = new List<Vector2>();
        int n      = poly.Count;

        for (int i = 0; i < n; i++)
        {
            Vector2 curr = poly[i];
            Vector2 prev = poly[(i + n - 1) % n];

            bool currInside = curr.y <= maxY + Epsilon;
            bool prevInside = prev.y <= maxY + Epsilon;

            if (currInside)
            {
                if (!prevInside)
                    output.Add(IntersectY(prev, curr, maxY));
                output.Add(curr);
            }
            else if (prevInside)
            {
                output.Add(IntersectY(prev, curr, maxY));
            }
        }

        return output.Count >= 3 ? output : null;
    }

    // --- Intersection helpers ---

    private static Vector2 IntersectX(Vector2 a, Vector2 b, float x)
    {
        if (Mathf.Abs(b.x - a.x) < Epsilon) return new Vector2(x, a.y);
        float t = (x - a.x) / (b.x - a.x);
        return new Vector2(x, a.y + t * (b.y - a.y));
    }

    private static Vector2 IntersectY(Vector2 a, Vector2 b, float y)
    {
        if (Mathf.Abs(b.y - a.y) < Epsilon) return new Vector2(a.x, y);
        float t = (y - a.y) / (b.y - a.y);
        return new Vector2(a.x + t * (b.x - a.x), y);
    }

    // --- Cohen-Sutherland segment clipping ---

    private const int Inside = 0, Left = 1, Right = 2, Bottom = 4, Top = 8;

    private static int ComputeCode(Vector2 p, Vector2 min, Vector2 max)
    {
        int code = Inside;
        if      (p.x < min.x) code |= Left;
        else if (p.x > max.x) code |= Right;
        if      (p.y < min.y) code |= Bottom;
        else if (p.y > max.y) code |= Top;
        return code;
    }

    private static bool ClipSegment(ref Vector2 a, ref Vector2 b, Vector2 min, Vector2 max)
    {
        int codeA = ComputeCode(a, min, max);
        int codeB = ComputeCode(b, min, max);

        while (true)
        {
            if ((codeA | codeB) == 0) return true;
            if ((codeA & codeB) != 0) return false;

            int     codeOut = codeA != Inside ? codeA : codeB;
            Vector2 p       = Vector2.zero;
            float   dx      = b.x - a.x;
            float   dy      = b.y - a.y;

            if ((codeOut & Top) != 0)
            {
                p.x = a.x + dx * (max.y - a.y) / dy;
                p.y = max.y;
            }
            else if ((codeOut & Bottom) != 0)
            {
                p.x = a.x + dx * (min.y - a.y) / dy;
                p.y = min.y;
            }
            else if ((codeOut & Right) != 0)
            {
                p.y = a.y + dy * (max.x - a.x) / dx;
                p.x = max.x;
            }
            else
            {
                p.y = a.y + dy * (min.x - a.x) / dx;
                p.x = min.x;
            }

            if (codeOut == codeA) { a = p; codeA = ComputeCode(a, min, max); }
            else                  { b = p; codeB = ComputeCode(b, min, max); }
        }
    }
}

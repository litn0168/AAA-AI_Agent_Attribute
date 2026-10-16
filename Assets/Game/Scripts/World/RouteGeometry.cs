using System;
using System.Collections.Generic;
using UnityEngine;

namespace DarkRealms.World
{
    /// <summary>Outcome of an optional smoothing pass. <see cref="Applied"/> is false when the smoothed shape
    /// was rejected, so a caller can report that the drawn route is still the authored one.</summary>
    public readonly struct SmoothingResult
    {
        public readonly List<Vector2> Points;
        public readonly bool Applied;
        public readonly string Note;

        public SmoothingResult(List<Vector2> points, bool applied, string note)
        {
            Points = points;
            Applied = applied;
            Note = note ?? string.Empty;
        }
    }

    /// <summary>
    /// Geometry rules shared by the route planner, the display and the validator.
    ///
    /// Coordinates come in two spaces and confusing them is the classic way to draw a route through a mountain:
    ///  - level space: a map's own world coordinates, where road segments, blockers and spawn points live;
    ///  - atlas space: the world map's 0..1 layout, where map cards and connectors live.
    /// Each helper states which space it works in, and the conversion between them is explicit.
    /// </summary>
    public static class RouteGeometry
    {
        // ------------------------------------------------------------------ polyline maths

        public static float Length(IReadOnlyList<Vector2> points)
        {
            if (points == null || points.Count < 2)
                return 0f;

            float total = 0f;
            for (int i = 1; i < points.Count; i++)
                total += Vector2.Distance(points[i - 1], points[i]);

            return total;
        }

        /// <summary>True when the two segments cross. Collinear touching counts as an intersection, because a
        /// route sliding exactly along a blocker edge is not a safe route.</summary>
        public static bool SegmentsIntersect(Vector2 a, Vector2 b, Vector2 c, Vector2 d)
        {
            float d1 = Cross(d, c, a);
            float d2 = Cross(d, c, b);
            float d3 = Cross(b, a, c);
            float d4 = Cross(b, a, d);

            if (((d1 > 0f && d2 < 0f) || (d1 < 0f && d2 > 0f)) &&
                ((d3 > 0f && d4 < 0f) || (d3 < 0f && d4 > 0f)))
                return true;

            const float epsilon = 1e-6f;
            if (Mathf.Abs(d1) <= epsilon && OnSegment(c, d, a)) return true;
            if (Mathf.Abs(d2) <= epsilon && OnSegment(c, d, b)) return true;
            if (Mathf.Abs(d3) <= epsilon && OnSegment(a, b, c)) return true;
            if (Mathf.Abs(d4) <= epsilon && OnSegment(a, b, d)) return true;

            return false;
        }

        private static float Cross(Vector2 a, Vector2 b, Vector2 p) =>
            (b.x - a.x) * (p.y - a.y) - (b.y - a.y) * (p.x - a.x);

        private static bool OnSegment(Vector2 a, Vector2 b, Vector2 p) =>
            Mathf.Min(a.x, b.x) - 1e-6f <= p.x && p.x <= Mathf.Max(a.x, b.x) + 1e-6f &&
            Mathf.Min(a.y, b.y) - 1e-6f <= p.y && p.y <= Mathf.Max(a.y, b.y) + 1e-6f;

        public static bool PolylineIntersectsPolygon(IReadOnlyList<Vector2> points, Vector2[] polygon)
        {
            if (points == null || points.Count < 2 || polygon == null || polygon.Length < 3)
                return false;

            for (int i = 1; i < points.Count; i++)
            {
                Vector2 a = points[i - 1];
                Vector2 b = points[i];

                // A fully enclosed segment never crosses an edge, so containment has to be tested as well.
                if (InsidePolygon(a, polygon) || InsidePolygon(b, polygon))
                    return true;

                for (int j = 0; j < polygon.Length; j++)
                {
                    Vector2 c = polygon[j];
                    Vector2 d = polygon[(j + 1) % polygon.Length];
                    if (SegmentsIntersect(a, b, c, d))
                        return true;
                }
            }

            return false;
        }

        public static bool InsidePolygon(Vector2 point, Vector2[] polygon)
        {
            if (polygon == null || polygon.Length < 3)
                return false;

            bool inside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                Vector2 pi = polygon[i];
                Vector2 pj = polygon[j];
                bool straddles = (pi.y > point.y) != (pj.y > point.y);
                if (straddles && point.x < (pj.x - pi.x) * (point.y - pi.y) / (pj.y - pi.y) + pi.x)
                    inside = !inside;
            }

            return inside;
        }

        /// <summary>Shortest distance from a point to a polygon boundary, or 0 when the point is inside.</summary>
        public static float DistanceToPolygon(Vector2 point, Vector2[] polygon)
        {
            if (polygon == null || polygon.Length < 3)
                return float.MaxValue;

            if (InsidePolygon(point, polygon))
                return 0f;

            float best = float.MaxValue;
            for (int i = 0; i < polygon.Length; i++)
            {
                Vector2 a = polygon[i];
                Vector2 b = polygon[(i + 1) % polygon.Length];
                best = Mathf.Min(best, DistanceToSegment(point, a, b));
            }

            return best;
        }

        public static float DistanceToSegment(Vector2 point, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float lengthSquared = ab.sqrMagnitude;
            if (lengthSquared <= 1e-8f)
                return Vector2.Distance(point, a);

            float t = Mathf.Clamp01(Vector2.Dot(point - a, ab) / lengthSquared);
            return Vector2.Distance(point, a + t * ab);
        }

        // ------------------------------------------------------------------ authored geometry

        /// <summary>
        /// The geometry a segment is drawn and validated with: its authored polyline when it has one, otherwise a
        /// straight line between its two nodes. Returning the straight line is a deliberate fallback - it is
        /// flagged as schematic by the caller rather than being presented as a walkable road.
        /// </summary>
        public static Vector2[] ResolveSegmentPoints(MapDefinition map, MapRoadSegmentDefinition segment)
        {
            if (segment == null)
                return Array.Empty<Vector2>();

            if (segment.HasAuthoredGeometry)
                return segment.polyline;

            MapRoadNodeDefinition from = map != null ? map.FindRoadNode(segment.fromNodeId) : null;
            MapRoadNodeDefinition to = map != null ? map.FindRoadNode(segment.toNodeId) : null;
            if (from == null || to == null)
                return Array.Empty<Vector2>();

            return new[] { from.position, to.position };
        }

        /// <summary>Cost used when a segment does not author one: the length of its geometry.</summary>
        public static float EffectiveDistance(MapDefinition map, MapRoadSegmentDefinition segment)
        {
            if (segment == null)
                return 0f;

            if (segment.distance >= 0f)
                return segment.distance;

            return Length(ResolveSegmentPoints(map, segment));
        }

        public static bool CrossesAnyBlocker(IReadOnlyList<Vector2> levelPoints, WorldAtlasDefinition atlas, string mapId)
        {
            // An empty map id is the authored way of declaring an atlas-space blocker (a sea, a mountain range),
            // so it is a valid key. Null means "no space was named", which has no blockers to test against.
            if (atlas == null || mapId == null)
                return false;

            IReadOnlyList<AtlasBlockerDefinition> blockers = atlas.BlockersOf(mapId);
            for (int i = 0; i < blockers.Count; i++)
            {
                AtlasBlockerDefinition blocker = blockers[i];
                if (blocker == null || !blocker.IsWellFormed)
                    continue;

                if (PolylineIntersectsPolygon(levelPoints, blocker.points))
                    return true;
            }

            return false;
        }

        /// <summary>Blocker test for geometry authored in atlas space (inter-map connectors). A blocker with an
        /// empty map id is an atlas-space blocker: a mountain range or a sea that spans the world map.</summary>
        public static bool CrossesAnyAtlasBlocker(IReadOnlyList<Vector2> atlasPoints, WorldAtlasDefinition atlas) =>
            CrossesAnyBlocker(atlasPoints, atlas, string.Empty);

        // ------------------------------------------------------------------ coordinate spaces

        /// <summary>Maps a level position into the atlas layout so the local view and the world view agree.
        /// Atlas scale is authored per map; without it the offset falls back to the map's card, which keeps the
        /// position on the correct card instead of inventing a distance.</summary>
        public static Vector2 LevelToAtlas(MapDefinition map, Vector2 levelPoint)
        {
            if (map == null)
                return levelPoint;

            Rect bounds = map.localBounds;
            Vector2 offset = levelPoint - bounds.center;

            if (map.atlasScale > 0.0001f)
                offset /= map.atlasScale;
            else
                offset = Vector2.zero;

            return map.atlasPosition + offset;
        }

        /// <summary>Inverse of <see cref="LevelToAtlas"/>, used to place a point authored on the world map into
        /// a level. Requires an authored atlas scale; without one the map centre is returned rather than a guess.</summary>
        public static Vector2 AtlasToLevel(MapDefinition map, Vector2 atlasPoint)
        {
            if (map == null)
                return Vector2.zero;

            if (map.atlasScale <= 0.0001f)
                return map.localBounds.center;

            return map.localBounds.center + (atlasPoint - map.atlasPosition) * map.atlasScale;
        }

        // ------------------------------------------------------------------ smoothing

        /// <summary>
        /// Corner cutting that is only accepted when the result is still clear of every blocker. Each pass is
        /// validated as a whole: a pass that would cut a corner through a mountain is discarded and the previous
        /// shape is kept, so smoothing can never turn a valid route into an invalid one.
        /// </summary>
        public static SmoothingResult Smooth(IReadOnlyList<Vector2> points, int passes, float clearance,
            IReadOnlyList<AtlasBlockerDefinition> blockers)
        {
            var current = new List<Vector2>(points ?? (IReadOnlyList<Vector2>)Array.Empty<Vector2>());
            if (current.Count < 3 || passes <= 0)
                return new SmoothingResult(current, false, "not enough points to smooth");

            for (int pass = 0; pass < passes; pass++)
            {
                var candidate = new List<Vector2> { current[0] };
                for (int i = 1; i < current.Count; i++)
                {
                    Vector2 a = current[i - 1];
                    Vector2 b = current[i];
                    candidate.Add(Vector2.Lerp(a, b, 0.25f));
                    candidate.Add(Vector2.Lerp(a, b, 0.75f));
                }

                candidate.Add(current[current.Count - 1]);

                string violation = FindClearanceViolation(candidate, clearance, blockers);
                if (violation != null)
                    return new SmoothingResult(current, false, "smoothing rejected: " + violation);

                current = candidate;
            }

            return new SmoothingResult(current, true, $"smoothed in {passes} pass(es), clearance {clearance:0.##} kept");
        }

        /// <summary>Returns a description of the first clearance violation, or null when the shape is clear.</summary>
        public static string FindClearanceViolation(IReadOnlyList<Vector2> points, float clearance,
            IReadOnlyList<AtlasBlockerDefinition> blockers)
        {
            if (points == null || blockers == null)
                return null;

            for (int i = 0; i < blockers.Count; i++)
            {
                AtlasBlockerDefinition blocker = blockers[i];
                if (blocker == null || !blocker.IsWellFormed)
                    continue;

                if (PolylineIntersectsPolygon(points, blocker.points))
                    return $"crosses blocker '{blocker.id}'";

                for (int p = 0; p < points.Count; p++)
                {
                    float distance = DistanceToPolygon(points[p], blocker.points);
                    if (distance < clearance)
                        return $"passes within {distance:0.##} of blocker '{blocker.id}'";
                }
            }

            return null;
        }
    }
}

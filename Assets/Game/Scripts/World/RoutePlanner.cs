using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DarkRealms.World
{
    /// <summary>Which heuristic the search may use. A heuristic is only ever a search accelerator: it must not
    /// be able to change which route is optimal.</summary>
    public enum RouteHeuristic
    {
        /// <summary>Plain Dijkstra, expressed as A* with a zero heuristic.</summary>
        None,

        /// <summary>Straight-line lower bound measured in atlas space. Usable only for the distance objective and
        /// only when every map declares an atlas scale, because card spacing alone is a drawing decision.</summary>
        AtlasDistanceLowerBound
    }

    public sealed class RouteOptions
    {
        public RouteObjective Objective = RouteObjective.ShortestDistance;
        public RouteHeuristic Heuristic = RouteHeuristic.None;

        /// <summary>When false, locked and gated edges are ignored. Used only to explain why a route is
        /// unavailable, never to walk one.</summary>
        public bool RespectAvailability = true;

        /// <summary>When false, geometry that crosses a blocked area is ignored. Used only to name the offending
        /// geometry when nothing else explains a failure - never to produce a route that would be walked.</summary>
        public bool RespectGeometry = true;

        /// <summary>
        /// Where the traveller actually is, in the start map's own level coordinates. When set, the route starts at
        /// the nearest road node instead of the map's default entry port, so a plan can begin from wherever the
        /// player is standing rather than from wherever they first arrived.
        /// </summary>
        public bool HasStartPosition;
        public Vector2 StartPosition;
    }

    /// <summary>One drawn piece of a route: either a run through a map's road graph or one inter-map hop.</summary>
    public sealed class RouteLeg
    {
        public string MapId = string.Empty;
        public bool IsConnection;
        public string ConnectionId = string.Empty;
        public readonly List<string> SegmentIds = new List<string>();
        public TravelMode Mode;
        public float Distance;
        public float Risk;
        public float Time = -1f;

        /// <summary>Resolved geometry: level space for a road leg, atlas space for a connector.</summary>
        public List<Vector2> Points = new List<Vector2>();

        /// <summary>The map this connector arrives on. Empty for a road leg.</summary>
        public string DestinationMapId = string.Empty;

        /// <summary>True when at least one piece of this leg had no authored geometry, so the drawn line is a
        /// schematic link and is labelled as one.</summary>
        public bool IsSchematic;

        public string Space => IsConnection ? "atlas" : "level";

        public override string ToString() => IsConnection
            ? $"connector {ConnectionId} ({Mode}) dist {Distance:0.#}{(IsSchematic ? ", schematic" : string.Empty)}"
            : $"roads in {MapId} ({SegmentIds.Count} segment(s)) dist {Distance:0.#}{(IsSchematic ? ", schematic" : string.Empty)}";
    }

    /// <summary>The result of one planning run. Everything the UI draws comes from here, so the drawn route and
    /// the route the travel service walks are the same object.</summary>
    public sealed class RoutePlan
    {
        public bool Found;
        public RouteObjective Objective;
        public RouteHeuristic HeuristicUsed;
        public string StartMapId = string.Empty;
        public string DestinationMapId = string.Empty;
        public string StartNodeId = string.Empty;
        public string DestinationNodeId = string.Empty;
        public float TotalCost;
        public float TotalDistance;
        public float TotalRisk;
        public float TotalTime = -1f;
        public bool HasUnknownTime;
        public bool IsSchematic;
        public string FailureReason = string.Empty;
        public string HeuristicNote = string.Empty;

        /// <summary>Requirements standing between the player and the destination when the graph connects them but
        /// the route is not available. Empty when there is no such evidence.</summary>
        public readonly List<string> Requirements = new List<string>();

        public readonly List<RouteLeg> Legs = new List<RouteLeg>();

        /// <summary>Maps travelled through in order, starting with the current map.</summary>
        public readonly List<string> MapSequence = new List<string>();

        public string MapSequenceText => string.Join(" -> ", MapSequence);

        public string CostText()
        {
            switch (Objective)
            {
                case RouteObjective.Fastest: return TotalTime >= 0f ? $"{TotalTime:0.#} s" : "unknown";
                case RouteObjective.Safest: return $"risk {TotalRisk:0.#}";
                default: return $"{TotalDistance:0.#} units";
            }
        }

        public string Describe()
        {
            if (!Found)
                return $"no route: {FailureReason}";

            var sb = new StringBuilder();
            sb.Append(Objective).Append(" route, ").Append(CostText());
            sb.Append(" | distance ").Append(TotalDistance.ToString("0.#"));
            sb.Append(" | risk ").Append(TotalRisk.ToString("0.#"));
            sb.Append(" | time ").Append(TotalTime >= 0f ? TotalTime.ToString("0.#") + " s" : "unknown");
            sb.Append(" | ").Append(MapSequenceText);
            if (IsSchematic)
                sb.Append(" | contains schematic link(s) with no authored geometry");
            return sb.ToString();
        }
    }

    /// <summary>
    /// Builds one weighted graph out of two layers - each map's authored road graph and the explicit inter-map
    /// connections - and searches it.
    ///
    /// One graph, not two, because a route planned map-by-map can disagree with the route the transition service
    /// walks. Here the same nodes and the same edges produce both the drawn line and the travel sequence.
    ///
    /// The search is A* with a pluggable heuristic. For this project's data the heuristic stays at zero (plain
    /// Dijkstra) unless every map declares an atlas scale, because card spacing on the world map is a drawing
    /// decision and must not be used as a travel-distance estimate.
    /// </summary>
    public static class RoutePlanner
    {
        private sealed class Edge
        {
            public int From;
            public int To;
            public string MapId = string.Empty;
            public bool IsConnection;
            public string ConnectionId = string.Empty;
            public string SegmentId = string.Empty;
            public TravelMode Mode;
            public float Distance;
            public float Risk;
            public float Time;
            public bool Available;
            public string UnavailableReason = string.Empty;
            public bool GeometryBlocked;
            public string BlockedBy = string.Empty;
            public bool IsSchematic;
            public Vector2[] Geometry = Array.Empty<Vector2>();
        }

        private sealed class Graph
        {
            public readonly List<string> NodeIds = new List<string>();
            public readonly List<string> NodeMapIds = new List<string>();
            public readonly Dictionary<string, int> Index = new Dictionary<string, int>(StringComparer.Ordinal);
            public readonly List<Edge> Edges = new List<Edge>();
            public readonly List<List<int>> Outgoing = new List<List<int>>();
            public readonly List<string> Notes = new List<string>();
            public float MinAtlasScale;

            public string Key(string mapId, string nodeId) => mapId + "\u001f" + nodeId;

            public bool TryGetNode(string mapId, string nodeId, out int index) =>
                Index.TryGetValue(Key(mapId, nodeId), out index);
        }

        // ------------------------------------------------------------------ public entry point

        /// <summary>
        /// Plans a route from one map to another over the connections that are valid right now. "Optimal" always
        /// means optimal for the selected objective's cost function.
        /// </summary>
        public static RoutePlan Plan(WorldAtlasDefinition atlas, WorldProgress progress, string fromMapId,
            string toMapId, RouteOptions options = null)
        {
            options = options ?? new RouteOptions();
            var plan = new RoutePlan
            {
                Objective = options.Objective,
                StartMapId = fromMapId ?? string.Empty,
                DestinationMapId = toMapId ?? string.Empty
            };

            if (atlas == null)
            {
                plan.FailureReason = "no atlas";
                return plan;
            }

            MapDefinition from = atlas.GetMap(fromMapId);
            MapDefinition to = atlas.GetMap(toMapId);
            if (from == null)
            {
                plan.FailureReason = $"current map '{fromMapId}' is not in the atlas";
                return plan;
            }

            if (to == null)
            {
                plan.FailureReason = $"destination map '{toMapId}' is not in the atlas";
                return plan;
            }

            AtlasRouteSettings settings = atlas.routeSettings ?? new AtlasRouteSettings();
            Graph graph = Build(atlas, progress, options);

            if (!TryResolveStart(graph, from, options, out int start))
            {
                plan.FailureReason = $"map '{from.id}' has no road node to start from";
                return plan;
            }

            // The goal is the destination map itself, not one particular port: arriving at any of its nodes counts,
            // so a route that arrives at the river landing is not forced to walk on to the map's default gate.
            HashSet<int> goals = GoalsFor(graph, to.id);
            if (goals.Count == 0)
            {
                plan.FailureReason = $"map '{to.id}' has no road node to arrive at";
                return plan;
            }

            plan.StartNodeId = graph.NodeIds[start];
            plan.DestinationNodeId = GoalsLabel(graph, goals, to);

            float heuristicScale = ResolveHeuristicScale(atlas, graph, options, plan);

            if (!Search(graph, atlas, start, goals, options.Objective, settings, heuristicScale,
                    options.RespectAvailability, options.RespectGeometry,
                    out int[] previousEdge, out float totalCost, out int reached))
            {
                ExplainFailure(atlas, graph, plan, options, settings);
                return plan;
            }

            plan.TotalCost = totalCost;
            plan.DestinationNodeId = graph.NodeIds[reached];
            BuildLegs(atlas, graph, reached, previousEdge, settings, plan);
            return plan;
        }

        /// <summary>Start node: the nearest road node to the traveller when a position is supplied, otherwise the
        /// map's default entry port, otherwise its first node.</summary>
        private static bool TryResolveStart(Graph graph, MapDefinition map, RouteOptions options, out int start)
        {
            start = -1;

            if (options.HasStartPosition)
            {
                float best = float.MaxValue;
                for (int i = 0; i < graph.NodeMapIds.Count; i++)
                {
                    if (!string.Equals(graph.NodeMapIds[i], map.id, StringComparison.Ordinal))
                        continue;

                    MapRoadNodeDefinition node = map.FindRoadNode(graph.NodeIds[i]);
                    if (node == null)
                        continue;

                    float distance = Vector2.Distance(node.position, options.StartPosition);
                    if (distance < best)
                    {
                        best = distance;
                        start = i;
                    }
                }

                if (start >= 0)
                    return true;
            }

            return graph.TryGetNode(map.id, EntryNodeId(map), out start);
        }

        /// <summary>Every road node of the destination map, which is what "you have arrived" means.</summary>
        private static HashSet<int> GoalsFor(Graph graph, string mapId)
        {
            var goals = new HashSet<int>();
            for (int i = 0; i < graph.NodeMapIds.Count; i++)
                if (string.Equals(graph.NodeMapIds[i], mapId, StringComparison.Ordinal))
                    goals.Add(i);

            return goals;
        }

        private static string GoalsLabel(Graph graph, HashSet<int> goals, MapDefinition map)
        {
            foreach (int index in goals)
                return graph.NodeIds[index] + " (any node of " + map.id + ")";

            return string.Empty;
        }

        /// <summary>Entry node used when the caller does not name one: the map's default entry anchor first, then
        /// its first road node. Never the map centre, which is not somewhere a route can start.</summary>
        private static string EntryNodeId(MapDefinition map)
        {
            MapAnchorDefinition anchor = map.ResolveDefaultEntryAnchor();
            if (anchor != null && !string.IsNullOrEmpty(anchor.roadNodeId))
                return anchor.roadNodeId;

            if (map.roadNodes != null)
                for (int i = 0; i < map.roadNodes.Length; i++)
                    if (map.roadNodes[i] != null)
                        return map.roadNodes[i].id;

            return string.Empty;
        }

        // ------------------------------------------------------------------ graph construction

        private static Graph Build(WorldAtlasDefinition atlas, WorldProgress progress, RouteOptions options)
        {
            var graph = new Graph();
            AtlasRouteSettings settings = atlas.routeSettings ?? new AtlasRouteSettings();

            if (atlas.maps != null)
            {
                for (int m = 0; m < atlas.maps.Length; m++)
                {
                    MapDefinition map = atlas.maps[m];
                    if (map == null)
                        continue;

                    if (map.atlasScale > 0.0001f)
                        graph.MinAtlasScale = graph.MinAtlasScale <= 0f
                            ? map.atlasScale
                            : Mathf.Min(graph.MinAtlasScale, map.atlasScale);

                    if (map.roadNodes == null)
                        continue;

                    for (int n = 0; n < map.roadNodes.Length; n++)
                    {
                        MapRoadNodeDefinition node = map.roadNodes[n];
                        if (node == null || string.IsNullOrEmpty(node.id))
                            continue;

                        string key = graph.Key(map.id, node.id);
                        if (graph.Index.ContainsKey(key))
                            continue;

                        graph.Index[key] = graph.NodeIds.Count;
                        graph.NodeIds.Add(node.id);
                        graph.NodeMapIds.Add(map.id);
                        graph.Outgoing.Add(new List<int>());
                    }
                }
            }

            // Road segments: authored geometry, in the map's own level space.
            if (atlas.maps != null)
            {
                for (int m = 0; m < atlas.maps.Length; m++)
                {
                    MapDefinition map = atlas.maps[m];
                    if (map == null || map.roadSegments == null)
                        continue;

                    for (int s = 0; s < map.roadSegments.Length; s++)
                    {
                        MapRoadSegmentDefinition segment = map.roadSegments[s];
                        if (segment == null)
                            continue;

                        if (!graph.TryGetNode(map.id, segment.fromNodeId, out int a) ||
                            !graph.TryGetNode(map.id, segment.toNodeId, out int b))
                        {
                            graph.Notes.Add($"segment '{segment.id}' in '{map.id}' references a missing road node");
                            continue;
                        }

                        Vector2[] geometry = RouteGeometry.ResolveSegmentPoints(map, segment);
                        bool blocked = settings.rejectGeometryThroughBlockers &&
                                       RouteGeometry.CrossesAnyBlocker(geometry, atlas, map.id);

                        // Availability is recorded truthfully, including on the diagnostic passes: the search decides
                    // whether to honour it, so a relaxed search can still explain what is standing in the way.
                        bool available = segment.condition == null || segment.condition.IsSatisfied(progress);
                        string reason = available ? string.Empty : segment.condition.Describe();

                        AddEdge(graph, a, b, new Edge
                        {
                            From = a,
                            To = b,
                            MapId = map.id,
                            IsConnection = false,
                            SegmentId = segment.id,
                            Mode = segment.mode,
                            Distance = RouteGeometry.EffectiveDistance(map, segment),
                            Risk = Mathf.Max(0f, segment.riskCost),
                            Time = segment.travelTime,
                            Available = available,
                        UnavailableReason = reason,
                        GeometryBlocked = blocked,
                        BlockedBy = blocked ? "a blocked area" : string.Empty,
                        IsSchematic = !segment.HasAuthoredGeometry,
                        Geometry = geometry
                    }, segment.bidirectional);
                    }
                }
            }

            // Inter-map connections attach at named anchors, and an anchor sits on a specific road node.
            if (atlas.connections != null)
            {
                for (int c = 0; c < atlas.connections.Length; c++)
                {
                    MapConnectionDefinition connection = atlas.connections[c];
                    if (connection == null)
                        continue;

                    MapDefinition fromMap = atlas.GetMap(connection.fromMapId);
                    MapDefinition toMap = atlas.GetMap(connection.toMapId);
                    MapAnchorDefinition fromAnchor = fromMap != null ? fromMap.FindAnchor(connection.fromAnchorId) : null;
                    MapAnchorDefinition toAnchor = toMap != null ? toMap.FindAnchor(connection.toAnchorId) : null;

                    if (fromAnchor == null || toAnchor == null)
                    {
                        graph.Notes.Add($"connection '{connection.id}' references a missing anchor");
                        continue;
                    }

                    if (!graph.TryGetNode(connection.fromMapId, fromAnchor.roadNodeId, out int a) ||
                        !graph.TryGetNode(connection.toMapId, toAnchor.roadNodeId, out int b))
                    {
                        graph.Notes.Add($"connection '{connection.id}' attaches to an anchor without a road node");
                        continue;
                    }

                    bool unlocked = progress != null && progress.IsConnectionUnlocked(connection.id);
                    bool satisfied = connection.condition == null || connection.condition.IsSatisfied(progress);
                    bool available = unlocked || satisfied;
                    string reason = available ? string.Empty : connection.condition.Describe();

                    bool blocked = settings.rejectGeometryThroughBlockers && connection.HasAuthoredGeometry &&
                                   RouteGeometry.CrossesAnyAtlasBlocker(connection.atlasPolyline, atlas);

                    AddEdge(graph, a, b, new Edge
                    {
                        From = a,
                        To = b,
                        MapId = connection.fromMapId,
                        IsConnection = true,
                        ConnectionId = connection.id,
                        Mode = connection.travelMode,
                        Distance = Mathf.Max(0f, connection.baseDistance),
                        Risk = Mathf.Max(0f, connection.riskCost),
                        Time = connection.travelTime,
                        Available = available,
                        UnavailableReason = reason,
                        GeometryBlocked = blocked,
                        BlockedBy = blocked ? "a blocked area on the world map" : string.Empty,
                        IsSchematic = !connection.HasAuthoredGeometry,
                        Geometry = connection.atlasPolyline
                    }, connection.direction == ConnectionDirection.Bidirectional);
                }
            }

            return graph;
        }

        private static void AddEdge(Graph graph, int from, int to, Edge edge, bool bidirectional)
        {
            graph.Edges.Add(edge);
            graph.Outgoing[from].Add(graph.Edges.Count - 1);

            if (!bidirectional)
                return;

            var reverse = new Edge
            {
                From = to,
                To = from,
                MapId = edge.MapId,
                IsConnection = edge.IsConnection,
                ConnectionId = edge.ConnectionId,
                SegmentId = edge.SegmentId,
                Mode = edge.Mode,
                Distance = edge.Distance,
                Risk = edge.Risk,
                Time = edge.Time,
                Available = edge.Available,
                UnavailableReason = edge.UnavailableReason,
                GeometryBlocked = edge.GeometryBlocked,
                BlockedBy = edge.BlockedBy,
                IsSchematic = edge.IsSchematic,
                Geometry = edge.Geometry
            };

            graph.Edges.Add(reverse);
            graph.Outgoing[to].Add(graph.Edges.Count - 1);
        }

        // ------------------------------------------------------------------ cost and heuristic

        /// <summary>Cost of one edge for the selected objective. Fastest returns infinity for an edge with no
        /// recorded travel time, which is exactly how "there is no time data here" is expressed.</summary>
        private static float Cost(Edge edge, RouteObjective objective, AtlasRouteSettings settings)
        {
            switch (objective)
            {
                case RouteObjective.Safest:
                    return edge.Risk;
                case RouteObjective.Fastest:
                    return edge.Time >= 0f ? edge.Time : float.PositiveInfinity;
                case RouteObjective.Custom:
                    float time = edge.Time >= 0f ? edge.Time : 0f;
                    return settings.distanceWeight * edge.Distance +
                           settings.riskWeight * edge.Risk +
                           settings.timeWeight * time;
                default:
                    return edge.Distance;
            }
        }

        /// <summary>
        /// Decides whether a straight-line heuristic is admissible for this data and returns its scale (0 means
        /// "use none"). A heuristic is a valid lower bound only when no edge is cheaper than the straight-line
        /// atlas distance between its ends. That is checked rather than assumed; a violation falls back to plain
        /// Dijkstra and records why on the plan.
        /// </summary>
        private static float ResolveHeuristicScale(WorldAtlasDefinition atlas, Graph graph, RouteOptions options, RoutePlan plan)
        {
            plan.HeuristicUsed = RouteHeuristic.None;

            if (options.Heuristic != RouteHeuristic.AtlasDistanceLowerBound)
                return 0f;

            if (options.Objective != RouteObjective.ShortestDistance)
            {
                plan.HeuristicNote = "the atlas-distance heuristic only lower-bounds distance, so it is not used for " +
                                     options.Objective;
                return 0f;
            }

            if (graph.MinAtlasScale <= 0.0001f)
            {
                plan.HeuristicNote = "at least one map does not declare an atlas scale, so atlas spacing is not a " +
                                     "travel distance";
                return 0f;
            }

            for (int i = 0; i < graph.Edges.Count; i++)
            {
                Edge edge = graph.Edges[i];
                if (edge.IsConnection)
                {
                    float atlasLength = RouteGeometry.Length(edge.Geometry);
                    if (edge.Distance + 1e-4f < atlasLength * graph.MinAtlasScale)
                    {
                        plan.HeuristicNote = $"connection '{edge.ConnectionId}' costs less than the straight-line " +
                                             "distance between its ends, so the heuristic would not be a lower bound";
                        return 0f;
                    }

                    continue;
                }

                MapDefinition map = atlas.GetMap(edge.MapId);
                if (map == null || map.atlasScale <= 0.0001f)
                {
                    plan.HeuristicNote = $"map '{edge.MapId}' does not declare an atlas scale";
                    return 0f;
                }

                float projected = RouteGeometry.Length(edge.Geometry) / map.atlasScale * graph.MinAtlasScale;
                if (edge.Distance + 1e-4f < projected)
                {
                    plan.HeuristicNote = $"segment '{edge.SegmentId}' costs less than its projected length";
                    return 0f;
                }
            }

            plan.HeuristicUsed = RouteHeuristic.AtlasDistanceLowerBound;
            return graph.MinAtlasScale;
        }

        /// <summary>Straight-line distance between two nodes in atlas space, converted into the search's cost
        /// units. Only called once <see cref="ResolveHeuristicScale"/> has proved it is a lower bound.</summary>
        private static float Heuristic(Graph graph, WorldAtlasDefinition atlas, int node, HashSet<int> goals, float scale)
        {
            if (scale <= 0f)
                return 0f;

            Vector2 a = NodeAtlasPosition(graph, atlas, node);
            float best = float.MaxValue;
            foreach (int goal in goals)
                best = Mathf.Min(best, Vector2.Distance(a, NodeAtlasPosition(graph, atlas, goal)) * scale);

            return best == float.MaxValue ? 0f : best;
        }

        private static Vector2 NodeAtlasPosition(Graph graph, WorldAtlasDefinition atlas, int node)
        {
            MapDefinition map = atlas.GetMap(graph.NodeMapIds[node]);
            if (map == null)
                return Vector2.zero;

            MapRoadNodeDefinition roadNode = map.FindRoadNode(graph.NodeIds[node]);
            return roadNode != null ? RouteGeometry.LevelToAtlas(map, roadNode.position) : map.atlasPosition;
        }

        // ------------------------------------------------------------------ search

        /// <summary>A* over the available edges. With <paramref name="heuristicScale"/> at zero it is exactly
        /// Dijkstra, which is the documented default for this project's data.</summary>
        private static bool Search(Graph graph, WorldAtlasDefinition atlas, int start, HashSet<int> goals,
            RouteObjective objective, AtlasRouteSettings settings, float heuristicScale, bool respectAvailability,
            bool respectGeometry, out int[] previousEdge, out float totalCost, out int reached)
        {
            int count = graph.NodeIds.Count;
            previousEdge = new int[count];
            var costTo = new float[count];
            var closed = new bool[count];
            reached = -1;

            for (int i = 0; i < count; i++)
            {
                previousEdge[i] = -1;
                costTo[i] = float.PositiveInfinity;
            }

            costTo[start] = 0f;
            var open = new List<int> { start };

            while (open.Count > 0)
            {
                // Linear selection is deliberate: the prototype graphs are tiny, which keeps the search
                // dependency-free and trivially deterministic for tests.
                int bestIndex = 0;
                float bestScore = float.PositiveInfinity;
                for (int i = 0; i < open.Count; i++)
                {
                    int candidate = open[i];
                    float score = costTo[candidate] + Heuristic(graph, atlas, candidate, goals, heuristicScale);
                    if (score < bestScore)
                    {
                        bestScore = score;
                        bestIndex = i;
                    }
                }

                int current = open[bestIndex];
                open.RemoveAt(bestIndex);

                if (goals.Contains(current))
                {
                    totalCost = costTo[current];
                    reached = current;
                    return true;
                }

                if (closed[current])
                    continue;

                closed[current] = true;

                List<int> edges = graph.Outgoing[current];
                for (int e = 0; e < edges.Count; e++)
                {
                    Edge edge = graph.Edges[edges[e]];
                    if (respectAvailability && !edge.Available)
                        continue;

                    if (respectGeometry && edge.GeometryBlocked)
                        continue;

                    float step = Cost(edge, objective, settings);
                    if (float.IsPositiveInfinity(step))
                        continue;

                    int next = edge.To;
                    float candidate = costTo[current] + step;
                    if (candidate + 1e-5f >= costTo[next])
                        continue;

                    costTo[next] = candidate;
                    previousEdge[next] = edges[e];
                    if (!open.Contains(next))
                        open.Add(next);
                }
            }

            totalCost = float.PositiveInfinity;
            return false;
        }

        // ------------------------------------------------------------------ results

        private static void BuildLegs(WorldAtlasDefinition atlas, Graph graph, int goal, int[] previousEdge,
            AtlasRouteSettings settings, RoutePlan plan)
        {
            var nodeChain = new List<int>();
            var edgeChain = new List<Edge>();

            int cursor = goal;
            int guard = 0;
            while (previousEdge[cursor] >= 0 && guard++ < 10000)
            {
                Edge edge = graph.Edges[previousEdge[cursor]];
                edgeChain.Add(edge);
                nodeChain.Add(cursor);
                cursor = edge.From;
            }

            nodeChain.Add(cursor);
            nodeChain.Reverse();
            edgeChain.Reverse();

            plan.Found = true;
            plan.MapSequence.Add(plan.StartMapId);

            RouteLeg current = null;

            for (int i = 0; i < edgeChain.Count; i++)
            {
                Edge edge = edgeChain[i];
                bool traversedForward = nodeChain[i] == edge.From && nodeChain[i + 1] == edge.To;

                Vector2[] points = edge.Geometry;
                if (!traversedForward && points != null && points.Length > 1)
                {
                    var reversed = new Vector2[points.Length];
                    for (int p = 0; p < points.Length; p++)
                        reversed[p] = points[points.Length - 1 - p];
                    points = reversed;
                }

                // A connector is always its own leg: it is drawn in atlas space and belongs to a single link.
                // Consecutive road segments inside one map merge into a single leg.
                bool startsNewLeg = current == null || current.IsConnection || edge.IsConnection ||
                                    !string.Equals(current.MapId, edge.MapId, StringComparison.Ordinal);

                if (startsNewLeg)
                {
                    current = new RouteLeg
                    {
                        MapId = edge.MapId,
                        IsConnection = edge.IsConnection,
                        ConnectionId = edge.ConnectionId,
                        Mode = edge.Mode,
                        DestinationMapId = graph.NodeMapIds[edge.To]
                    };
                    plan.Legs.Add(current);
                }

                plan.TotalDistance += edge.Distance;
                plan.TotalRisk += edge.Risk;

                if (edge.Time >= 0f)
                    plan.TotalTime = (plan.TotalTime < 0f ? 0f : plan.TotalTime) + edge.Time;
                else
                    plan.HasUnknownTime = true;

                current.Distance += edge.Distance;
                current.Risk += edge.Risk;
                if (edge.Time >= 0f)
                    current.Time = (current.Time < 0f ? 0f : current.Time) + edge.Time;

                if (!string.IsNullOrEmpty(edge.SegmentId) && !current.SegmentIds.Contains(edge.SegmentId))
                    current.SegmentIds.Add(edge.SegmentId);

                current.IsSchematic |= edge.IsSchematic;

                if (points != null && points.Length > 0)
                    AppendPoints(current.Points, points);

                if (edge.IsConnection)
                {
                    string arrival = graph.NodeMapIds[edge.To];
                    if (!string.Equals(plan.MapSequence[plan.MapSequence.Count - 1], arrival, StringComparison.Ordinal))
                        plan.MapSequence.Add(arrival);
                }
            }

            foreach (RouteLeg leg in plan.Legs)
            {
                leg.Points = SmoothLeg(atlas, settings, leg);
                plan.IsSchematic |= leg.IsSchematic;
            }

            if (plan.HasUnknownTime)
                plan.TotalTime = -1f;
        }

        /// <summary>Appends points, skipping a duplicated joint so consecutive segments do not double a vertex.</summary>
        private static void AppendPoints(List<Vector2> target, Vector2[] points)
        {
            int start = 0;
            if (target.Count > 0 && Vector2.Distance(target[target.Count - 1], points[0]) < 1e-4f)
                start = 1;

            for (int i = start; i < points.Length; i++)
                target.Add(points[i]);
        }

        private static List<Vector2> SmoothLeg(WorldAtlasDefinition atlas, AtlasRouteSettings settings, RouteLeg leg)
        {
            var blockers = new List<AtlasBlockerDefinition>();
            IReadOnlyList<AtlasBlockerDefinition> source = atlas.BlockersOf(leg.IsConnection ? string.Empty : leg.MapId);
            blockers.AddRange(source);

            SmoothingResult smoothing = RouteGeometry.Smooth(leg.Points, settings.smoothingPasses,
                settings.smoothingClearance, blockers);
            return smoothing.Points;
        }

        /// <summary>
        /// Explains a failed search. The most useful answer is "the graph connects them, but these requirements
        /// stand in the way", so a second search ignores availability and reports what had to be ignored.
        /// </summary>
        private static void ExplainFailure(WorldAtlasDefinition atlas, Graph graph, RoutePlan plan, RouteOptions options,
            AtlasRouteSettings settings)
        {
            var relaxed = new RouteOptions
            {
                Objective = options.Objective,
                Heuristic = RouteHeuristic.None,
                RespectAvailability = false
            };

            Graph openGraph = Build(atlas, null, relaxed);

            bool hasStart = openGraph.TryGetNode(plan.StartMapId, plan.StartNodeId, out int openStart);
            HashSet<int> openGoals = GoalsFor(openGraph, plan.DestinationMapId);
            int[] previousEdge = new int[0];
            int relaxedGoal = -1;
            bool connected = false;

            if (hasStart && openGoals.Count > 0)
                connected = Search(openGraph, atlas, openStart, openGoals, options.Objective, settings, 0f, false, true,
                    out previousEdge, out _, out relaxedGoal);

            if (!connected)
            {
                // Nothing is reachable even with locks ignored, so the graph shape is the problem - unless the only
                // thing standing in the way is geometry, which is worth naming precisely.
                var geometryRelaxed = new RouteOptions
                {
                    Objective = options.Objective,
                    Heuristic = RouteHeuristic.None,
                    RespectAvailability = false,
                    RespectGeometry = false
                };

                Graph geometryGraph = Build(atlas, null, geometryRelaxed);
                HashSet<int> geometryGoals = GoalsFor(geometryGraph, plan.DestinationMapId);
                bool geometryConnected = false;
                int geometryGoal = -1;
                int[] geometryPath = new int[0];

                if (geometryGraph.TryGetNode(plan.StartMapId, plan.StartNodeId, out int gStart) && geometryGoals.Count > 0)
                    geometryConnected = Search(geometryGraph, atlas, gStart, geometryGoals, options.Objective, settings, 0f,
                        false, false, out geometryPath, out _, out geometryGoal);

                if (geometryConnected)
                {
                    var offenders = new List<string>();
                    int node = geometryGoal;
                    int steps = 0;
                    while (geometryPath[node] >= 0 && steps++ < 10000)
                    {
                        Edge edge = geometryGraph.Edges[geometryPath[node]];
                        if (edge.GeometryBlocked)
                            offenders.Add(edge.IsConnection ? $"connection '{edge.ConnectionId}'" : $"segment '{edge.SegmentId}'");

                        node = edge.From;
                    }

                    if (offenders.Count > 0)
                    {
                        plan.FailureReason = "route geometry needs authoring: it crosses a blocked area at " +
                                             string.Join(", ", offenders);
                        return;
                    }
                }

                plan.FailureReason = options.Objective == RouteObjective.Fastest
                    ? "no route with recorded travel times; the fastest objective needs time data on every edge it uses"
                    : "the destination is not connected to the current map";
                return;
            }

            var requirements = new List<string>();
            var blocked = new List<string>();

            int cursor = relaxedGoal;
            int guard = 0;
            while (previousEdge[cursor] >= 0 && guard++ < 10000)
            {
                Edge edge = openGraph.Edges[previousEdge[cursor]];
                if (edge.GeometryBlocked)
                    blocked.Add(edge.IsConnection ? $"connection '{edge.ConnectionId}'" : $"segment '{edge.SegmentId}'");
                else if (!edge.Available && !string.IsNullOrEmpty(edge.UnavailableReason))
                    requirements.Add(edge.UnavailableReason);

                cursor = edge.From;
            }

            foreach (string requirement in requirements)
                if (!plan.Requirements.Contains(requirement))
                    plan.Requirements.Add(requirement);

            if (requirements.Count > 0 && blocked.Count == 0)
                plan.FailureReason = "the route exists but is not available: " + string.Join("; ", plan.Requirements);
            else if (blocked.Count > 0)
                plan.FailureReason = "route geometry needs authoring: it crosses a blocked area at " + string.Join(", ", blocked);
            else
                plan.FailureReason = "no available route";
        }
    }
}

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DarkRealms.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DarkRealms.EditorTools
{
    /// <summary>
    /// Validates the authored atlas and writes a deterministic report.
    ///
    /// It reports only; it never repairs. Fixing data automatically could rename an id or re-route a connection,
    /// and both of those silently invalidate saved progress - so a human decides, and the report is stable enough
    /// to diff between runs and to assert on from a test.
    /// </summary>
    public static class WorldAtlasValidator
    {
        public const string ReportPath = @"D:\Unity Project\My project\WorldAtlasValidationReport.txt";

        public sealed class Entry
        {
            public bool IsError;
            public string Code = string.Empty;
            public string Subject = string.Empty;
            public string Message = string.Empty;

            public override string ToString() =>
                $"{(IsError ? "ERROR" : "WARN ")} [{Code}] {Subject}: {Message}";
        }

        private static readonly List<Entry> Entries = new List<Entry>();

        private static void Error(string code, string subject, string message) =>
            Entries.Add(new Entry { IsError = true, Code = code, Subject = subject, Message = message });

        private static void Warn(string code, string subject, string message) =>
            Entries.Add(new Entry { IsError = false, Code = code, Subject = subject, Message = message });

        public static int ErrorCount { get; private set; }
        public static int WarningCount { get; private set; }

        // ------------------------------------------------------------------ menus

        [MenuItem("Dark Realms/World Map/Validate Atlas")]
        public static void ValidateAtlasFromMenu()
        {
            WorldAtlasDefinition atlas = WorldPrototypeBuilder.LoadAtlas();
            if (atlas == null)
            {
                Debug.LogError($"[WorldAtlasValidator] No atlas at {WorldPrototypeBuilder.AtlasPath}. " +
                               "Run 'Dark Realms/World Map/Build Prototype World' first.");
                return;
            }

            List<Entry> entries = Validate(atlas, includeSceneChecks: true);
            WriteReport(atlas, entries, true);

            Debug.Log($"[WorldAtlasValidator] {ErrorCount} error(s), {WarningCount} warning(s). Report: {ReportPath}");
        }

        // ------------------------------------------------------------------ validation

        /// <summary>Validates an atlas in memory. Scene cross-checks open each map scene additively, so they are
        /// optional: a unit test validating a temporary atlas does not want to touch the project's scenes.</summary>
        public static List<Entry> Validate(WorldAtlasDefinition atlas, bool includeSceneChecks)
        {
            Entries.Clear();
            ErrorCount = 0;
            WarningCount = 0;

            if (atlas == null)
            {
                Error("atlas.missing", "(atlas)", "no atlas was supplied");
                Tally();
                return new List<Entry>(Entries);
            }

            ValidateTags(atlas);
            ValidateLocations(atlas);
            ValidateMaps(atlas);
            ValidateConnections(atlas);
            ValidateMarkers(atlas);
            ValidateNpcRoutes(atlas);
            ValidateBlockersAndGeometry(atlas);
            ValidateFlags(atlas);
            ValidateGraphConnectivity(atlas);
            ValidateCompletion(atlas);

            if (includeSceneChecks)
                ValidateScenes(atlas);

            SortAndTally();
            return new List<Entry>(Entries);
        }

        private static void Tally()
        {
            ErrorCount = 0;
            WarningCount = 0;
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].IsError)
                    ErrorCount++;
                else
                    WarningCount++;
            }
        }

        private static void SortAndTally()
        {
            Entries.Sort((a, b) =>
            {
                int bySeverity = b.IsError.CompareTo(a.IsError);
                if (bySeverity != 0)
                    return bySeverity;

                int byCode = string.CompareOrdinal(a.Code, b.Code);
                if (byCode != 0)
                    return byCode;

                int bySubject = string.CompareOrdinal(a.Subject, b.Subject);
                return bySubject != 0 ? bySubject : string.CompareOrdinal(a.Message, b.Message);
            });

            Tally();
        }

        // ------------------------------------------------------------------ identity

        private static void ValidateTags(WorldAtlasDefinition atlas)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < atlas.tags.Length; i++)
            {
                AtlasTagDefinition tag = atlas.tags[i];
                if (tag == null)
                {
                    Error("tag.null", "tags[" + i + "]", "empty tag entry");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(tag.id))
                    Error("tag.empty_id", "tags[" + i + "]", "tag has an empty id");

                if (!seen.Add(tag.id))
                    Error("tag.duplicate_id", tag.id, "tag id is declared more than once");

                if (string.IsNullOrWhiteSpace(tag.displayName))
                    Warn("tag.empty_name", tag.id, "tag has no display name");
            }
        }

        private static void ValidateLocations(WorldAtlasDefinition atlas)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < atlas.locations.Length; i++)
            {
                AtlasLocationDefinition location = atlas.locations[i];
                if (location == null)
                {
                    Error("location.null", "locations[" + i + "]", "empty location entry");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(location.id))
                    Error("location.empty_id", "locations[" + i + "]", "location has an empty id");

                if (!seen.Add(location.id))
                    Error("location.duplicate_id", location.id, "location id is declared more than once");

                if (!string.IsNullOrEmpty(location.parentId) && atlas.GetLocation(location.parentId) == null)
                    Error("location.missing_parent", location.id, $"parent '{location.parentId}' does not exist");

                if (location.kind != LocationKind.World && string.IsNullOrEmpty(location.parentId))
                    Warn("location.no_parent", location.id, "non-root location has no parent");
            }

            // Parent cycles.
            foreach (AtlasLocationDefinition location in atlas.locations)
            {
                if (location == null)
                    continue;

                var visited = new HashSet<string>(StringComparer.Ordinal);
                string current = location.id;
                int guard = 0;

                while (!string.IsNullOrEmpty(current) && guard++ < 64)
                {
                    if (!visited.Add(current))
                    {
                        Error("location.parent_cycle", location.id, "the location hierarchy contains a cycle");
                        break;
                    }

                    AtlasLocationDefinition node = atlas.GetLocation(current);
                    current = node != null ? node.parentId : string.Empty;
                }
            }

            // Declared tags must exist in the catalog.
            foreach (AtlasLocationDefinition location in atlas.locations)
            {
                if (location?.tags == null)
                    continue;

                for (int i = 0; i < location.tags.Length; i++)
                    if (atlas.GetTag(location.tags[i]) == null)
                        Warn("location.unknown_tag", location.id, $"tag '{location.tags[i]}' is not in the tag catalog");
            }
        }

        private static void ValidateMaps(WorldAtlasDefinition atlas)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var scenePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < atlas.maps.Length; i++)
            {
                MapDefinition map = atlas.maps[i];
                if (map == null)
                {
                    Error("map.null", "maps[" + i + "]", "empty map entry");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(map.id))
                    Error("map.empty_id", "maps[" + i + "]", "map has an empty id");

                if (!seen.Add(map.id))
                    Error("map.duplicate_id", map.id, "map id is declared more than once");

                if (string.IsNullOrEmpty(map.locationId))
                    Error("map.no_location", map.id, "map has no location");
                else if (atlas.GetLocation(map.locationId) == null)
                    Error("map.missing_location", map.id, $"location '{map.locationId}' does not exist");

                if (string.IsNullOrEmpty(map.scenePath))
                    Error("map.no_scene", map.id, "map has no scene path");
                else if (!scenePaths.Add(map.scenePath))
                    Error("map.duplicate_scene", map.id, $"scene '{map.scenePath}' is used by more than one map");

                if (map.thumbnail == null)
                    Warn("map.no_thumbnail", map.id, "no thumbnail; a placeholder is drawn instead");

                if (map.dangerMin < 0 || map.dangerMax < 0)
                    Error("map.negative_danger", map.id, "danger rating cannot be negative");

                if (map.dangerMin > 0 && map.dangerMax > 0 && map.dangerMin > map.dangerMax)
                    Error("map.danger_range", map.id, $"danger range {map.dangerMin}-{map.dangerMax} is inverted");

                if (map.recommendedLevel < 0)
                    Error("map.negative_level", map.id, "recommended level cannot be negative");

                if (map.atlasScale < 0f)
                    Error("map.negative_scale", map.id, "atlas scale cannot be negative");

                if (map.localBounds.width <= 0f || map.localBounds.height <= 0f)
                    Error("map.empty_bounds", map.id, "local bounds have no area, so the local map view cannot map a position");

                if (map.atlasPosition.x < 0f || map.atlasPosition.x > 1f || map.atlasPosition.y < 0f || map.atlasPosition.y > 1f)
                    Warn("map.atlas_position", map.id, "atlas position is outside the 0..1 atlas area");

                if (map.tags != null)
                {
                    for (int t = 0; t < map.tags.Length; t++)
                        if (atlas.GetTag(map.tags[t]) == null)
                            Warn("map.unknown_tag", map.id, $"tag '{map.tags[t]}' is not in the tag catalog");
                }

                ValidateMapGraph(atlas, map);
            }

            MapDefinition start = atlas.GetMap(atlas.startMapId);
            if (string.IsNullOrEmpty(atlas.startMapId))
                Warn("atlas.no_start", atlas.atlasId, "no starting map is set");
            else if (start == null)
                Error("atlas.start_missing", atlas.startMapId, "the starting map is not in the atlas");
        }

        private static void ValidateMapGraph(WorldAtlasDefinition atlas, MapDefinition map)
        {
            var anchorIds = new HashSet<string>(StringComparer.Ordinal);
            if (map.anchors != null)
            {
                for (int i = 0; i < map.anchors.Length; i++)
                {
                    MapAnchorDefinition anchor = map.anchors[i];
                    if (anchor == null)
                    {
                        Error("anchor.null", map.id, "an anchor entry is empty");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(anchor.id))
                        Error("anchor.empty_id", map.id, "an anchor has an empty id");

                    if (!anchorIds.Add(anchor.id))
                        Error("anchor.duplicate_id", anchor.id, $"anchor id is duplicated in '{map.id}'");

                    if (string.IsNullOrEmpty(anchor.roadNodeId))
                        Error("anchor.no_road_node", anchor.id, "anchor is not attached to a road node");
                    else if (map.FindRoadNode(anchor.roadNodeId) == null)
                        Error("anchor.missing_road_node", anchor.id, $"road node '{anchor.roadNodeId}' does not exist in '{map.id}'");
                }
            }

            var nodeIds = new HashSet<string>(StringComparer.Ordinal);
            if (map.roadNodes != null)
            {
                for (int i = 0; i < map.roadNodes.Length; i++)
                {
                    MapRoadNodeDefinition node = map.roadNodes[i];
                    if (node == null)
                    {
                        Error("node.null", map.id, "a road node entry is empty");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(node.id))
                        Error("node.empty_id", map.id, "a road node has an empty id");

                    if (!nodeIds.Add(node.id))
                        Error("node.duplicate_id", node.id, $"road node id is duplicated in '{map.id}'");
                }
            }

            var segmentIds = new HashSet<string>(StringComparer.Ordinal);
            if (map.roadSegments != null)
            {
                for (int i = 0; i < map.roadSegments.Length; i++)
                {
                    MapRoadSegmentDefinition segment = map.roadSegments[i];
                    if (segment == null)
                    {
                        Error("segment.null", map.id, "a road segment entry is empty");
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(segment.id))
                        Error("segment.empty_id", map.id, "a road segment has an empty id");

                    if (!segmentIds.Add(segment.id))
                        Error("segment.duplicate_id", segment.id, $"road segment id is duplicated in '{map.id}'");

                    if (map.FindRoadNode(segment.fromNodeId) == null || map.FindRoadNode(segment.toNodeId) == null)
                        Error("segment.missing_node", segment.id, "road segment references a node that does not exist");

                    if (segment.distance < -1f)
                        Error("segment.negative_cost", segment.id, "road segment cost cannot be negative");

                    if (segment.riskCost < 0f)
                        Error("segment.negative_risk", segment.id, "road segment risk cannot be negative");

                    if (segment.travelTime < -1f)
                        Error("segment.negative_time", segment.id, "travel time cannot be negative other than -1 (unknown)");

                    if (!segment.HasAuthoredGeometry)
                        Warn("segment.no_geometry", segment.id,
                            "no authored geometry: the segment is drawn as a schematic link, not as a walkable road");
                }
            }

            // A map with no road graph cannot be travelled into or out of.
            bool usedByConnections = false;
            for (int i = 0; i < atlas.connections.Length; i++)
            {
                MapConnectionDefinition connection = atlas.connections[i];
                if (connection != null &&
                    (string.Equals(connection.fromMapId, map.id, StringComparison.Ordinal) ||
                     string.Equals(connection.toMapId, map.id, StringComparison.Ordinal)))
                    usedByConnections = true;
            }

            if (usedByConnections && (map.roadNodes == null || map.roadNodes.Length == 0))
                Error("map.no_road_graph", map.id, "map is used by a connection but has no road graph");
        }

        private static void ValidateConnections(WorldAtlasDefinition atlas)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < atlas.connections.Length; i++)
            {
                MapConnectionDefinition connection = atlas.connections[i];
                if (connection == null)
                {
                    Error("connection.null", "connections[" + i + "]", "empty connection entry");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(connection.id))
                    Error("connection.empty_id", "connections[" + i + "]", "connection has an empty id");

                if (!seen.Add(connection.id))
                    Error("connection.duplicate_id", connection.id, "connection id is declared more than once");

                MapDefinition fromMap = atlas.GetMap(connection.fromMapId);
                MapDefinition toMap = atlas.GetMap(connection.toMapId);

                if (fromMap == null)
                    Error("connection.missing_map", connection.id, $"source map '{connection.fromMapId}' does not exist");

                if (toMap == null)
                    Error("connection.missing_map", connection.id, $"destination map '{connection.toMapId}' does not exist");

                if (fromMap != null && fromMap.FindAnchor(connection.fromAnchorId) == null)
                    Error("connection.missing_anchor", connection.id,
                        $"source anchor '{connection.fromAnchorId}' does not exist in '{fromMap.id}'");

                if (toMap != null && toMap.FindAnchor(connection.toAnchorId) == null)
                    Error("connection.missing_anchor", connection.id,
                        $"destination anchor '{connection.toAnchorId}' does not exist in '{toMap.id}'");

                if (string.Equals(connection.fromMapId, connection.toMapId, StringComparison.Ordinal))
                    Warn("connection.self", connection.id, "connection has the same map at both ends");

                if (connection.direction == ConnectionDirection.Bidirectional && toMap != null)
                {
                    // A two-way link has to be usable in both directions, which means the destination anchor must
                    // accept arrivals and the source anchor must accept departures.
                    MapAnchorDefinition toAnchor = toMap.FindAnchor(connection.toAnchorId);
                    MapAnchorDefinition fromAnchor = fromMap != null ? fromMap.FindAnchor(connection.fromAnchorId) : null;

                    if (toAnchor != null && toAnchor.kind == MapAnchorKind.Exit)
                        Error("connection.anchor_direction", connection.id,
                            $"two-way connection arrives at exit-only anchor '{toAnchor.id}'");

                    if (fromAnchor != null && fromAnchor.kind == MapAnchorKind.Entry)
                        Error("connection.anchor_direction", connection.id,
                            $"two-way connection leaves from entry-only anchor '{fromAnchor.id}'");
                }

                if (connection.baseDistance < 0f)
                    Error("connection.negative_cost", connection.id, "connection distance cannot be negative");

                if (connection.riskCost < 0f)
                    Error("connection.negative_risk", connection.id, "connection risk cannot be negative");

                if (connection.travelTime < -1f)
                    Error("connection.negative_time", connection.id, "travel time cannot be negative other than -1 (unknown)");

                if (!connection.HasAuthoredGeometry)
                    Warn("connection.no_geometry", connection.id,
                        "no world-map geometry: the link is drawn thin and labelled schematic");

                ValidateCondition(atlas, connection.condition, connection.id, "connection");
            }

            // An exit that no connection uses is usually a level that cannot be left.
            foreach (MapDefinition map in atlas.maps)
            {
                if (map?.anchors == null)
                    continue;

                for (int a = 0; a < map.anchors.Length; a++)
                {
                    MapAnchorDefinition anchor = map.anchors[a];
                    if (anchor == null || anchor.kind == MapAnchorKind.Entry)
                        continue;

                    // The default entry anchor is where a route starts, not somewhere a connection has to attach.
                    if (string.Equals(anchor.id, map.defaultEntryAnchorId, StringComparison.Ordinal))
                        continue;

                    bool used = false;
                    for (int c = 0; c < atlas.connections.Length && !used; c++)
                    {
                        MapConnectionDefinition connection = atlas.connections[c];
                        if (connection == null)
                            continue;

                        used |= string.Equals(connection.fromAnchorId, anchor.id, StringComparison.Ordinal) &&
                                string.Equals(connection.fromMapId, map.id, StringComparison.Ordinal);
                        used |= string.Equals(connection.toAnchorId, anchor.id, StringComparison.Ordinal) &&
                                string.Equals(connection.toMapId, map.id, StringComparison.Ordinal);
                    }

                    if (!used)
                        Warn("anchor.unused_exit", anchor.id, $"exit anchor in '{map.id}' is not used by any connection");
                }
            }
        }

        private static void ValidateCondition(WorldAtlasDefinition atlas, AtlasCondition condition, string subject, string owner)
        {
            if (condition == null)
            {
                Error("condition.null", subject, $"{owner} has a null availability rule");
                return;
            }

            switch (condition.kind)
            {
                case AtlasConditionKind.Always:
                    return;
                case AtlasConditionKind.FlagSet:
                case AtlasConditionKind.StoryMarkerCompleted:
                    if (string.IsNullOrWhiteSpace(condition.id))
                        Error("condition.empty_id", subject, $"{owner} rule '{condition.kind}' has no id");
                    break;
                default:
                    return;
            }

            if (condition.kind == AtlasConditionKind.FlagSet && !string.IsNullOrEmpty(condition.id))
            {
                bool declared = false;
                for (int i = 0; i < atlas.declaredFlagIds.Length; i++)
                    declared |= string.Equals(atlas.declaredFlagIds[i], condition.id, StringComparison.Ordinal);

                if (!declared)
                    Error("condition.undeclared_flag", subject,
                        $"{owner} depends on flag '{condition.id}', which is not in the atlas's declared flag list");
            }
        }

        private static void ValidateMarkers(WorldAtlasDefinition atlas)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < atlas.pois.Length; i++)
            {
                MapPoiDefinition poi = atlas.pois[i];
                if (poi == null)
                {
                    Error("poi.null", "pois[" + i + "]", "empty marker entry");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(poi.id))
                    Error("poi.empty_id", "pois[" + i + "]", "marker has an empty id");

                if (!seen.Add(poi.id))
                    Error("poi.duplicate_id", poi.id, "marker id is declared more than once");

                if (atlas.GetMap(poi.mapId) == null)
                    Error("poi.missing_map", poi.id, $"marker references map '{poi.mapId}', which does not exist");

                if (poi.dangerLevel < 0)
                    Error("poi.negative_danger", poi.id, "marker danger cannot be negative");

                if (poi.recommendedLevel < 0)
                    Error("poi.negative_level", poi.id, "marker recommended level cannot be negative");

                if (!string.IsNullOrEmpty(poi.npcRouteId) && atlas.GetNpcRoute(poi.npcRouteId) == null)
                    Error("poi.missing_npc_route", poi.id, $"marker references NPC route '{poi.npcRouteId}', which does not exist");

                if (poi.kind == PoiKind.EnemySpecies && string.IsNullOrWhiteSpace(poi.enemySpecies))
                    Warn("poi.no_species", poi.id, "enemy marker has no species, so it cannot be searched or counted");

                if (poi.tags != null)
                {
                    for (int t = 0; t < poi.tags.Length; t++)
                        if (atlas.GetTag(poi.tags[t]) == null)
                            Warn("poi.unknown_tag", poi.id, $"tag '{poi.tags[t]}' is not in the tag catalog");
                }

                ValidateCondition(atlas, poi.condition, poi.id, "marker");
            }

            // Chest totals only exist as a sum of the authored records; recompute them here so a future stored
            // total could never drift away from the markers it claims to summarise.
            foreach (MapDefinition map in atlas.maps)
            {
                if (map == null)
                    continue;

                ChestSummary declared = atlas.ChestSummaryOf(map.id);
                int common = 0, purple = 0, special = 0;
                foreach (MapPoiDefinition poi in atlas.PoisOf(map.id))
                {
                    switch (poi.kind)
                    {
                        case PoiKind.ChestCommon: common++; break;
                        case PoiKind.ChestPurple: purple++; break;
                        case PoiKind.ChestSpecial: special++; break;
                    }
                }

                if (common != declared.Common || purple != declared.Purple || special != declared.Special)
                    Error("chest.summary_mismatch", map.id, "chest summary does not match the authored chest markers");
            }
        }

        private static void ValidateNpcRoutes(WorldAtlasDefinition atlas)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < atlas.npcRoutes.Length; i++)
            {
                NpcRouteDefinition route = atlas.npcRoutes[i];
                if (route == null)
                {
                    Error("npc_route.null", "npcRoutes[" + i + "]", "empty NPC route entry");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(route.id))
                    Error("npc_route.empty_id", "npcRoutes[" + i + "]", "NPC route has an empty id");

                if (!seen.Add(route.id))
                    Error("npc_route.duplicate_id", route.id, "NPC route id is declared more than once");

                if (atlas.GetMap(route.mapId) == null)
                    Error("npc_route.missing_map", route.id, $"NPC route references map '{route.mapId}', which does not exist");

                ValidateCondition(atlas, route.condition, route.id, "NPC route");

                if (!route.HasUsableRoute && route.visibility != NpcRouteVisibility.Hidden)
                    Warn("npc_route.no_data", route.id,
                        "visibility allows drawing this route, but it has fewer than two waypoints - the overlay must stay hidden");
            }
        }

        private static void ValidateBlockersAndGeometry(WorldAtlasDefinition atlas)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < atlas.blockers.Length; i++)
            {
                AtlasBlockerDefinition blocker = atlas.blockers[i];
                if (blocker == null)
                {
                    Error("blocker.null", "blockers[" + i + "]", "empty blocker entry");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(blocker.id))
                    Error("blocker.empty_id", "blockers[" + i + "]", "blocker has an empty id");

                if (!seen.Add(blocker.id))
                    Error("blocker.duplicate_id", blocker.id, "blocker id is declared more than once");

                // An empty map id is the documented way to author a world-map (atlas space) blocker.
                if (!string.IsNullOrEmpty(blocker.mapId) && atlas.GetMap(blocker.mapId) == null)
                    Error("blocker.missing_map", blocker.id, $"blocker references map '{blocker.mapId}', which does not exist");

                if (!blocker.IsWellFormed)
                    Error("blocker.malformed", blocker.id, "blocker polygon needs at least three points");
            }

            AtlasRouteSettings settings = atlas.routeSettings ?? new AtlasRouteSettings();

            foreach (MapDefinition map in atlas.maps)
            {
                if (map?.roadSegments == null)
                    continue;

                for (int s = 0; s < map.roadSegments.Length; s++)
                {
                    MapRoadSegmentDefinition segment = map.roadSegments[s];
                    if (segment == null)
                        continue;

                    Vector2[] geometry = RouteGeometry.ResolveSegmentPoints(map, segment);
                    if (geometry.Length < 2)
                        continue;

                    if (RouteGeometry.CrossesAnyBlocker(geometry, atlas, map.id))
                        Error("geometry.crosses_blocker", segment.id,
                            $"road geometry crosses a blocked area in '{map.id}'; the route needs authoring");

                    // Smoothing must never be the reason a valid route becomes invalid.
                    List<AtlasBlockerDefinition> blockers = new List<AtlasBlockerDefinition>(atlas.BlockersOf(map.id));
                    SmoothingResult smoothing = RouteGeometry.Smooth(geometry, settings.smoothingPasses,
                        settings.smoothingClearance, blockers);

                    if (smoothing.Applied)
                    {
                        string violation = RouteGeometry.FindClearanceViolation(smoothing.Points, settings.smoothingClearance, blockers);
                        if (violation != null)
                            Error("geometry.smoothing_clearance", segment.id, "smoothed route geometry violates clearance: " + violation);
                    }
                }
            }

            if (atlas.connections != null)
            {
                for (int c = 0; c < atlas.connections.Length; c++)
                {
                    MapConnectionDefinition connection = atlas.connections[c];
                    if (connection == null || !connection.HasAuthoredGeometry)
                        continue;

                    if (RouteGeometry.CrossesAnyAtlasBlocker(connection.atlasPolyline, atlas))
                        Error("geometry.crosses_blocker", connection.id,
                            "connection geometry crosses a world-map blocked area; the route needs authoring");
                }
            }
        }

        private static void ValidateFlags(WorldAtlasDefinition atlas)
        {
            var declared = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < atlas.declaredFlagIds.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(atlas.declaredFlagIds[i]))
                    Error("flag.empty_id", "declaredFlagIds[" + i + "]", "declared flag has an empty id");
                else if (!declared.Add(atlas.declaredFlagIds[i]))
                    Error("flag.duplicate_id", atlas.declaredFlagIds[i], "declared flag id is duplicated");
            }
        }

        private static void ValidateGraphConnectivity(WorldAtlasDefinition atlas)
        {
            if (atlas.maps.Length == 0)
                return;

            MapDefinition start = atlas.GetMap(atlas.startMapId) ?? atlas.FirstMap();
            if (start == null)
                return;

            // Reachability ignoring availability: this check is about the shape of the graph, not about locks.
            var reachable = new HashSet<string>(StringComparer.Ordinal) { start.id };
            var frontier = new Queue<string>();
            frontier.Enqueue(start.id);

            while (frontier.Count > 0)
            {
                string current = frontier.Dequeue();
                foreach (MapConnectionDefinition connection in atlas.ConnectionsOf(current))
                {
                    if (connection == null)
                        continue;

                    if (!connection.TryResolveDestination(current, out string next, out _))
                        continue;

                    if (reachable.Add(next))
                        frontier.Enqueue(next);
                }
            }

            foreach (MapDefinition map in atlas.maps)
            {
                if (map == null || reachable.Contains(map.id))
                    continue;

                Error("graph.unreachable", map.id,
                    $"map is not reachable from the starting map '{start.id}' through any connection");
            }

            if (reachable.Count < atlas.maps.Length)
                Warn("graph.disconnected", start.id,
                    $"{atlas.maps.Length - reachable.Count} map(s) sit in a separate component");
        }

        private static void ValidateCompletion(WorldAtlasDefinition atlas)
        {
            foreach (MapDefinition map in atlas.maps)
            {
                if (map == null)
                    continue;

                List<MapPoiDefinition> objectives = atlas.ObjectivesOf(map.id);
                int expected = 0;
                foreach (MapPoiDefinition poi in atlas.PoisOf(map.id))
                    if (poi.countsAsObjective)
                        expected++;

                if (objectives.Count != expected)
                    Error("completion.denominator", map.id, "completion denominator does not match the authored objectives");

                var ids = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < objectives.Count; i++)
                    if (!ids.Add(objectives[i].id))
                        Error("completion.duplicate_objective", objectives[i].id,
                            "the same objective is counted twice in the completion denominator");

                if (objectives.Count == 0)
                    Warn("completion.no_objectives", map.id,
                        "no authored objectives: completion is reported as Unknown / N/A, never as 100%");
            }
        }

        // ------------------------------------------------------------------ scene cross-checks

        /// <summary>
        /// Cross-checks the actual scenes: the identity they report, whether every anchor has an arrival port, and
        /// whether their positions roughly agree with the authored ones.
        ///
        /// Scenes are opened one at a time (not additively) and the previously open scene is restored afterwards.
        /// Loading several prototype maps at once puts several global 2D lights in the same lighting session, which
        /// Unity reports as an error - noise that has nothing to do with the atlas being validated.
        /// </summary>
        private static void ValidateScenes(WorldAtlasDefinition atlas)
        {
            Scene original = SceneManager.GetActiveScene();
            string originalPath = original.path;

            if (!string.IsNullOrEmpty(originalPath) && original.isDirty)
            {
                Warn("scene.checks_skipped", atlas.atlasId,
                    "the open scene has unsaved changes, so scene cross-checks were skipped to avoid discarding them");
                return;
            }

            try
            {
                foreach (MapDefinition map in atlas.maps)
                {
                    if (map == null || string.IsNullOrEmpty(map.scenePath))
                        continue;

                    SceneAsset asset = AssetDatabase.LoadAssetAtPath<SceneAsset>(map.scenePath);
                    if (asset == null)
                    {
                        Error("scene.missing", map.id, $"scene '{map.scenePath}' does not exist");
                        continue;
                    }

                    if (!IsInBuildSettings(map.scenePath))
                        Error("scene.not_in_build", map.id, $"scene '{map.scenePath}' is not enabled in Build Settings");

                    Scene scene = EditorSceneManager.OpenScene(map.scenePath, OpenSceneMode.Single);

                    MapIdentity identity = FindInScene<MapIdentity>(scene);
                    if (identity == null)
                        Error("scene.no_identity", map.id, "scene has no MapIdentity component");
                    else if (!string.Equals(identity.MapId, map.id, StringComparison.Ordinal))
                        Error("scene.identity_mismatch", map.id, $"scene reports map id '{identity.MapId}'");

                    WorldAtlasRuntime runtime = FindInScene<WorldAtlasRuntime>(scene);
                    if (runtime == null)
                        Warn("scene.no_runtime", map.id, "scene has no WorldAtlasRuntime; the scene cannot travel on its own");
                    else if (runtime.Atlas == null)
                        Error("scene.runtime_no_atlas", map.id,
                            "the scene's WorldAtlasRuntime has no atlas assigned, so playing this map on its own cannot travel " +
                            "or plan a route. Re-run 'Dark Realms/World Map/Install World Map Into Open Scene' on it.");

                    if (map.anchors != null)
                    {
                        for (int i = 0; i < map.anchors.Length; i++)
                        {
                            MapAnchorDefinition anchor = map.anchors[i];
                            if (anchor == null)
                                continue;

                            MapEntryPoint port = FindEntryPoint(scene, anchor.id);
                            if (port == null)
                            {
                                Error("scene.missing_port", anchor.id,
                                    $"scene '{map.scenePath}' has no entry port for anchor '{anchor.id}'");
                                continue;
                            }

                            if (Vector2.Distance(port.Position2D, anchor.position) > 2f)
                                Warn("scene.port_position", anchor.id,
                                    $"port sits at {port.Position2D} while the atlas anchor is authored at {anchor.position}");
                        }
                    }
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(originalPath))
                    EditorSceneManager.OpenScene(originalPath, OpenSceneMode.Single);
            }
        }

        private static bool IsInBuildSettings(string path)
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            for (int i = 0; i < scenes.Length; i++)
                if (scenes[i].enabled && string.Equals(scenes[i].path, path, StringComparison.OrdinalIgnoreCase))
                    return true;

            return false;
        }

        private static T FindInScene<T>(Scene scene) where T : Component
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                T[] found = root.GetComponentsInChildren<T>(true);
                if (found.Length > 0)
                    return found[0];
            }

            return null;
        }

        private static MapEntryPoint FindEntryPoint(Scene scene, string anchorId)
        {
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                MapEntryPoint[] ports = root.GetComponentsInChildren<MapEntryPoint>(true);
                for (int i = 0; i < ports.Length; i++)
                    if (string.Equals(ports[i].AnchorId, anchorId, StringComparison.Ordinal))
                        return ports[i];
            }

            return null;
        }

        // ------------------------------------------------------------------ report

        /// <summary>Writes the report. The same atlas always produces the same text, so the file can be diffed
        /// between runs and asserted on by a test.</summary>
        public static void WriteReport(WorldAtlasDefinition atlas, List<Entry> entries, bool includeSummary)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Dark Realms - World Atlas validation report");
            sb.AppendLine("atlas: " + (atlas != null ? $"{atlas.atlasId} ('{atlas.displayName}')" : "<none>"));
            sb.AppendLine("content: " + (atlas != null
                ? $"{atlas.maps.Length} map(s), {atlas.connections.Length} connection(s), {atlas.pois.Length} marker(s), " +
                  $"{atlas.npcRoutes.Length} NPC route(s), {atlas.blockers.Length} blocker(s)"
                : "<none>"));
            sb.AppendLine("result: " + (ErrorCount == 0 ? "PASS" : "FAIL") +
                          $" ({ErrorCount} error(s), {WarningCount} warning(s))");
            sb.AppendLine();

            foreach (Entry entry in entries)
                sb.AppendLine(entry.ToString());

            if (includeSummary)
            {
                sb.AppendLine();
                sb.AppendLine("--- per-map summary ---");
                if (atlas != null)
                {
                    foreach (MapDefinition map in atlas.maps)
                    {
                        if (map == null)
                            continue;

                        ChestSummary chests = atlas.ChestSummaryOf(map.id);
                        DangerRange danger = atlas.DangerRangeOf(map.id);
                        List<string> species = atlas.EnemySpeciesOf(map.id);
                        int segments = map.roadSegments?.Length ?? 0;

                        sb.AppendLine($"{map.id}: chests {chests.Total} ({chests.Common}/{chests.Purple}/{chests.Special}), " +
                                      $"{danger}, enemies [{(species.Count == 0 ? "unknown" : string.Join(", ", species))}], " +
                                      $"objectives {atlas.ObjectivesOf(map.id).Count}, road segments {segments}, " +
                                      $"anchors {map.anchors?.Length ?? 0}");
                    }
                }
            }

            File.WriteAllText(ReportPath, sb.ToString());
        }
    }
}
#endif

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DarkRealms.World
{
    /// <summary>
    /// The authored world atlas: one asset describing the hierarchy, the playable maps, how they connect,
    /// what is on them and which route rules apply. It is read-only at runtime - player progress lives in
    /// <see cref="WorldProgress"/> - so loading a save can never rewrite game content.
    ///
    /// Scope note: this asset deliberately describes the world, not the level layout. Anything that must stay
    /// in sync with an authored level (spawn points, road geometry, blockers) is stored as a coordinate plus a
    /// reference to the map it belongs to.
    /// </summary>
    [CreateAssetMenu(menuName = "Dark Realms/World/Atlas Definition", fileName = "WorldAtlas_")]
    public sealed class WorldAtlasDefinition : ScriptableObject
    {
        [Header("Identity")]
        public string atlasId = "atlas.proto";
        public string displayName = "Dark Realms (prototype atlas)";

        [Tooltip("Map the player starts on when there is no save. Empty means the first map in the list.")]
        public string startMapId = string.Empty;

        [Header("Content")]
        [Tooltip("Every progress flag an availability rule is allowed to read. A condition naming a flag that is " +
                 "not declared here is a validation error, so a gate can never depend on a flag nothing sets.")]
        public string[] declaredFlagIds = Array.Empty<string>();

        public AtlasTagDefinition[] tags = Array.Empty<AtlasTagDefinition>();
        public AtlasLocationDefinition[] locations = Array.Empty<AtlasLocationDefinition>();
        public MapDefinition[] maps = Array.Empty<MapDefinition>();
        public MapConnectionDefinition[] connections = Array.Empty<MapConnectionDefinition>();
        public MapPoiDefinition[] pois = Array.Empty<MapPoiDefinition>();
        public NpcRouteDefinition[] npcRoutes = Array.Empty<NpcRouteDefinition>();
        public AtlasBlockerDefinition[] blockers = Array.Empty<AtlasBlockerDefinition>();

        [Header("Route rules")]
        public AtlasRouteSettings routeSettings = new AtlasRouteSettings();

        // ------------------------------------------------------------------ indexes

        private Dictionary<string, AtlasTagDefinition> tagIndex;
        private Dictionary<string, AtlasLocationDefinition> locationIndex;
        private Dictionary<string, MapDefinition> mapIndex;
        private Dictionary<string, List<MapPoiDefinition>> poiIndex;
        private Dictionary<string, List<NpcRouteDefinition>> npcRouteIndex;
        private Dictionary<string, List<AtlasBlockerDefinition>> blockerIndex;
        private Dictionary<string, List<MapConnectionDefinition>> connectionIndex;

        /// <summary>Drops the lookup tables so they are rebuilt on next use, after an authoring change.</summary>
        public void RebuildIndex()
        {
            tagIndex = null;
            locationIndex = null;
            mapIndex = null;
            poiIndex = null;
            npcRouteIndex = null;
            blockerIndex = null;
            connectionIndex = null;
        }

#if UNITY_EDITOR
        private void OnValidate() => RebuildIndex();
#endif

        private static void Add<TKey, TValue>(Dictionary<TKey, List<TValue>> map, TKey key, TValue value)
        {
            if (!map.TryGetValue(key, out List<TValue> list))
            {
                list = new List<TValue>();
                map[key] = list;
            }

            list.Add(value);
        }

        private void EnsureIndex()
        {
            if (mapIndex != null)
                return;

            tagIndex = new Dictionary<string, AtlasTagDefinition>(StringComparer.Ordinal);
            if (tags != null)
            {
                for (int i = 0; i < tags.Length; i++)
                {
                    AtlasTagDefinition tag = tags[i];
                    if (tag == null || string.IsNullOrEmpty(tag.id) || tagIndex.ContainsKey(tag.id))
                        continue;

                    tagIndex[tag.id] = tag;
                }
            }

            locationIndex = new Dictionary<string, AtlasLocationDefinition>(StringComparer.Ordinal);
            if (locations != null)
            {
                for (int i = 0; i < locations.Length; i++)
                {
                    AtlasLocationDefinition location = locations[i];
                    if (location == null || string.IsNullOrEmpty(location.id) || locationIndex.ContainsKey(location.id))
                        continue;

                    locationIndex[location.id] = location;
                }
            }

            mapIndex = new Dictionary<string, MapDefinition>(StringComparer.Ordinal);
            if (maps != null)
            {
                for (int i = 0; i < maps.Length; i++)
                {
                    MapDefinition map = maps[i];
                    if (map == null || string.IsNullOrEmpty(map.id) || mapIndex.ContainsKey(map.id))
                        continue;

                    mapIndex[map.id] = map;
                }
            }

            poiIndex = new Dictionary<string, List<MapPoiDefinition>>(StringComparer.Ordinal);
            if (pois != null)
            {
                for (int i = 0; i < pois.Length; i++)
                {
                    MapPoiDefinition poi = pois[i];
                    if (poi == null || string.IsNullOrEmpty(poi.mapId))
                        continue;

                    Add(poiIndex, poi.mapId, poi);
                }
            }

            npcRouteIndex = new Dictionary<string, List<NpcRouteDefinition>>(StringComparer.Ordinal);
            if (npcRoutes != null)
            {
                for (int i = 0; i < npcRoutes.Length; i++)
                {
                    NpcRouteDefinition route = npcRoutes[i];
                    if (route == null || string.IsNullOrEmpty(route.mapId))
                        continue;

                    Add(npcRouteIndex, route.mapId, route);
                }
            }

            blockerIndex = new Dictionary<string, List<AtlasBlockerDefinition>>(StringComparer.Ordinal);
            if (blockers != null)
            {
                for (int i = 0; i < blockers.Length; i++)
                {
                    AtlasBlockerDefinition blocker = blockers[i];
                    if (blocker == null)
                        continue;

                    // An empty map id is the documented way to author a world-map (atlas space) blocker, so it is
                    // indexed under the empty key instead of being skipped.
                    Add(blockerIndex, blocker.mapId ?? string.Empty, blocker);
                }
            }

            connectionIndex = new Dictionary<string, List<MapConnectionDefinition>>(StringComparer.Ordinal);
            if (connections != null)
            {
                for (int i = 0; i < connections.Length; i++)
                {
                    MapConnectionDefinition connection = connections[i];
                    if (connection == null)
                        continue;

                    if (!string.IsNullOrEmpty(connection.fromMapId))
                        Add(connectionIndex, connection.fromMapId, connection);

                    // A two-way link belongs to both maps; a one-way link only to its source, which is what
                    // makes "no route in this direction" a data answer rather than a UI special case.
                    if (connection.direction == ConnectionDirection.Bidirectional &&
                        !string.IsNullOrEmpty(connection.toMapId) &&
                        !string.Equals(connection.toMapId, connection.fromMapId, StringComparison.Ordinal))
                    {
                        Add(connectionIndex, connection.toMapId, connection);
                    }
                }
            }
        }

        // ------------------------------------------------------------------ lookups

        public AtlasTagDefinition GetTag(string tagId)
        {
            EnsureIndex();
            return tagId != null && tagIndex.TryGetValue(tagId, out AtlasTagDefinition tag) ? tag : null;
        }

        public bool TryGetTag(string tagId, out AtlasTagDefinition tag)
        {
            tag = GetTag(tagId);
            return tag != null;
        }

        public AtlasLocationDefinition GetLocation(string locationId)
        {
            EnsureIndex();
            return locationId != null && locationIndex.TryGetValue(locationId, out AtlasLocationDefinition location)
                ? location
                : null;
        }

        public MapDefinition GetMap(string mapId)
        {
            EnsureIndex();
            return mapId != null && mapIndex.TryGetValue(mapId, out MapDefinition map) ? map : null;
        }

        /// <summary>Resolves a map from the project-relative scene path, used when a scene reports itself.</summary>
        public MapDefinition GetMapByScenePath(string scenePath)
        {
            if (string.IsNullOrEmpty(scenePath) || maps == null)
                return null;

            for (int i = 0; i < maps.Length; i++)
            {
                MapDefinition map = maps[i];
                if (map != null && string.Equals(map.scenePath, scenePath, StringComparison.OrdinalIgnoreCase))
                    return map;
            }

            return null;
        }

        public IReadOnlyList<MapPoiDefinition> PoisOf(string mapId)
        {
            EnsureIndex();
            return mapId != null && poiIndex.TryGetValue(mapId, out List<MapPoiDefinition> list)
                ? list
                : (IReadOnlyList<MapPoiDefinition>)Array.Empty<MapPoiDefinition>();
        }

        public IReadOnlyList<NpcRouteDefinition> NpcRoutesOf(string mapId)
        {
            EnsureIndex();
            return mapId != null && npcRouteIndex.TryGetValue(mapId, out List<NpcRouteDefinition> list)
                ? list
                : (IReadOnlyList<NpcRouteDefinition>)Array.Empty<NpcRouteDefinition>();
        }

        public IReadOnlyList<AtlasBlockerDefinition> BlockersOf(string mapId)
        {
            EnsureIndex();
            return mapId != null && blockerIndex.TryGetValue(mapId, out List<AtlasBlockerDefinition> list)
                ? list
                : (IReadOnlyList<AtlasBlockerDefinition>)Array.Empty<AtlasBlockerDefinition>();
        }

        /// <summary>Connections attached to a map, in either direction where the link allows it.</summary>
        public IReadOnlyList<MapConnectionDefinition> ConnectionsOf(string mapId)
        {
            EnsureIndex();
            return mapId != null && connectionIndex.TryGetValue(mapId, out List<MapConnectionDefinition> list)
                ? list
                : (IReadOnlyList<MapConnectionDefinition>)Array.Empty<MapConnectionDefinition>();
        }

        public MapConnectionDefinition GetConnection(string connectionId)
        {
            if (string.IsNullOrEmpty(connectionId) || connections == null)
                return null;

            for (int i = 0; i < connections.Length; i++)
                if (connections[i] != null && string.Equals(connections[i].id, connectionId, StringComparison.Ordinal))
                    return connections[i];

            return null;
        }

        public MapPoiDefinition GetPoi(string poiId)
        {
            if (string.IsNullOrEmpty(poiId) || pois == null)
                return null;

            for (int i = 0; i < pois.Length; i++)
                if (pois[i] != null && string.Equals(pois[i].id, poiId, StringComparison.Ordinal))
                    return pois[i];

            return null;
        }

        public NpcRouteDefinition GetNpcRoute(string routeId)
        {
            if (string.IsNullOrEmpty(routeId) || npcRoutes == null)
                return null;

            for (int i = 0; i < npcRoutes.Length; i++)
                if (npcRoutes[i] != null && string.Equals(npcRoutes[i].id, routeId, StringComparison.Ordinal))
                    return npcRoutes[i];

            return null;
        }

        /// <summary>First map of the atlas, used when nothing else designates a starting map.</summary>
        public MapDefinition FirstMap()
        {
            if (maps == null)
                return null;

            for (int i = 0; i < maps.Length; i++)
                if (maps[i] != null)
                    return maps[i];

            return null;
        }

        // ------------------------------------------------------------------ derived summaries

        /// <summary>Chest totals for a map, counted from the authored markers every time. There is no
        /// stored total that could disagree with them.</summary>
        public ChestSummary ChestSummaryOf(string mapId)
        {
            int common = 0, purple = 0, special = 0;
            IReadOnlyList<MapPoiDefinition> list = PoisOf(mapId);
            for (int i = 0; i < list.Count; i++)
            {
                switch (list[i].Tier)
                {
                    case ChestTier.Common: common++; break;
                    case ChestTier.Purple: purple++; break;
                    case ChestTier.Special: special++; break;
                }
            }

            return new ChestSummary(common, purple, special);
        }

        /// <summary>Danger range for a map: its own authored range widened by any marker that declares one.
        /// Unknown stays unknown - nothing is invented for a map with no rating.</summary>
        public DangerRange DangerRangeOf(string mapId)
        {
            MapDefinition map = GetMap(mapId);
            int min = 0, max = 0;

            if (map != null && map.HasDanger)
            {
                min = map.dangerMin;
                max = map.dangerMax > 0 ? map.dangerMax : map.dangerMin;
            }

            IReadOnlyList<MapPoiDefinition> list = PoisOf(mapId);
            for (int i = 0; i < list.Count; i++)
            {
                int level = list[i].dangerLevel;
                if (level <= 0)
                    continue;

                min = min == 0 ? level : Mathf.Min(min, level);
                max = Mathf.Max(max, level);
            }

            return new DangerRange(min, max);
        }

        /// <summary>Enemy species recorded for a map. An empty result means "not recorded", never "none".</summary>
        public List<string> EnemySpeciesOf(string mapId)
        {
            var species = new List<string>();
            IReadOnlyList<MapPoiDefinition> list = PoisOf(mapId);
            for (int i = 0; i < list.Count; i++)
            {
                MapPoiDefinition poi = list[i];
                if (poi.kind != PoiKind.EnemySpecies || string.IsNullOrEmpty(poi.enemySpecies))
                    continue;

                if (!species.Contains(poi.enemySpecies))
                    species.Add(poi.enemySpecies);
            }

            return species;
        }

        public bool HasPoiOfKind(string mapId, params PoiKind[] kinds)
        {
            IReadOnlyList<MapPoiDefinition> list = PoisOf(mapId);
            for (int i = 0; i < list.Count; i++)
            {
                PoiKind kind = list[i].kind;
                for (int k = 0; k < kinds.Length; k++)
                    if (kinds[k] == kind)
                        return true;
            }

            return false;
        }

        /// <summary>Authored completion objectives. The denominator of map completion comes from here and
        /// nowhere else, so it can never count a task that does not exist.</summary>
        public List<MapPoiDefinition> ObjectivesOf(string mapId)
        {
            var objectives = new List<MapPoiDefinition>();
            IReadOnlyList<MapPoiDefinition> list = PoisOf(mapId);
            for (int i = 0; i < list.Count; i++)
                if (list[i].countsAsObjective)
                    objectives.Add(list[i]);

            return objectives;
        }

        /// <summary>Human-readable hierarchy path for a map, e.g. "World / Prototype Continent / Grassland".
        /// Missing parents are reported rather than silently skipped.</summary>
        public string LocationBreadcrumb(string mapId)
        {
            MapDefinition map = GetMap(mapId);
            if (map == null)
                return "(unknown map)";

            var parts = new List<string>();
            string current = map.locationId;
            int guard = 0;

            while (!string.IsNullOrEmpty(current) && guard++ < 32)
            {
                AtlasLocationDefinition location = GetLocation(current);
                if (location == null)
                {
                    parts.Add(current + " (missing)");
                    break;
                }

                parts.Add(location.displayName);
                current = location.parentId;
            }

            if (map.locationId.Length == 0)
                parts.Add("(no location)");

            parts.Reverse();
            parts.Add(map.displayName);
            return string.Join(" / ", parts);
        }

        /// <summary>All tag ids used by a map: its own tags plus the tags of every location above it.</summary>
        public HashSet<string> EffectiveTagsOf(string mapId)
        {
            var result = new HashSet<string>(StringComparer.Ordinal);
            MapDefinition map = GetMap(mapId);
            if (map == null)
                return result;

            if (map.tags != null)
                for (int i = 0; i < map.tags.Length; i++)
                    result.Add(map.tags[i]);

            string current = map.locationId;
            int guard = 0;
            while (!string.IsNullOrEmpty(current) && guard++ < 32)
            {
                AtlasLocationDefinition location = GetLocation(current);
                if (location == null)
                    break;

                if (location.tags != null)
                    for (int i = 0; i < location.tags.Length; i++)
                        result.Add(location.tags[i]);

                current = location.parentId;
            }

            return result;
        }

        /// <summary>Searches names, aliases and ids. Case-insensitive and deterministic in list order.</summary>
        public List<MapDefinition> SearchByName(string text)
        {
            var results = new List<MapDefinition>();
            if (maps == null)
                return results;

            for (int i = 0; i < maps.Length; i++)
            {
                MapDefinition map = maps[i];
                if (map == null)
                    continue;

                if (string.IsNullOrWhiteSpace(text) || MatchesText(map, text))
                    results.Add(map);
            }

            return results;
        }

        private static bool MatchesText(MapDefinition map, string text)
        {
            if (Contains(map.id, text) || Contains(map.displayName, text))
                return true;

            if (map.aliases != null)
                for (int i = 0; i < map.aliases.Length; i++)
                    if (Contains(map.aliases[i], text))
                        return true;

            return false;
        }

        private static bool Contains(string haystack, string needle) =>
            !string.IsNullOrEmpty(haystack) &&
            haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>Short summary used by the map card and the details panel. Unknown data is shown as
        /// unknown, never as a zero.</summary>
        public string DescribeMap(string mapId)
        {
            MapDefinition map = GetMap(mapId);
            if (map == null)
                return "(unknown map)";

            var sb = new StringBuilder();
            sb.Append(map.mapType);
            ChestSummary chests = ChestSummaryOf(mapId);
            if (!chests.IsEmpty)
                sb.Append(" | chests: ").Append(chests.Total);

            DangerRange danger = DangerRangeOf(mapId);
            sb.Append(" | ").Append(danger.IsKnown ? danger.ToString() : "danger unknown");

            List<string> species = EnemySpeciesOf(mapId);
            sb.Append(" | enemies: ").Append(species.Count == 0 ? "unknown" : string.Join(", ", species));
            return sb.ToString();
        }
    }
}

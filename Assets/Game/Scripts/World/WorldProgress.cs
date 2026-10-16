using System;
using System.Collections.Generic;
using UnityEngine;

namespace DarkRealms.World
{
    /// <summary>
    /// Mutable player progress through the atlas. This is the only thing a save contains: the authored atlas
    /// asset is never written to, so a save can always be loaded into edited content, and an unmodified
    /// definition can never be corrupted by a play session.
    ///
    /// Every state transition here is monotonic where the meaning is "the player has achieved something":
    /// a chest that was opened does not become unopened because an older copy of the state was applied.
    /// </summary>
    [Serializable]
    public sealed class WorldProgress
    {
        /// <summary>Bumped whenever the persisted shape changes; older saves are migrated or rejected
        /// explicitly instead of being loaded half-read.</summary>
        public const int CurrentSchemaVersion = 1;

        public int schemaVersion = CurrentSchemaVersion;

        // -------- position
        public string currentMapId = string.Empty;
        public string lastSafeAnchorId = string.Empty;

        // -------- world state that availability rules can read
        public bool isNight;

        // -------- discovery
        private readonly HashSet<string> visitedMaps = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> discoveredMaps = new HashSet<string>(StringComparer.Ordinal);

        // -------- unlocks
        private readonly HashSet<string> flags = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> unlockedConnections = new HashSet<string>(StringComparer.Ordinal);

        // -------- markers
        private readonly Dictionary<string, DiscoveryState> poiStates = new Dictionary<string, DiscoveryState>(StringComparer.Ordinal);

        // -------- story / services
        private readonly HashSet<string> completedStoryMarkers = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> unlockedGuilds = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> unlockedAltars = new HashSet<string>(StringComparer.Ordinal);
        private readonly HashSet<string> knownNpcRoutes = new HashSet<string>(StringComparer.Ordinal);

        // -------- convenience
        private readonly HashSet<string> favoriteMaps = new HashSet<string>(StringComparer.Ordinal);
        private readonly List<string> recentDestinations = new List<string>();

        /// <summary>Raised after any state change, so open UI can refresh without polling.</summary>
        public event Action Changed;

        /// <summary>Current time of day. Availability rules read this, which is how a night-only NPC route or
        /// gate is expressed without a second state object.</summary>
        public bool IsNight
        {
            get => isNight;
            set
            {
                if (isNight == value)
                    return;

                isNight = value;
                Raise();
            }
        }

        // ------------------------------------------------------------------ queries

        public bool HasVisited(string mapId) => mapId != null && visitedMaps.Contains(mapId);

        public bool IsDiscovered(string mapId) => mapId != null && discoveredMaps.Contains(mapId);

        public bool HasFlag(string flagId) => !string.IsNullOrEmpty(flagId) && flags.Contains(flagId);

        public bool IsConnectionUnlocked(string connectionId) =>
            !string.IsNullOrEmpty(connectionId) && unlockedConnections.Contains(connectionId);

        public DiscoveryState PoiState(string poiId)
        {
            if (string.IsNullOrEmpty(poiId))
                return DiscoveryState.Undiscovered;

            return poiStates.TryGetValue(poiId, out DiscoveryState state) ? state : DiscoveryState.Undiscovered;
        }

        public bool IsStoryMarkerCompleted(string markerId) =>
            !string.IsNullOrEmpty(markerId) && completedStoryMarkers.Contains(markerId);

        public bool IsGuildUnlocked(string guildId) => !string.IsNullOrEmpty(guildId) && unlockedGuilds.Contains(guildId);

        public bool IsAltarUnlocked(string altarId) => !string.IsNullOrEmpty(altarId) && unlockedAltars.Contains(altarId);

        public bool IsNpcRouteKnown(string routeId) => !string.IsNullOrEmpty(routeId) && knownNpcRoutes.Contains(routeId);

        public bool IsFavorite(string mapId) => !string.IsNullOrEmpty(mapId) && favoriteMaps.Contains(mapId);

        public IReadOnlyCollection<string> VisitedMaps => visitedMaps;
        public IReadOnlyCollection<string> DiscoveredMaps => discoveredMaps;
        public IReadOnlyCollection<string> Flags => flags;
        public IReadOnlyCollection<string> UnlockedConnections => unlockedConnections;
        public IReadOnlyCollection<string> KnownNpcRoutes => knownNpcRoutes;
        public IReadOnlyCollection<string> CompletedStoryMarkers => completedStoryMarkers;
        public IReadOnlyCollection<string> Favorites => favoriteMaps;
        public IReadOnlyList<string> RecentDestinations => recentDestinations;

        // ------------------------------------------------------------------ mutations

        public void SetCurrentMap(string mapId, string anchorId)
        {
            bool changed = !string.Equals(currentMapId, mapId, StringComparison.Ordinal);
            currentMapId = mapId ?? string.Empty;

            if (!string.IsNullOrEmpty(anchorId))
                lastSafeAnchorId = anchorId;

            changed |= Visit(mapId);
            if (changed)
                Raise();
        }

        /// <summary>Marks a map visited. Visiting always implies discovered; the reverse is not true, because a
        /// map can be known about without having been entered.</summary>
        public bool Visit(string mapId)
        {
            if (string.IsNullOrEmpty(mapId))
                return false;

            bool changed = visitedMaps.Add(mapId);
            changed |= discoveredMaps.Add(mapId);
            return changed;
        }

        public void Discover(string mapId)
        {
            if (!string.IsNullOrEmpty(mapId) && discoveredMaps.Add(mapId))
                Raise();
        }

        public void SetFlag(string flagId)
        {
            if (!string.IsNullOrEmpty(flagId) && flags.Add(flagId))
                Raise();
        }

        public void UnlockConnection(string connectionId)
        {
            if (!string.IsNullOrEmpty(connectionId) && unlockedConnections.Add(connectionId))
                Raise();
        }

        /// <summary>Records marker progress. Only forward moves are accepted (Undiscovered -> Discovered ->
        /// Completed), so replaying an older save cannot un-loot a chest.</summary>
        public void SetPoiState(string poiId, DiscoveryState state)
        {
            if (string.IsNullOrEmpty(poiId))
                return;

            DiscoveryState current = PoiState(poiId);
            if (state <= current)
                return;

            poiStates[poiId] = state;
            Raise();
        }

        public void CompleteStoryMarker(string markerId)
        {
            if (!string.IsNullOrEmpty(markerId) && completedStoryMarkers.Add(markerId))
                Raise();
        }

        public void UnlockGuild(string guildId)
        {
            if (!string.IsNullOrEmpty(guildId) && unlockedGuilds.Add(guildId))
                Raise();
        }

        public void UnlockAltar(string altarId)
        {
            if (!string.IsNullOrEmpty(altarId) && unlockedAltars.Add(altarId))
                Raise();
        }

        public void MarkNpcRouteKnown(string routeId)
        {
            if (!string.IsNullOrEmpty(routeId) && knownNpcRoutes.Add(routeId))
                Raise();
        }

        public void ToggleFavorite(string mapId)
        {
            if (string.IsNullOrEmpty(mapId))
                return;

            if (!favoriteMaps.Remove(mapId))
                favoriteMaps.Add(mapId);

            Raise();
        }

        public void NoteDestination(string mapId)
        {
            if (string.IsNullOrEmpty(mapId))
                return;

            recentDestinations.Remove(mapId);
            recentDestinations.Insert(0, mapId);
            while (recentDestinations.Count > 10)
                recentDestinations.RemoveAt(recentDestinations.Count - 1);

            Raise();
        }

        /// <summary>Marks every authored objective of a map complete. Used by the prototype travel flow once
        /// its single objective exists; authored content stays the source of truth for the denominator.</summary>
        public void Clear()
        {
            currentMapId = string.Empty;
            lastSafeAnchorId = string.Empty;
            isNight = false;
            visitedMaps.Clear();
            discoveredMaps.Clear();
            flags.Clear();
            unlockedConnections.Clear();
            poiStates.Clear();
            completedStoryMarkers.Clear();
            unlockedGuilds.Clear();
            unlockedAltars.Clear();
            knownNpcRoutes.Clear();
            favoriteMaps.Clear();
            recentDestinations.Clear();
            Raise();
        }

        public void Raise() => Changed?.Invoke();

        // ------------------------------------------------------------------ persistence shape

        /// <summary>Copies the state into the flat, JsonUtility-friendly save shape.</summary>
        public WorldSaveData ToSaveData()
        {
            var data = new WorldSaveData
            {
                schemaVersion = CurrentSchemaVersion,
                currentMapId = currentMapId,
                lastSafeAnchorId = lastSafeAnchorId,
                isNight = isNight,
                visitedMaps = ToSortedArray(visitedMaps),
                discoveredMaps = ToSortedArray(discoveredMaps),
                flags = ToSortedArray(flags),
                unlockedConnections = ToSortedArray(unlockedConnections),
                completedStoryMarkers = ToSortedArray(completedStoryMarkers),
                unlockedGuilds = ToSortedArray(unlockedGuilds),
                unlockedAltars = ToSortedArray(unlockedAltars),
                knownNpcRoutes = ToSortedArray(knownNpcRoutes),
                favoriteMaps = ToSortedArray(favoriteMaps),
                recentDestinations = recentDestinations.ToArray()
            };

            var poiIds = new List<string>(poiStates.Keys);
            poiIds.Sort(StringComparer.Ordinal);
            var states = new int[poiIds.Count];
            for (int i = 0; i < poiIds.Count; i++)
                states[i] = (int)poiStates[poiIds[i]];

            data.poiIds = poiIds.ToArray();
            data.poiStates = states;
            return data;
        }

        /// <summary>Replaces the state from a save. Unknown or malformed entries are ignored rather than
        /// throwing, so a partially damaged save still restores what it can.</summary>
        public void LoadFrom(WorldSaveData data)
        {
            Clear();
            if (data == null)
                return;

            schemaVersion = data.schemaVersion;
            currentMapId = data.currentMapId ?? string.Empty;
            lastSafeAnchorId = data.lastSafeAnchorId ?? string.Empty;
            isNight = data.isNight;

            AddAll(visitedMaps, data.visitedMaps);
            AddAll(discoveredMaps, data.discoveredMaps);
            AddAll(flags, data.flags);
            AddAll(unlockedConnections, data.unlockedConnections);
            AddAll(completedStoryMarkers, data.completedStoryMarkers);
            AddAll(unlockedGuilds, data.unlockedGuilds);
            AddAll(unlockedAltars, data.unlockedAltars);
            AddAll(knownNpcRoutes, data.knownNpcRoutes);
            AddAll(favoriteMaps, data.favoriteMaps);

            if (data.recentDestinations != null)
                foreach (string mapId in data.recentDestinations)
                    if (!string.IsNullOrEmpty(mapId) && !recentDestinations.Contains(mapId))
                        recentDestinations.Add(mapId);

            int count = Mathf.Min(data.poiIds?.Length ?? 0, data.poiStates?.Length ?? 0);
            for (int i = 0; i < count; i++)
            {
                string poiId = data.poiIds[i];
                if (string.IsNullOrEmpty(poiId))
                    continue;

                int state = Mathf.Clamp(data.poiStates[i], 0, (int)DiscoveryState.Completed);
                poiStates[poiId] = (DiscoveryState)state;
            }

            Raise();
        }

        private static string[] ToSortedArray(HashSet<string> set)
        {
            var list = new List<string>(set);
            list.Sort(StringComparer.Ordinal);
            return list.ToArray();
        }

        private static void AddAll(HashSet<string> target, string[] source)
        {
            if (source == null)
                return;

            for (int i = 0; i < source.Length; i++)
                if (!string.IsNullOrEmpty(source[i]))
                    target.Add(source[i]);
        }
    }

    /// <summary>
    /// Flat persistence shape. Deliberately arrays rather than dictionaries: Unity's JsonUtility cannot
    /// serialize dictionaries, and a flat shape keeps a save diffable and forward-compatible.
    /// </summary>
    [Serializable]
    public sealed class WorldSaveData
    {
        public int schemaVersion = WorldProgress.CurrentSchemaVersion;
        public string currentMapId = string.Empty;
        public string lastSafeAnchorId = string.Empty;
        public bool isNight;

        public string[] visitedMaps = Array.Empty<string>();
        public string[] discoveredMaps = Array.Empty<string>();
        public string[] flags = Array.Empty<string>();
        public string[] unlockedConnections = Array.Empty<string>();
        public string[] completedStoryMarkers = Array.Empty<string>();
        public string[] unlockedGuilds = Array.Empty<string>();
        public string[] unlockedAltars = Array.Empty<string>();
        public string[] knownNpcRoutes = Array.Empty<string>();
        public string[] favoriteMaps = Array.Empty<string>();
        public string[] recentDestinations = Array.Empty<string>();

        public string[] poiIds = Array.Empty<string>();
        public int[] poiStates = Array.Empty<int>();
    }

    /// <summary>What happened while loading, so a caller can report it instead of guessing.</summary>
    public enum WorldSaveLoadOutcome
    {
        FreshStart,
        Loaded,
        LoadedFromBackup,
        RecoveredAfterCorruption,
        RejectedNewerSchema
    }

    public readonly struct WorldSaveLoadResult
    {
        public readonly WorldSaveLoadOutcome Outcome;
        public readonly string Detail;

        public WorldSaveLoadResult(WorldSaveLoadOutcome outcome, string detail)
        {
            Outcome = outcome;
            Detail = detail ?? string.Empty;
        }

        public bool Restored => Outcome == WorldSaveLoadOutcome.Loaded || Outcome == WorldSaveLoadOutcome.LoadedFromBackup;
    }
}

using System;
using System.Collections.Generic;
using DarkRealms.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DarkRealms.World
{
    /// <summary>How far a map has been completed. An unknown denominator stays unknown instead of
    /// reporting a division by zero as 100%.</summary>
    public readonly struct MapCompletion
    {
        public readonly int Completed;
        public readonly int Eligible;
        public readonly bool IsKnown;

        public MapCompletion(int completed, int eligible, bool isKnown)
        {
            Completed = completed;
            Eligible = eligible;
            IsKnown = isKnown;
        }

        public float Ratio => IsKnown && Eligible > 0 ? (float)Completed / Eligible : 0f;

        public override string ToString() => IsKnown ? $"{Completed}/{Eligible}" : "Unknown / N/A";
    }

    /// <summary>
    /// The live world state: the authored atlas, the player's progress through it, and the save lifecycle.
    ///
    /// It is the one thing in the world feature that must outlive a scene load, so it is created once, marked
    /// DontDestroyOnLoad and refuses to create a second copy. Everything else (the world map screen, the
    /// transition service) is either owned by it or discovered lazily, which is what keeps a transition from
    /// leaving two managers, two players or two event systems behind.
    ///
    /// Execution order is pinned in front of the frame so that the current map, the save load and the shared
    /// Event System all exist before any gameplay or UI component runs its own Awake.
    /// </summary>
    [DefaultExecutionOrder(-300)]
    [DisallowMultipleComponent]
    public sealed class WorldAtlasRuntime : MonoBehaviour
    {
        public static WorldAtlasRuntime Instance { get; private set; }

        [Tooltip("The authored atlas this session runs on.")]
        [SerializeField] private WorldAtlasDefinition atlas;

        [Tooltip("Load the persisted progress on start. Off means every play session begins at the starting map.")]
        [SerializeField] private bool loadSaveOnStart = true;

        [Tooltip("Write progress automatically after a meaningful change, debounced so a burst of changes " +
                 "becomes one file write.")]
        [SerializeField] private bool autoSave = true;

        [Tooltip("Seconds a change waits before it is written. Keeps a transition from writing twice.")]
        [SerializeField] private float autoSaveDelay = 1.5f;

        [Tooltip("Create the world map screen (persistent, hidden until opened).")]
        [SerializeField] private bool createWorldMapScreen = true;

        private readonly WorldProgress progress = new WorldProgress();
        private float dirtySince = -1f;
        private bool authoritative;
        private MapTransitionService transition;
        private WorldMapScreen worldMapScreen;

        public WorldAtlasDefinition Atlas => atlas;
        public WorldProgress Progress => progress;
        public bool AutoSaveEnabled => autoSave;

        /// <summary>What happened the last time a save was read. Reported by the UI, never hidden.</summary>
        public WorldSaveLoadResult LastLoadResult { get; private set; }

        public string CurrentMapId => progress.currentMapId;

        public MapDefinition CurrentMap => atlas != null ? atlas.GetMap(progress.currentMapId) : null;

        /// <summary>The transition service, created on demand on this persistent object.</summary>
        public MapTransitionService Transition
        {
            get
            {
                if (transition == null)
                    transition = GetComponent<MapTransitionService>() ?? gameObject.AddComponent<MapTransitionService>();

                return transition;
            }
        }

        /// <summary>The world map screen, created once and hidden until opened.</summary>
        public WorldMapScreen WorldMap
        {
            get
            {
                if (worldMapScreen == null && createWorldMapScreen)
                    worldMapScreen = WorldMapScreen.Create(transform, atlas);

                return worldMapScreen;
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                // A scene that ships its own runtime (so it can be played standalone) must not become a second
                // source of truth once another one already owns the session.
                Debug.Log($"[WorldAtlasRuntime] Duplicate on '{name}' destroyed; '{Instance.name}' owns the session.", this);
                Destroy(gameObject);
                return;
            }

            Instance = this;
            authoritative = true;
            DontDestroyOnLoad(gameObject);

            if (atlas == null)
            {
                Debug.LogError($"[{nameof(WorldAtlasRuntime)}] No atlas assigned; world travel is unavailable.", this);
                return;
            }

            LastLoadResult = loadSaveOnStart
                ? WorldSaveStore.Load(progress)
                : new WorldSaveLoadResult(WorldSaveLoadOutcome.FreshStart, "save loading disabled on this runtime");

            progress.Changed += HandleProgressChanged;
        }

        private void OnEnable()
        {
            if (!authoritative)
                return;

            SceneManager.sceneLoaded += HandleSceneLoaded;
        }

        private void OnDisable()
        {
            if (authoritative)
                SceneManager.sceneLoaded -= HandleSceneLoaded;
        }

        private void OnDestroy()
        {
            if (!authoritative)
                return;

            progress.Changed -= HandleProgressChanged;
            Instance = null;
            authoritative = false;
        }

        // ------------------------------------------------------------------ scene bookkeeping

        private void HandleSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Scene-local UI creates its own Event System and dies with the scene, so the shared one has to be
            // re-established after every load. Exactly one is ever created.
            EnsureEventSystem();

            MapIdentity identity = UnityEngine.Object.FindAnyObjectByType<MapIdentity>();
            if (identity != null)
                NotifySceneMapEntered(identity);
        }

        /// <summary>
        /// Records that a scene claims a map. An unregistered map id is reported rather than adopted: the atlas
        /// is the authority on which maps exist, so a mistyped id cannot invent a map.
        /// </summary>
        public void NotifySceneMapEntered(MapIdentity identity)
        {
            if (identity == null || string.IsNullOrEmpty(identity.MapId))
                return;

            if (atlas == null || atlas.GetMap(identity.MapId) == null)
            {
                Debug.LogError($"[WorldAtlasRuntime] Scene '{identity.gameObject.scene.name}' reports map id " +
                               $"'{identity.MapId}', which is not registered in the atlas. World state left unchanged.", identity);
                return;
            }

            if (string.Equals(progress.currentMapId, identity.MapId, StringComparison.Ordinal))
                return;

            progress.SetCurrentMap(identity.MapId, progress.lastSafeAnchorId);
        }

        /// <summary>Makes sure the session has exactly one Event System, reusing any existing one.</summary>
        public void EnsureEventSystem()
        {
            if (UnityEngine.Object.FindAnyObjectByType<UnityEngine.EventSystems.EventSystem>() != null)
                return;

            var go = new GameObject("DarkRealms World EventSystem",
                typeof(UnityEngine.EventSystems.EventSystem),
                typeof(UnityEngine.InputSystem.UI.InputSystemUIInputModule));
            go.transform.SetParent(transform, false);
            DontDestroyOnLoad(go);
        }

        // ------------------------------------------------------------------ progression helpers

        public void SetCurrentMap(string mapId, string anchorId)
        {
            progress.SetCurrentMap(mapId, anchorId);
        }

        /// <summary>Whether a connection may be used right now, and why not when it may not.</summary>
        public bool IsConnectionAvailable(MapConnectionDefinition connection, out string reason)
        {
            reason = string.Empty;
            if (connection == null)
            {
                reason = "unknown connection";
                return false;
            }

            if (connection.condition == null || connection.condition.IsSatisfied(progress))
                return true;

            reason = connection.condition.Describe();
            return false;
        }

        /// <summary>
        /// Map completion: completed eligible objectives over all authored eligible objectives. A map with no
        /// authored objectives is "Unknown / N/A", not 100%.
        /// </summary>
        public MapCompletion CompletionOf(string mapId)
        {
            MapDefinition map = atlas != null ? atlas.GetMap(mapId) : null;
            if (map == null)
                return new MapCompletion(0, 0, false);

            List<MapPoiDefinition> objectives = atlas.ObjectivesOf(mapId);
            if (objectives.Count == 0)
                return new MapCompletion(0, 0, false);

            int completed = 0;
            for (int i = 0; i < objectives.Count; i++)
                if (progress.PoiState(objectives[i].id) == DiscoveryState.Completed)
                    completed++;

            return new MapCompletion(completed, objectives.Count, true);
        }

        /// <summary>Opened / discovered chest counts, kept separate from the authored totals.</summary>
        public void ChestProgressOf(string mapId, out int opened, out int discovered)
        {
            opened = 0;
            discovered = 0;

            if (atlas == null)
                return;

            IReadOnlyList<MapPoiDefinition> list = atlas.PoisOf(mapId);
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].IsChest)
                    continue;

                DiscoveryState state = progress.PoiState(list[i].id);
                if (state == DiscoveryState.Completed)
                    opened++;
                else if (state == DiscoveryState.Discovered)
                    discovered++;
            }
        }

        // ------------------------------------------------------------------ saving

        private void HandleProgressChanged()
        {
            if (autoSave)
                MarkDirty();
        }

        /// <summary>Requests a debounced save. Uses unscaled time so a paused game still saves.</summary>
        public void MarkDirty()
        {
            if (dirtySince < 0f)
                dirtySince = Time.realtimeSinceStartup;
        }

        public bool HasPendingSave => dirtySince >= 0f;

        /// <summary>Writes immediately, bypassing the debounce. Used before a map transition.</summary>
        public bool SaveNow()
        {
            dirtySince = -1f;
            return WorldSaveStore.Save(progress);
        }

        private void Update()
        {
            if (dirtySince < 0f)
                return;

            if (Time.realtimeSinceStartup - dirtySince < autoSaveDelay)
                return;

            dirtySince = -1f;
            WorldSaveStore.Save(progress);
        }

        /// <summary>Builds the evaluation context the query system and the route planner share.</summary>
        public AtlasQueryContext CreateQueryContext() => new AtlasQueryContext(atlas, progress, CurrentMapId);

#if UNITY_EDITOR
        /// <summary>Authoring/verification entry point: points this session at an atlas without a scene.</summary>
        public void EditorAssignAtlas(WorldAtlasDefinition definition) => atlas = definition;
#endif
    }
}

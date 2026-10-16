using System;
using UnityEngine;

namespace DarkRealms.World
{
    // ======================================================================================
    //  Static atlas data.
    //
    //  Everything in this file is AUTHORED content: it describes the world, never the
    //  player's progress through it. Nothing here may be written to at runtime - player
    //  state lives in WorldProgress / WorldSaveData, so a build can never dirty an asset.
    //
    //  The enums below enumerate the layers the current game actually uses. They are the
    //  convenience vocabulary, not the limit of the model: every definition also carries
    //  free-form tags and aliases, which is how a new biome, marker type or travel mode is
    //  added without touching search, serialization or the map renderer.
    // ======================================================================================

    /// <summary>Which facet of the world a tag belongs to. Tags are cross-cutting: a map may carry
    /// a biome tag, a nation tag and a feature tag at the same time.</summary>
    public enum AtlasTagCategory
    {
        Biome,
        Geography,
        Travel,
        Exploration,
        Combat,
        Narrative,
        Social,
        Npc,
        Feature
    }

    /// <summary>Position in the geographic hierarchy. <see cref="Subregion"/> is the named lowest
    /// static level (a plain, a pass, a forest); playable maps hang off it.</summary>
    public enum LocationKind
    {
        World,
        Continent,
        Landmass,
        Nation,
        Region,
        Subregion,
        Biome,
        Settlement,
        Dungeon,
        Interior,
        BossArena,
        Instanced
    }

    /// <summary>How a playable map behaves when it is loaded.</summary>
    public enum MapType
    {
        Overworld,
        Settlement,
        Dungeon,
        Interior,
        BossArena,
        Instanced
    }

    /// <summary>An anchor is a named point on a map boundary that a connection attaches to. It is not
    /// enough for two maps to be adjacent: travel happens between two anchors.</summary>
    public enum MapAnchorKind
    {
        Entry,
        Exit,
        Both
    }

    /// <summary>How a connection or road segment is traversed. Drives cost, unlock rules and the
    /// icon/colour used by the atlas.</summary>
    public enum TravelMode
    {
        Footpath,
        Road,
        Bridge,
        MountainPass,
        Tunnel,
        Gate,
        Boat,
        Ferry,
        Teleport
    }

    /// <summary>Directionality of an inter-map connection. A one-way river current is modelled with
    /// <see cref="FromToOnly"/> rather than by hiding the reverse direction in the UI.</summary>
    public enum ConnectionDirection
    {
        Bidirectional,
        FromToOnly
    }

    /// <summary>Typed point-of-interest kinds. Anything the game does not yet have data for simply
    /// has no marker; new kinds can also be expressed with tags instead of editing this enum.</summary>
    public enum PoiKind
    {
        // Geography and travel
        Town,
        Dungeon,
        Cave,
        Route,
        Bridge,
        Pass,
        Port,
        Island,
        Sea,
        MapExit,
        LockedGate,
        FastTravel,

        // Exploration
        ChestCommon,
        ChestPurple,
        ChestSpecial,
        Collectible,
        Secret,
        Lore,
        Landmark,
        Viewpoint,

        // Combat
        EnemySpecies,
        EncounterGroup,
        Boss,
        DangerZone,

        // Progression / services
        Guild,
        Altar,
        Shop,
        Inn,
        CraftingService,
        TrainingService,

        // Story
        MainStory,
        SideStory,
        CrossedPath,
        QuestStart,
        QuestDestination,

        // NPC
        NpcLocation
    }

    /// <summary>Chest tier, so counts can be grouped without switching on <see cref="PoiKind"/>.</summary>
    public enum ChestTier
    {
        None,
        Common,
        Purple,
        Special
    }

    /// <summary>What the player knows about a marker. This is progress, not authored data.</summary>
    public enum DiscoveryState
    {
        Undiscovered,
        Discovered,
        Completed
    }

    /// <summary>Under which policy a NPC route may be drawn at all.</summary>
    public enum NpcRouteVisibility
    {
        /// <summary>Never drawn, whatever the data says.</summary>
        Hidden,

        /// <summary>Drawn only once the player's progress marks the route as known.</summary>
        HiddenUntilKnown,

        /// <summary>Drawn whenever usable route data exists.</summary>
        ShownWhenDataExists
    }

    // ======================================================================================
    //  Availability conditions
    // ======================================================================================

    public enum AtlasConditionKind
    {
        /// <summary>Always available; the default for authored prototype content.</summary>
        Always,

        /// <summary>Available once the named progress flag has been set (a key, an unlocked gate...).</summary>
        FlagSet,

        /// <summary>Available once a story marker has been completed.</summary>
        StoryMarkerCompleted,

        /// <summary>Available only at night (mirrors the project's existing day/night switch).</summary>
        NightOnly,

        /// <summary>Available only during the day.</summary>
        DayOnly
    }

    /// <summary>
    /// A single, serializable availability rule. Kept deliberately small: one rule, one reason, so the
    /// validator can name exactly what a gate needs and the route report can say why a path is blocked.
    /// </summary>
    [Serializable]
    public sealed class AtlasCondition
    {
        public AtlasConditionKind kind = AtlasConditionKind.Always;

        [Tooltip("Flag id for FlagSet, or story marker id for StoryMarkerCompleted. Ignored otherwise.")]
        public string id = string.Empty;

        [Tooltip("Author-facing reason shown when a route is blocked. Never invented at runtime.")]
        public string description = string.Empty;

        public bool IsAlwaysAvailable => kind == AtlasConditionKind.Always;

        /// <summary>Evaluates the rule against player progress. Day/night uses an explicit override so a
        /// verifier can exercise both branches without waiting for a real clock.</summary>
        public bool IsSatisfied(WorldProgress progress, bool? isNightOverride = null)
        {
            switch (kind)
            {
                case AtlasConditionKind.Always:
                    return true;
                case AtlasConditionKind.FlagSet:
                    return progress != null && progress.HasFlag(id);
                case AtlasConditionKind.StoryMarkerCompleted:
                    return progress != null && progress.IsStoryMarkerCompleted(id);
                case AtlasConditionKind.NightOnly:
                    return isNightOverride ?? (progress != null && progress.IsNight);
                case AtlasConditionKind.DayOnly:
                    return !(isNightOverride ?? (progress != null && progress.IsNight));
                default:
                    return false;
            }
        }

        public string Describe()
        {
            if (kind == AtlasConditionKind.Always)
                return "always available";

            return string.IsNullOrEmpty(description) ? $"{kind}:{id}" : description;
        }
    }

    // ======================================================================================
    //  Tags, locations
    // ======================================================================================

    /// <summary>A named, colorable label that can be searched and filtered across the whole atlas.</summary>
    [Serializable]
    public sealed class AtlasTagDefinition
    {
        [Tooltip("Stable id used by data and saved progress. Never rename without a migration.")]
        public string id = "tag.new";
        public string displayName = "New Tag";
        public AtlasTagCategory category = AtlasTagCategory.Feature;
        public Color color = new Color(0.62f, 0.68f, 0.78f, 1f);
    }

    /// <summary>
    /// One node of the static world hierarchy (world -> continent -> nation -> region -> subregion),
    /// or a directly referenced place. Biomes are cross-cutting tags rather than a hierarchy level, so a
    /// temperate forest can belong to a nation without being a child of "forest" as a place.
    /// </summary>
    [Serializable]
    public sealed class AtlasLocationDefinition
    {
        public string id = "loc.new";
        public string displayName = "New Location";
        public LocationKind kind = LocationKind.Region;

        [Tooltip("Parent location id. Empty for a root (the world). Cycles are a validation error.")]
        public string parentId = string.Empty;

        [Tooltip("Cross-cutting tags: biome, nation, features. Free-form ids declared in the atlas tag catalog.")]
        public string[] tags = Array.Empty<string>();

        [Tooltip("Alternative names that search must match, e.g. an old name or a translation.")]
        public string[] aliases = Array.Empty<string>();

        public bool HasTag(string tagId)
        {
            if (string.IsNullOrEmpty(tagId) || tags == null)
                return false;

            for (int i = 0; i < tags.Length; i++)
                if (string.Equals(tags[i], tagId, StringComparison.Ordinal))
                    return true;

            return false;
        }
    }

    // ======================================================================================
    //  Playable maps, anchors and the local road graph
    // ======================================================================================

    /// <summary>An interior-facing representation of a map, authored separately from the level layout.
    /// Once authored, the atlas layout is a decision of the world map; the local overview can be drawn
    /// from the road graph so the two never disagree.</summary>
    [Serializable]
    public sealed class MapOverviewDefinition
    {
        [Tooltip("Raw top-down silhouette for the local map view, in atlas space. Replaceable placeholder.")]
        public Sprite graphic;

        [Tooltip("Local map view extents in atlas space (xMin, yMin, width, height). The road graph is drawn inside it.")]
        public Rect overviewRect = new Rect(0f, 0f, 1f, 1f);
    }

    /// <summary>A boundary point a connection attaches to. Anchors are the only legal attach points,
    /// which is what stops a connection from being inferred from two nodes looking close on screen.</summary>
    [Serializable]
    public sealed class MapAnchorDefinition
    {
        public string id = "anchor.new";
        public string displayName = "New Anchor";
        public MapAnchorKind kind = MapAnchorKind.Both;

        [Tooltip("Road graph node the anchor sits on. Cross-map travel joins the local road graph here. " +
                 "Empty is a validation error for any anchor a connection uses.")]
        public string roadNodeId = string.Empty;

        [Tooltip("Where the player stands inside the level when arriving here, in level world coordinates.")]
        public Vector2 position;

        [Tooltip("Camera/level bounds are authored per map; this only positions the actor.")]
        public float facingX = 1f;
    }

    /// <summary>A junction or waypoint of a map's authored walkable route graph.</summary>
    [Serializable]
    public sealed class MapRoadNodeDefinition
    {
        public string id = "node.new";

        [Tooltip("Position in the level's own world coordinates, so route geometry matches real level geometry.")]
        public Vector2 position;

        [Tooltip("Marker for a junction the route planner must respect, e.g. the mountain pass itself.")]
        public bool isLandmark;
    }

    /// <summary>
    /// One authored piece of walkable route. <see cref="polyline"/> is the real shape: a route must follow the
    /// bridge deck, the pass or the corridor, never a straight chord between two nodes. An empty polyline
    /// means the segment has no authored geometry, which the validator warns about and the route display
    /// labels as schematic instead of pretending it is a walkable path.
    /// </summary>
    [Serializable]
    public sealed class MapRoadSegmentDefinition
    {
        public string id = "seg.new";
        public string fromNodeId = string.Empty;
        public string toNodeId = string.Empty;
        public bool bidirectional = true;
        public TravelMode mode = TravelMode.Footpath;

        [Tooltip("Travel cost in level units. Negative means 'derive from the authored geometry'.")]
        public float distance = -1f;

        [Tooltip("Risk cost used by the Safest objective. 0 means no authored risk.")]
        public float riskCost;

        [Tooltip("Seconds to traverse, used by the Fastest objective. Negative means unknown, which makes " +
                 "the Fastest objective unavailable for any route crossing this segment.")]
        public float travelTime = -1f;

        [Tooltip("Availability rule. A closed bridge is this rule being unsatisfied.")]
        public AtlasCondition condition = new AtlasCondition();

        [Tooltip("Authored geometry, including the two end points. Empty = no authored geometry.")]
        public Vector2[] polyline = Array.Empty<Vector2>();

        public bool HasAuthoredGeometry => polyline != null && polyline.Length >= 2;
    }

    /// <summary>A forbidden area: a mountain range, a river, a wall. Route geometry may not cross it.</summary>
    [Serializable]
    public sealed class AtlasBlockerDefinition
    {
        public string id = "blocker.new";
        public string label = "New Blocker";
        public string mapId = string.Empty;

        [Tooltip("Closed polygon in the level's own world coordinates. Minimum three points.")]
        public Vector2[] points = Array.Empty<Vector2>();

        public bool IsWellFormed => points != null && points.Length >= 3;
    }

    /// <summary>
    /// The authored description of a playable map. Scene reference is a path (not a SceneAsset) so the same
    /// definition works in a build, where an editor-only asset reference would not exist.
    /// </summary>
    [Serializable]
    public sealed class MapDefinition
    {
        [Header("Identity")]
        public string id = "map.new";
        public string displayName = "New Map";

        [Tooltip("Alternative names search must match.")]
        public string[] aliases = Array.Empty<string>();

        [Tooltip("Static location this map belongs to (nation / region / subregion).")]
        public string locationId = string.Empty;

        [Tooltip("Stable id of the scene component that must report this map when the scene is loaded.")]
        public string sceneMapId = string.Empty;

        public MapType mapType = MapType.Overworld;

        [Header("Loading")]
        [Tooltip("Project-relative scene path, e.g. Assets/Game/Scenes/Proto_Grassland.unity. Must be in Build Settings.")]
        public string scenePath = string.Empty;

        [Header("Atlas layout (world map)")]
        [Tooltip("Where this map's card sits in the atlas. This is a drawing decision and never implies connectivity.")]
        public Vector2 atlasPosition = new Vector2(0.5f, 0.5f);

        [Tooltip("Optional cover image. A missing one draws a generated placeholder, so art can be added later " +
                 "without any code change.")]
        public Sprite thumbnail;

        [Header("Local map view")]
        [Tooltip("Level extents in level world coordinates. Used to map player position and road nodes into " +
                 "the local map view - never to guess a position from the atlas card.")]
        public Rect localBounds = new Rect(-20f, -8f, 40f, 24f);

        public MapOverviewDefinition overview = new MapOverviewDefinition();

        [Header("Metadata")]
        [Tooltip("Tags: biome, terrain, features. Free-form ids declared in the atlas tag catalog.")]
        public string[] tags = Array.Empty<string>();

        [Tooltip("Level world units per atlas unit, used to project level geometry into the atlas layout. " +
                 "0 means 'not declared', which disables the distance heuristic and keeps positions on the map card.")]
        public float atlasScale;

        [Tooltip("Danger rating range. 0/0 means 'not recorded' and must stay unknown in the UI.")]
        public int dangerMin;
        public int dangerMax;

        [Tooltip("Recommended character level. 0 means 'not recorded'.")]
        public int recommendedLevel;

        [Tooltip("Anchor used when a map is entered without a specific arrival port. Empty falls back to the " +
                 "first Exit/Entry anchor.")]
        public string defaultEntryAnchorId = string.Empty;

        [Header("Graph")]
        public MapAnchorDefinition[] anchors = Array.Empty<MapAnchorDefinition>();
        public MapRoadNodeDefinition[] roadNodes = Array.Empty<MapRoadNodeDefinition>();
        public MapRoadSegmentDefinition[] roadSegments = Array.Empty<MapRoadSegmentDefinition>();

        public bool HasDanger => dangerMin > 0 || dangerMax > 0;

        public bool HasTag(string tagId)
        {
            if (string.IsNullOrEmpty(tagId) || tags == null)
                return false;

            for (int i = 0; i < tags.Length; i++)
                if (string.Equals(tags[i], tagId, StringComparison.Ordinal))
                    return true;

            return false;
        }

        public MapAnchorDefinition FindAnchor(string anchorId)
        {
            if (string.IsNullOrEmpty(anchorId) || anchors == null)
                return null;

            for (int i = 0; i < anchors.Length; i++)
                if (anchors[i] != null && string.Equals(anchors[i].id, anchorId, StringComparison.Ordinal))
                    return anchors[i];

            return null;
        }

        public MapRoadNodeDefinition FindRoadNode(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId) || roadNodes == null)
                return null;

            for (int i = 0; i < roadNodes.Length; i++)
                if (roadNodes[i] != null && string.Equals(roadNodes[i].id, nodeId, StringComparison.Ordinal))
                    return roadNodes[i];

            return null;
        }

        public MapRoadSegmentDefinition FindRoadSegment(string segmentId)
        {
            if (string.IsNullOrEmpty(segmentId) || roadSegments == null)
                return null;

            for (int i = 0; i < roadSegments.Length; i++)
                if (roadSegments[i] != null && string.Equals(roadSegments[i].id, segmentId, StringComparison.Ordinal))
                    return roadSegments[i];

            return null;
        }

        /// <summary>Anchor used when arriving without an explicit port. Never invents one.</summary>
        public MapAnchorDefinition ResolveDefaultEntryAnchor()
        {
            MapAnchorDefinition named = FindAnchor(defaultEntryAnchorId);
            if (named != null)
                return named;

            if (anchors != null)
            {
                for (int i = 0; i < anchors.Length; i++)
                {
                    MapAnchorDefinition anchor = anchors[i];
                    if (anchor == null)
                        continue;

                    if (anchor.kind == MapAnchorKind.Entry || anchor.kind == MapAnchorKind.Both)
                        return anchor;
                }
            }

            return null;
        }
    }

    /// <summary>An explicit, directed or two-way link between two maps, attached at named anchors.
    /// Existing here is what makes travel possible - being drawn near each other is not.</summary>
    [Serializable]
    public sealed class MapConnectionDefinition
    {
        public string id = "conn.new";
        public string label = "New Connection";

        public string fromMapId = string.Empty;
        public string fromAnchorId = string.Empty;
        public string toMapId = string.Empty;
        public string toAnchorId = string.Empty;

        public ConnectionDirection direction = ConnectionDirection.Bidirectional;
        public TravelMode travelMode = TravelMode.Footpath;

        [Tooltip("Cost used by the Shortest Distance objective.")]
        public float baseDistance = 1f;

        [Tooltip("Risk cost used by the Safest objective.")]
        public float riskCost;

        [Tooltip("Seconds, used by the Fastest objective. Negative means unknown, which makes Fastest " +
                 "unavailable for any route using this connection.")]
        public float travelTime = -1f;

        [Tooltip("Availability rule: a locked gate, a closed pass or a boat that only runs later.")]
        public AtlasCondition condition = new AtlasCondition();

        [Tooltip("Drawn geometry for the connector, in atlas space. Empty draws a thin schematic link that is " +
                 "labelled as such, never as a walkable road.")]
        public Vector2[] atlasPolyline = Array.Empty<Vector2>();

        public bool IsTraversableFrom(string mapId)
        {
            if (string.Equals(fromMapId, mapId, StringComparison.Ordinal))
                return true;

            return direction == ConnectionDirection.Bidirectional &&
                   string.Equals(toMapId, mapId, StringComparison.Ordinal);
        }

        /// <summary>Resolves the other end of the connection when travelling from <paramref name="mapId"/>.
        /// Returns false for a one-way link being used backwards, which is a hard travel failure.</summary>
        public bool TryResolveDestination(string mapId, out string destinationMapId, out string destinationAnchorId)
        {
            if (string.Equals(fromMapId, mapId, StringComparison.Ordinal))
            {
                destinationMapId = toMapId;
                destinationAnchorId = toAnchorId;
                return true;
            }

            if (direction == ConnectionDirection.Bidirectional &&
                string.Equals(toMapId, mapId, StringComparison.Ordinal))
            {
                destinationMapId = fromMapId;
                destinationAnchorId = fromAnchorId;
                return true;
            }

            destinationMapId = null;
            destinationAnchorId = null;
            return false;
        }

        public bool HasAuthoredGeometry => atlasPolyline != null && atlasPolyline.Length >= 2;
    }

    // ======================================================================================
    //  Markers
    // ======================================================================================

    /// <summary>
    /// A typed marker. Chest totals are always derived from these records - no separate count field exists
    /// that could drift away from them.
    /// </summary>
    [Serializable]
    public sealed class MapPoiDefinition
    {
        public string id = "poi.new";
        public string mapId = string.Empty;
        public PoiKind kind = PoiKind.Landmark;
        public string displayName = "New Marker";

        [Tooltip("Alternative names search must match.")]
        public string[] aliases = Array.Empty<string>();

        [Tooltip("Free-form tags, so a new marker category does not require an enum change.")]
        public string[] tags = Array.Empty<string>();

        [Tooltip("Position in the level's own world coordinates for the local map view.")]
        public Vector2 position;

        [Tooltip("Danger rating for this marker (bosses, encounter groups). 0 means not recorded.")]
        public int dangerLevel;

        [Tooltip("Recommended level for this marker. 0 means not recorded.")]
        public int recommendedLevel;

        [Tooltip("Enemy species id for EnemySpecies markers. Empty means not recorded.")]
        public string enemySpecies = string.Empty;

        [Tooltip("Story chapter / act id for story markers. Empty means not recorded.")]
        public string storyChapter = string.Empty;

        [Tooltip("NPC / Path Action id for NPC markers. Empty means not recorded.")]
        public string pathAction = string.Empty;

        [Tooltip("Route this NPC marker is associated with. The route is only drawn when route data exists.")]
        public string npcRouteId = string.Empty;

        [Tooltip("Availability rule. An unsatisfied rule means the marker exists in data but is not present now.")]
        public AtlasCondition condition = new AtlasCondition();

        [Tooltip("Counts toward map completion. Only authored markers count, so the denominator can never " +
                 "be filled up from nowhere.")]
        public bool countsAsObjective;

        public ChestTier Tier
        {
            get
            {
                switch (kind)
                {
                    case PoiKind.ChestCommon: return ChestTier.Common;
                    case PoiKind.ChestPurple: return ChestTier.Purple;
                    case PoiKind.ChestSpecial: return ChestTier.Special;
                    default: return ChestTier.None;
                }
            }
        }

        public bool IsChest => Tier != ChestTier.None;

        public bool HasTag(string tagId)
        {
            if (string.IsNullOrEmpty(tagId) || tags == null)
                return false;

            for (int i = 0; i < tags.Length; i++)
                if (string.Equals(tags[i], tagId, StringComparison.Ordinal))
                    return true;

            return false;
        }
    }

    /// <summary>
    /// An authored NPC travel route. The line is drawn only when this data exists, with at least two
    /// waypoints, and the visibility policy allows it. A missing route is "no data", never "a route of
    /// length zero".
    /// </summary>
    [Serializable]
    public sealed class NpcRouteDefinition
    {
        public string id = "npcroute.new";
        public string npcId = string.Empty;
        public string displayName = "New NPC Route";
        public string mapId = string.Empty;

        [Tooltip("Authored waypoints in level world coordinates. Fewer than two means no usable route.")]
        public Vector2[] waypoints = Array.Empty<Vector2>();

        [Tooltip("Time-of-day window. Always = the route is not time gated.")]
        public AtlasCondition condition = new AtlasCondition();

        public NpcRouteVisibility visibility = NpcRouteVisibility.HiddenUntilKnown;

        [Tooltip("Route is known to the player before any discovery progress.")]
        public bool knownByDefault;

        public bool HasUsableRoute => waypoints != null && waypoints.Length >= 2;
    }

    // ======================================================================================
    //  Route planning configuration
    // ======================================================================================

    public enum RouteObjective
    {
        ShortestDistance,
        Safest,
        Fastest,
        Custom
    }

    /// <summary>
    /// Documented weights for the Custom objective and the geometry rules used by every route. Defaults are the
    /// ones the prototype content is validated against.
    /// </summary>
    [Serializable]
    public sealed class AtlasRouteSettings
    {
        [Tooltip("Custom objective: weight applied to distance cost.")]
        public float distanceWeight = 1f;

        [Tooltip("Custom objective: weight applied to authored risk cost.")]
        public float riskWeight = 1f;

        [Tooltip("Custom objective: weight applied to travel time.")]
        public float timeWeight = 1f;

        [Tooltip("A route may not use a segment or connection whose authored geometry crosses a blocker.")]
        public bool rejectGeometryThroughBlockers = true;

        [Tooltip("Segments without authored geometry are usable but reported as schematic.")]
        public bool allowSchematicSegments = true;

        [Tooltip("Chaikin-like smoothing passes applied to the displayed route, each pass re-checked against blockers.")]
        [Range(0, 4)] public int smoothingPasses = 1;

        [Tooltip("Minimum clearance from blocker polygons required for a smoothed point to be accepted.")]
        public float smoothingClearance = 0.35f;
    }

    // ======================================================================================
    //  Derived summaries (always computed, never stored)
    // ======================================================================================

    /// <summary>Chest counts derived from authored markers. Total is by construction the sum of the tiers,
    /// so it can never disagree with them.</summary>
    public readonly struct ChestSummary
    {
        public readonly int Common;
        public readonly int Purple;
        public readonly int Special;

        public ChestSummary(int common, int purple, int special)
        {
            Common = common;
            Purple = purple;
            Special = special;
        }

        public int Total => Common + Purple + Special;

        public bool IsEmpty => Total == 0;

        public override string ToString() =>
            IsEmpty ? "no chests recorded" : $"{Total} total ({Common} common, {Purple} purple, {Special} special)";
    }

    /// <summary>A danger rating range. Both ends zero means the data is not recorded and must stay unknown.</summary>
    public readonly struct DangerRange
    {
        public readonly int Min;
        public readonly int Max;

        public DangerRange(int min, int max)
        {
            Min = min;
            Max = max;
        }

        public bool IsKnown => Min > 0 || Max > 0;

        public override string ToString() => IsKnown
            ? (Min == Max ? $"danger {Min}" : $"danger {Min}-{Max}")
            : "danger unknown";
    }
}

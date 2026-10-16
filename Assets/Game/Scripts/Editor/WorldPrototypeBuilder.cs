#if UNITY_EDITOR
using System.Collections.Generic;
using DarkRealms.CameraSystem;
using DarkRealms.Core;
using DarkRealms.Enemies;
using DarkRealms.Player;
using DarkRealms.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DarkRealms.EditorTools
{
    /// <summary>
    /// Authors the prototype world: one atlas asset plus three playable prototype maps, each with its own road
    /// graph, blockers and markers.
    ///
    /// The content is generated from code rather than hand-placed so it can be rebuilt and re-validated
    /// deterministically, and so the data stays readable as a description of the world instead of being spread
    /// over a scene file. Everything here is placeholder content on purpose - no final art is referenced, and a
    /// map thumbnail that is missing simply draws a placeholder.
    /// </summary>
    public static class WorldPrototypeBuilder
    {
        public const string WorldFolder = "Assets/Game/World";
        public const string AtlasPath = WorldFolder + "/WorldAtlas_Prototype.asset";
        private const string SceneFolder = "Assets/Game/Scenes";

        // -------- prototype ids (stable: saved progress refers to them by name)
        public const string HubMapId = "map.proto.hub";
        public const string GrasslandMapId = "map.proto.grassland";
        public const string PassMapId = "map.proto.mountain_pass";
        public const string ForestMapId = "map.proto.forest";

        public const string HubScenePath = SceneFolder + "/DarkRealms_Proto_01.unity";
        public const string GrasslandScenePath = SceneFolder + "/Proto_Grassland.unity";
        public const string PassScenePath = SceneFolder + "/Proto_MountainPass.unity";
        public const string ForestScenePath = SceneFolder + "/Proto_Forest.unity";

        public const string BridgeKeyFlag = "flag.pass_bridge_key";
        public const string PassBridgeConnectionId = "conn.pass_forest";

        // ------------------------------------------------------------------ menus

        [MenuItem("Dark Realms/World Map/Build Prototype World")]
        public static void BuildPrototypeWorld()
        {
            EnsureFolders();
            WorldAtlasDefinition atlas = BuildAtlasAsset();

            BuildScene(GrasslandSpec());
            BuildScene(PassSpec());
            BuildScene(ForestSpec());

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[WorldPrototypeBuilder] Prototype world built: atlas at {AtlasPath} and 3 map scenes. " +
                      "Run 'Dark Realms/World Map/Validate Atlas' next, then 'Install World Map Into Open Scene' " +
                      "for the hub map.");
        }

        [MenuItem("Dark Realms/World Map/Select Atlas Asset")]
        public static void SelectAtlas() => Selection.activeObject = AssetDatabase.LoadAssetAtPath<WorldAtlasDefinition>(AtlasPath);

        // ------------------------------------------------------------------ atlas asset

        public static WorldAtlasDefinition LoadAtlas() =>
            AssetDatabase.LoadAssetAtPath<WorldAtlasDefinition>(AtlasPath);

        internal static WorldAtlasDefinition GetOrCreateAtlas()
        {
            EnsureFolders();
            WorldAtlasDefinition atlas = LoadAtlas();
            if (atlas == null)
            {
                atlas = ScriptableObject.CreateInstance<WorldAtlasDefinition>();
                AssetDatabase.CreateAsset(atlas, AtlasPath);
            }

            return atlas;
        }

        public static WorldAtlasDefinition BuildAtlasAsset()
        {
            WorldAtlasDefinition atlas = GetOrCreateAtlas();

            atlas.atlasId = "atlas.proto";
            atlas.displayName = "Dark Realms - Prototype Atlas";
            atlas.startMapId = HubMapId;

            atlas.declaredFlagIds = new[] { BridgeKeyFlag, "flag.ferry_operational" };
            atlas.tags = BuildTags();
            atlas.locations = BuildLocations();
            atlas.maps = BuildMaps();
            atlas.connections = BuildConnections();
            atlas.pois = BuildPois();
            atlas.npcRoutes = BuildNpcRoutes();
            atlas.blockers = BuildBlockers();
            atlas.routeSettings = BuildRouteSettings();

            EditorUtility.SetDirty(atlas);
            atlas.RebuildIndex();
            return atlas;
        }

        private static AtlasRouteSettings BuildRouteSettings() => new AtlasRouteSettings
        {
            distanceWeight = 1f,
            riskWeight = 1f,
            timeWeight = 1f,
            rejectGeometryThroughBlockers = true,
            allowSchematicSegments = true,
            smoothingPasses = 1,
            smoothingClearance = 0.3f
        };

        // ------------------------------------------------------------------ tags and locations

        private static AtlasTagDefinition[] BuildTags() => new[]
        {
            Tag("biome.plains", "Plains", AtlasTagCategory.Biome),
            Tag("biome.highlands", "Highlands", AtlasTagCategory.Biome),
            Tag("biome.temperate_forest", "Temperate Forest", AtlasTagCategory.Biome),
            Tag("biome.ruins", "Ruins", AtlasTagCategory.Biome),
            Tag("terrain.plateau", "Plateau", AtlasTagCategory.Geography),
            Tag("nation.proto_freeholds", "Freeholds (prototype)", AtlasTagCategory.Geography),
            Tag("feature.bridge", "Bridge", AtlasTagCategory.Travel),
            Tag("feature.pass", "Mountain Pass", AtlasTagCategory.Travel),
            Tag("feature.ferry", "Ferry", AtlasTagCategory.Travel),
            Tag("feature.ruins", "Ruins", AtlasTagCategory.Exploration),
            Tag("danger.high", "High danger", AtlasTagCategory.Combat)
        };

        private static AtlasLocationDefinition[] BuildLocations() => new[]
        {
            Location("loc.world", "World", LocationKind.World, ""),
            Location("loc.continent", "Prototype Continent", LocationKind.Continent, "loc.world"),
            Location("loc.nation_freeholds", "Freeholds", LocationKind.Nation, "loc.continent", "nation.proto_freeholds"),
            Location("loc.region_north", "North Reach", LocationKind.Region, "loc.nation_freeholds"),
            Location("loc.region_south", "South Vale", LocationKind.Region, "loc.nation_freeholds"),
            Location("loc.sub_grassland", "Sunlit Grassland", LocationKind.Subregion, "loc.region_north", "biome.plains"),
            Location("loc.sub_pass", "Windward Pass", LocationKind.Subregion, "loc.region_north", "biome.highlands"),
            Location("loc.sub_forest", "Mistwood", LocationKind.Subregion, "loc.region_south", "biome.temperate_forest"),
            Location("loc.sub_hub", "Old Keep District", LocationKind.Subregion, "loc.region_south", "biome.ruins")
        };

        // ------------------------------------------------------------------ maps

        private static MapDefinition[] BuildMaps() => new[]
        {
            HubMap(),
            GrasslandMap(),
            PassMap(),
            ForestMap()
        };

        private static MapDefinition HubMap()
        {
            // Bounds match the camera limits of the existing prototype scene, so the player indicator and the
            // local map view land on real geometry rather than on a guessed rectangle.
            var bounds = new Rect(-10f, -6f, 72f, 32f);
            return new MapDefinition
            {
                id = HubMapId,
                sceneMapId = HubMapId,
                displayName = "Fallen Keep",
                aliases = new[] { "hub", "keep", "start" },
                locationId = "loc.sub_hub",
                mapType = MapType.Settlement,
                scenePath = HubScenePath,
                atlasPosition = new Vector2(0.22f, 0.30f),
                localBounds = bounds,
                atlasScale = 200f,
                tags = new[] { "biome.ruins", "feature.ruins" },
                dangerMin = 1,
                dangerMax = 2,
                recommendedLevel = 1,
                defaultEntryAnchorId = "port.hub.hub",
                anchors = new[]
                {
                    Anchor("port.hub.hub", "Keep Gate", MapAnchorKind.Both, "node.hub.centre", new Vector2(-4f, 0.6f)),
                    Anchor("port.hub.west", "West Road", MapAnchorKind.Exit, "node.hub.west", new Vector2(-9f, 0.6f)),
                    Anchor("port.hub.east", "Rampart Walk", MapAnchorKind.Exit, "node.hub.east", new Vector2(52f, 3.6f))
                },
                roadNodes = new[]
                {
                    Node("node.hub.west", -9f, 0.5f),
                    Node("node.hub.centre", -4f, 0.5f),
                    Node("node.hub.east", 52f, 3.5f)
                },
                roadSegments = new[]
                {
                    Seg("seg.hub.west_centre", "node.hub.west", "node.hub.centre", TravelMode.Road, -1f, 0f, 6f,
                        new[] { new Vector2(-9f, 0.5f), new Vector2(-6f, 0.5f), new Vector2(-4f, 0.5f) }),
                    Seg("seg.hub.centre_east", "node.hub.centre", "node.hub.east", TravelMode.Road, -1f, 0f, 55f,
                        new[] { new Vector2(-4f, 0.5f), new Vector2(18f, 0.5f), new Vector2(34f, 1.5f), new Vector2(52f, 3.5f) })
                }
            };
        }

        private static MapDefinition GrasslandMap()
        {
            var bounds = new Rect(-30f, -10f, 60f, 26f);
            return new MapDefinition
            {
                id = GrasslandMapId,
                sceneMapId = GrasslandMapId,
                displayName = "Proto Grassland",
                aliases = new[] { "grassland", "plains" },
                locationId = "loc.sub_grassland",
                mapType = MapType.Overworld,
                scenePath = GrasslandScenePath,
                atlasPosition = new Vector2(0.42f, 0.46f),
                localBounds = bounds,
                atlasScale = 200f,
                tags = new[] { "biome.plains", "biome.temperate_forest" },
                dangerMin = 1,
                dangerMax = 3,
                recommendedLevel = 2,
                defaultEntryAnchorId = "port.grassland.west",
                anchors = new[]
                {
                    Anchor("port.grassland.west", "West Gate", MapAnchorKind.Both, "node.grass.west", new Vector2(-26f, 0.6f)),
                    Anchor("port.grassland.east", "East Road", MapAnchorKind.Both, "node.grass.east", new Vector2(27f, 0.6f)),
                    Anchor("port.grassland.ferry", "River Ferry", MapAnchorKind.Both, "node.grass.ferry", new Vector2(24f, 0.6f))
                },
                roadNodes = new[]
                {
                    Node("node.grass.west", -26f, 0.5f),
                    Node("node.grass.bridge_west", -6f, 0.5f),
                    Node("node.grass.bridge_east", 6f, 0.5f),
                    Node("node.grass.centre", 15f, 0.5f),
                    Node("node.grass.ferry", 24f, 0.5f),
                    Node("node.grass.east", 27f, 0.5f)
                },
                roadSegments = new[]
                {
                    Seg("seg.grass.west_bridge", "node.grass.west", "node.grass.bridge_west", TravelMode.Road, -1f, 1f, 70f,
                        new[] { new Vector2(-26f, 0.5f), new Vector2(-16f, 0.5f), new Vector2(-6f, 0.5f) }),
                    // The bridge is the only crossing: its authored geometry runs over the gorge, and the gorge is
                    // a blocker, so a straight chord between the two banks is not a walkable route.
                    Seg("seg.grass.bridge", "node.grass.bridge_west", "node.grass.bridge_east", TravelMode.Bridge, -1f, 2f, 12f,
                        new[] { new Vector2(-6f, 0.5f), new Vector2(-1.2f, 0.9f), new Vector2(1.2f, 0.9f), new Vector2(6f, 0.5f) }),
                    Seg("seg.grass.bridge_centre", "node.grass.bridge_east", "node.grass.centre", TravelMode.Road, -1f, 1f, 35f,
                        new[] { new Vector2(6f, 0.5f), new Vector2(10f, 0.5f), new Vector2(15f, 0.5f) }),
                    Seg("seg.grass.centre_ferry", "node.grass.centre", "node.grass.ferry", TravelMode.Road, -1f, 1f, 30f,
                        new[] { new Vector2(15f, 0.5f), new Vector2(20f, 0.5f), new Vector2(24f, 0.5f) }),
                    Seg("seg.grass.ferry_east", "node.grass.ferry", "node.grass.east", TravelMode.Footpath, -1f, 1f, 10f,
                        new[] { new Vector2(24f, 0.5f), new Vector2(27f, 0.5f) })
                }
            };
        }

        private static MapDefinition PassMap()
        {
            var bounds = new Rect(-25f, -14f, 50f, 30f);
            return new MapDefinition
            {
                id = PassMapId,
                sceneMapId = PassMapId,
                displayName = "Proto Mountain Pass",
                aliases = new[] { "pass", "mountain pass", "windward" },
                locationId = "loc.sub_pass",
                mapType = MapType.Overworld,
                scenePath = PassScenePath,
                atlasPosition = new Vector2(0.60f, 0.62f),
                localBounds = bounds,
                atlasScale = 200f,
                tags = new[] { "biome.highlands", "terrain.plateau", "feature.pass", "danger.high" },
                dangerMin = 3,
                dangerMax = 5,
                recommendedLevel = 4,
                defaultEntryAnchorId = "port.pass.west",
                anchors = new[]
                {
                    Anchor("port.pass.west", "Lowland Track", MapAnchorKind.Both, "node.pass.west", new Vector2(-22f, 0.6f)),
                    Anchor("port.pass.east", "Downhill Track", MapAnchorKind.Both, "node.pass.east", new Vector2(22f, 0.6f))
                },
                roadNodes = new[]
                {
                    Node("node.pass.west", -22f, 0.5f),
                    Node("node.pass.ridge_west", -5f, -6f),
                    Node("node.pass.gap", 0f, -7f, true),
                    Node("node.pass.ridge_east", 5f, -6f),
                    Node("node.pass.east", 22f, 0.5f)
                },
                roadSegments = new[]
                {
                    // The ridge blocks the direct line, so the authored chain through the pass gap is the only
                    // valid geometry - which is exactly what the route planner has to follow.
                    Seg("seg.pass.west_ridge", "node.pass.west", "node.pass.ridge_west", TravelMode.MountainPass, -1f, 3f, 80f,
                        new[] { new Vector2(-22f, 0.5f), new Vector2(-14f, 0.0f), new Vector2(-9f, -3.5f), new Vector2(-5f, -6f) }),
                    Seg("seg.pass.ridge_gap", "node.pass.ridge_west", "node.pass.gap", TravelMode.MountainPass, -1f, 2f, 25f,
                        new[] { new Vector2(-5f, -6f), new Vector2(-2f, -6.8f), new Vector2(0f, -7f) }),
                    Seg("seg.pass.gap_ridge", "node.pass.gap", "node.pass.ridge_east", TravelMode.MountainPass, -1f, 2f, 25f,
                        new[] { new Vector2(0f, -7f), new Vector2(2f, -6.8f), new Vector2(5f, -6f) }),
                    Seg("seg.pass.ridge_east_exit", "node.pass.ridge_east", "node.pass.east", TravelMode.MountainPass, -1f, 3f, 80f,
                        new[] { new Vector2(5f, -6f), new Vector2(9f, -3.5f), new Vector2(14f, 0.0f), new Vector2(22f, 0.5f) })
                }
            };
        }

        private static MapDefinition ForestMap()
        {
            var bounds = new Rect(-28f, -10f, 56f, 28f);
            return new MapDefinition
            {
                id = ForestMapId,
                sceneMapId = ForestMapId,
                displayName = "Proto Forest",
                aliases = new[] { "forest", "mistwood" },
                locationId = "loc.sub_forest",
                mapType = MapType.Overworld,
                scenePath = ForestScenePath,
                atlasPosition = new Vector2(0.78f, 0.40f),
                localBounds = bounds,
                atlasScale = 200f,
                tags = new[] { "biome.temperate_forest" },
                dangerMin = 4,
                dangerMax = 6,
                recommendedLevel = 6,
                defaultEntryAnchorId = "port.forest.west",
                anchors = new[]
                {
                    Anchor("port.forest.west", "North Trail", MapAnchorKind.Both, "node.forest.west", new Vector2(-26f, 0.6f)),
                    Anchor("port.forest.east", "Deep Wood Gate", MapAnchorKind.Both, "node.forest.east", new Vector2(26f, 0.6f)),
                    Anchor("port.forest.ferry", "River Landing", MapAnchorKind.Both, "node.forest.ferry", new Vector2(22f, 0.6f))
                },
                roadNodes = new[]
                {
                    Node("node.forest.west", -26f, 0.5f),
                    Node("node.forest.climb_west", -12f, 0.5f),
                    Node("node.forest.crest", 0f, 9f, true),
                    Node("node.forest.climb_east", 12f, 0.5f),
                    Node("node.forest.ferry", 22f, 0.5f),
                    Node("node.forest.east", 26f, 0.5f)
                },
                roadSegments = new[]
                {
                    Seg("seg.forest.west_climb", "node.forest.west", "node.forest.climb_west", TravelMode.Road, -1f, 2f, 75f,
                        new[] { new Vector2(-26f, 0.5f), new Vector2(-18f, 0.5f), new Vector2(-12f, 0.5f) }),
                    // The rock shelf splits the wood; the only walkable line goes over the crest.
                    Seg("seg.forest.climb_crest", "node.forest.climb_west", "node.forest.crest", TravelMode.Footpath, -1f, 4f, 60f,
                        new[] { new Vector2(-12f, 0.5f), new Vector2(-9f, 4f), new Vector2(-5f, 7.5f), new Vector2(0f, 9f) }),
                    Seg("seg.forest.crest_climb", "node.forest.crest", "node.forest.climb_east", TravelMode.Footpath, -1f, 4f, 60f,
                        new[] { new Vector2(0f, 9f), new Vector2(5f, 7.5f), new Vector2(9f, 4f), new Vector2(12f, 0.5f) }),
                    Seg("seg.forest.climb_ferry", "node.forest.climb_east", "node.forest.ferry", TravelMode.Road, -1f, 2f, 40f,
                        new[] { new Vector2(12f, 0.5f), new Vector2(17f, 0.5f), new Vector2(22f, 0.5f) }),
                    Seg("seg.forest.ferry_east", "node.forest.ferry", "node.forest.east", TravelMode.Footpath, -1f, 1f, 12f,
                        new[] { new Vector2(22f, 0.5f), new Vector2(26f, 0.5f) })
                }
            };
        }

        // ------------------------------------------------------------------ connections

        private static MapConnectionDefinition[] BuildConnections() => new[]
        {
            new MapConnectionDefinition
            {
                id = "conn.hub_grassland",
                label = "Keep Road",
                fromMapId = HubMapId, fromAnchorId = "port.hub.west",
                toMapId = GrasslandMapId, toAnchorId = "port.grassland.west",
                direction = ConnectionDirection.Bidirectional, travelMode = TravelMode.Road,
                baseDistance = 60f, riskCost = 1f, travelTime = 20f,
                atlasPolyline = new[] { new Vector2(0.22f, 0.30f), new Vector2(0.32f, 0.38f), new Vector2(0.42f, 0.46f) }
            },
            new MapConnectionDefinition
            {
                id = "conn.grassland_pass",
                label = "Windward Track",
                fromMapId = GrasslandMapId, fromAnchorId = "port.grassland.east",
                toMapId = PassMapId, toAnchorId = "port.pass.west",
                direction = ConnectionDirection.Bidirectional, travelMode = TravelMode.MountainPass,
                baseDistance = 60f, riskCost = 5f, travelTime = 60f,
                atlasPolyline = new[] { new Vector2(0.42f, 0.46f), new Vector2(0.51f, 0.54f), new Vector2(0.60f, 0.62f) }
            },
            new MapConnectionDefinition
            {
                id = PassBridgeConnectionId,
                label = "Pass Bridge (locked until the key is found)",
                fromMapId = PassMapId, fromAnchorId = "port.pass.east",
                toMapId = ForestMapId, toAnchorId = "port.forest.west",
                direction = ConnectionDirection.Bidirectional, travelMode = TravelMode.Bridge,
                baseDistance = 70f, riskCost = 4f, travelTime = 45f,
                condition = new AtlasCondition
                {
                    kind = AtlasConditionKind.FlagSet,
                    id = BridgeKeyFlag,
                    description = "the pass bridge key"
                },
                atlasPolyline = new[] { new Vector2(0.60f, 0.62f), new Vector2(0.69f, 0.52f), new Vector2(0.78f, 0.40f) }
            },
            new MapConnectionDefinition
            {
                id = "conn.grassland_forest_ferry",
                label = "River Ferry (no schedule recorded)",
                fromMapId = GrasslandMapId, fromAnchorId = "port.grassland.ferry",
                toMapId = ForestMapId, toAnchorId = "port.forest.ferry",
                direction = ConnectionDirection.Bidirectional, travelMode = TravelMode.Ferry,
                baseDistance = 200f, riskCost = 2f,
                // Deliberately unknown: the ferry has no recorded schedule, which is what makes the Fastest
                // objective unavailable along this route instead of guessing a time.
                travelTime = -1f,
                atlasPolyline = new[] { new Vector2(0.42f, 0.46f), new Vector2(0.50f, 0.32f), new Vector2(0.66f, 0.34f), new Vector2(0.78f, 0.40f) }
            },
            new MapConnectionDefinition
            {
                id = "conn.forest_river_current",
                label = "River Current (one way downstream)",
                fromMapId = ForestMapId, fromAnchorId = "port.forest.west",
                toMapId = GrasslandMapId, toAnchorId = "port.grassland.ferry",
                direction = ConnectionDirection.FromToOnly, travelMode = TravelMode.Boat,
                baseDistance = 95f, riskCost = 3f, travelTime = 25f,
                atlasPolyline = new[] { new Vector2(0.78f, 0.40f), new Vector2(0.62f, 0.28f), new Vector2(0.46f, 0.44f) }
            }
        };

        // ------------------------------------------------------------------ markers

        private static MapPoiDefinition[] BuildPois() => new[]
        {
            // -------- hub: ruins, no authored objectives, so completion reports as N/A
            Poi("poi.hub.chest_common", HubMapId, PoiKind.ChestCommon, "Supply Chest", new Vector2(30f, 1.2f)),
            Poi("poi.hub.enemy_rat", HubMapId, PoiKind.EnemySpecies, "Ruins rat", new Vector2(8f, 0.2f), species: "rat"),

            // -------- grassland dungeon (used by the 'has dungeon' filter test)
            Poi("poi.grass.dungeon", GrasslandMapId, PoiKind.Dungeon, "Sunken Barrow", new Vector2(-14f, 0.2f)),

            // -------- grassland
            Poi("poi.grass.town", GrasslandMapId, PoiKind.Town, "Wayfarer's Rest", new Vector2(15f, 0.2f)),
            Poi("poi.grass.chest_common_1", GrasslandMapId, PoiKind.ChestCommon, "Field Chest", new Vector2(-20f, 0.2f), objective: true),
            Poi("poi.grass.chest_common_2", GrasslandMapId, PoiKind.ChestCommon, "Bridge Chest", new Vector2(-9f, 0.2f), objective: true),
            Poi("poi.grass.chest_purple", GrasslandMapId, PoiKind.ChestPurple, "Ferry Chest", new Vector2(21f, 0.2f), objective: true),
            Poi("poi.grass.guild", GrasslandMapId, PoiKind.Guild, "Adventurers' Guild", new Vector2(15f, 0.2f)),
            Poi("poi.grass.enemy_wolf", GrasslandMapId, PoiKind.EnemySpecies, "Plain wolf", new Vector2(4f, 0.2f), species: "wolf"),
            Poi("poi.grass.enemy_bandit", GrasslandMapId, PoiKind.EnemySpecies, "Bandit", new Vector2(18f, 0.2f), species: "bandit"),
            Poi("poi.grass.story_main", GrasslandMapId, PoiKind.MainStory, "Chapter 1: the broken keep", new Vector2(24f, 0.2f), chapter: "ch1", objective: true),
            Poi("poi.grass.exit_east", GrasslandMapId, PoiKind.MapExit, "Windward Track", new Vector2(27f, 0.2f)),
            Poi("poi.grass.bridge", GrasslandMapId, PoiKind.Bridge, "Stone Bridge", new Vector2(0f, 0.9f)),
            Poi("poi.grass.port_ferry", GrasslandMapId, PoiKind.Port, "River Ferry", new Vector2(24f, 0.2f)),
            Poi("poi.grass.npc_elder", GrasslandMapId, PoiKind.NpcLocation, "Elder Mira", new Vector2(15f, 0.2f),
                pathAction: "guide", npcRouteId: "npcroute.grass.elder"),

            // -------- mountain pass
            Poi("poi.pass.chest_common", PassMapId, PoiKind.ChestCommon, "Pass Chest", new Vector2(-4f, -6f), objective: true),
            Poi("poi.pass.chest_special", PassMapId, PoiKind.ChestSpecial, "Ridge Reliquary", new Vector2(8f, -3f), objective: true),
            Poi("poi.pass.boss", PassMapId, PoiKind.Boss, "Ridge Warden", new Vector2(0f, -7f), danger: 6, level: 6),
            Poi("poi.pass.enemy_golem", PassMapId, PoiKind.EnemySpecies, "Rock golem", new Vector2(10f, 0.2f), species: "rock_golem", danger: 4),
            Poi("poi.pass.enemy_harpy", PassMapId, PoiKind.EnemySpecies, "Cliff harpy", new Vector2(-10f, 0.2f), species: "harpy", danger: 5),
            Poi("poi.pass.story_crossed", PassMapId, PoiKind.CrossedPath, "Crossed Path: two travellers", new Vector2(-14f, 0.2f), chapter: "ch2", objective: true),
            Poi("poi.pass.gate_locked", PassMapId, PoiKind.LockedGate, "Bridge Gate (key required)", new Vector2(22f, 0.6f)),
            Poi("poi.pass.exit_east", PassMapId, PoiKind.MapExit, "Downhill Track", new Vector2(22f, 0.2f)),
            Poi("poi.pass.npc_scout", PassMapId, PoiKind.NpcLocation, "Scout Ivo", new Vector2(-18f, 0.2f),
                pathAction: "inquire", npcRouteId: "npcroute.pass.scout_incomplete"),

            // -------- forest
            Poi("poi.forest.chest_common_1", ForestMapId, PoiKind.ChestCommon, "Hollow Log", new Vector2(-22f, 0.2f), objective: true),
            Poi("poi.forest.chest_common_2", ForestMapId, PoiKind.ChestCommon, "Mossy Cache", new Vector2(-8f, 0.2f), objective: true),
            Poi("poi.forest.chest_common_3", ForestMapId, PoiKind.ChestCommon, "Crest Chest", new Vector2(0f, 9.2f), objective: true),
            Poi("poi.forest.chest_purple", ForestMapId, PoiKind.ChestPurple, "Deepwood Reliquary", new Vector2(18f, 0.2f), objective: true),
            Poi("poi.forest.shop", ForestMapId, PoiKind.Shop, "Trapper's Post", new Vector2(20f, 0.2f)),
            Poi("poi.forest.altar", ForestMapId, PoiKind.Altar, "Moss Altar", new Vector2(6f, 5f)),
            Poi("poi.forest.enemy_wolf", ForestMapId, PoiKind.EnemySpecies, "Mistwolf", new Vector2(-10f, 0.2f), species: "wolf", danger: 4),
            Poi("poi.forest.enemy_spider", ForestMapId, PoiKind.EnemySpecies, "Grove spider", new Vector2(10f, 0.2f), species: "grove_spider", danger: 5),
            Poi("poi.forest.story_side", ForestMapId, PoiKind.SideStory, "Side Story: the trapper's debt", new Vector2(18f, 0.2f), chapter: "ch2", objective: true),
            Poi("poi.forest.dungeon", ForestMapId, PoiKind.Cave, "Grove Hollow", new Vector2(-6f, 0.2f)),
            Poi("poi.forest.landmark", ForestMapId, PoiKind.Landmark, "Sunken Shrine", new Vector2(-16f, 1.5f)),
            Poi("poi.forest.npc_trapper", ForestMapId, PoiKind.NpcLocation, "Trapper Sena", new Vector2(20f, 0.2f),
                pathAction: "purchase", npcRouteId: "npcroute.forest.trapper")
        };

        private static NpcRouteDefinition[] BuildNpcRoutes() => new[]
        {
            new NpcRouteDefinition
            {
                id = "npcroute.grass.elder",
                npcId = "npc.elder_mira",
                displayName = "Elder Mira's round",
                mapId = GrasslandMapId,
                waypoints = new[]
                {
                    new Vector2(15f, 0.2f), new Vector2(-4f, 0.2f), new Vector2(0f, 0.9f), new Vector2(21f, 0.2f)
                },
                visibility = NpcRouteVisibility.HiddenUntilKnown,
                knownByDefault = false
            },
            new NpcRouteDefinition
            {
                id = "npcroute.forest.trapper",
                npcId = "npc.trapper_sena",
                displayName = "Trapper's patrol",
                mapId = ForestMapId,
                waypoints = new[]
                {
                    new Vector2(20f, 0.2f), new Vector2(12f, 0.5f), new Vector2(0f, 9f), new Vector2(-22f, 0.2f)
                },
                visibility = NpcRouteVisibility.ShownWhenDataExists,
                knownByDefault = true
            },
            new NpcRouteDefinition
            {
                // Deliberately incomplete: one waypoint is not a route. The validator must warn and the overlay
                // must stay hidden instead of drawing a line to nowhere.
                id = "npcroute.pass.scout_incomplete",
                npcId = "npc.scout_ivo",
                displayName = "Scout Ivo's patrol (data incomplete)",
                mapId = PassMapId,
                waypoints = new[] { new Vector2(-18f, 0.2f) },
                visibility = NpcRouteVisibility.HiddenUntilKnown,
                knownByDefault = false
            }
        };

        private static AtlasBlockerDefinition[] BuildBlockers() => new[]
        {
            // The grassland gorge: water below the bridge. The bridge geometry runs above its top edge, so the
            // authored crossing is clear and a straight chord between the banks is not.
            Blocker("blocker.grass.gorge", "River gorge", GrasslandMapId,
                new[]
                {
                    new Vector2(-1.2f, -10f), new Vector2(1.2f, -10f), new Vector2(1.2f, -0.6f), new Vector2(-1.2f, -0.6f)
                }),
            // The pass ridge: impassable, with the corridor below it where the road goes.
            Blocker("blocker.pass.ridge", "Windward ridge", PassMapId,
                new[]
                {
                    new Vector2(-3.5f, -2f), new Vector2(3.5f, -2f), new Vector2(5.5f, 4f),
                    new Vector2(3f, 12f), new Vector2(-3f, 12f), new Vector2(-5.5f, 4f)
                }),
            // The forest rock shelf: the route goes over the crest above it.
            Blocker("blocker.forest.shelf", "Rock shelf", ForestMapId,
                new[]
                {
                    new Vector2(-7f, -8f), new Vector2(7f, -8f), new Vector2(8f, 3f),
                    new Vector2(4f, 7f), new Vector2(-4f, 7f), new Vector2(-8f, 3f)
                })
        };

        // ------------------------------------------------------------------ scene specs

        private sealed class PortSpec
        {
            public string AnchorId;
            public Vector2 Position;
            public bool IsDefault;
        }

        private sealed class TriggerSpec
        {
            public string ConnectionId;
            public Vector2 Position;
            public Vector2 Size;
        }

        private sealed class SceneSpec
        {
            public string MapId;
            public string ScenePath;
            public string RootName;
            public Rect Bounds;
            public Vector2 PlayerSpawn;
            public List<Vector4> Ground = new List<Vector4>();
            public List<Vector3> Platforms = new List<Vector3>();
            public List<PortSpec> Ports = new List<PortSpec>();
            public List<TriggerSpec> Triggers = new List<TriggerSpec>();
            public List<Vector4> Enemies = new List<Vector4>();
        }

        private static SceneSpec GrasslandSpec() => new SceneSpec
        {
            MapId = GrasslandMapId,
            ScenePath = GrasslandScenePath,
            RootName = "Proto_Grassland",
            Bounds = new Rect(-30f, -10f, 60f, 26f),
            PlayerSpawn = new Vector2(-26f, 0.6f),
            Ground = new List<Vector4>
            {
                new Vector4(-30f, -1.2f, 0f, 8f),
                new Vector4(1.2f, 30f, 0f, 8f),
                new Vector4(-1.2f, 1.2f, -6f, 4f)
            },
            Platforms = new List<Vector3>
            {
                new Vector3(10f, 4.5f, 2.5f),
                new Vector3(-16f, 5f, 2f)
            },
            Ports = new List<PortSpec>
            {
                new PortSpec { AnchorId = "port.grassland.west", Position = new Vector2(-26f, 0.6f), IsDefault = true },
                new PortSpec { AnchorId = "port.grassland.east", Position = new Vector2(27f, 0.6f) },
                new PortSpec { AnchorId = "port.grassland.ferry", Position = new Vector2(24f, 0.6f) }
            },
            Triggers = new List<TriggerSpec>
            {
                new TriggerSpec { ConnectionId = "conn.hub_grassland", Position = new Vector2(-28f, 1.5f), Size = new Vector2(2f, 5f) },
                new TriggerSpec { ConnectionId = "conn.grassland_pass", Position = new Vector2(29f, 1.5f), Size = new Vector2(2f, 5f) },
                new TriggerSpec { ConnectionId = "conn.grassland_forest_ferry", Position = new Vector2(24f, 4f), Size = new Vector2(3f, 2f) }
            },
            Enemies = new List<Vector4>
            {
                new Vector4(4f, 0.05f, 0f, 3.5f),
                new Vector4(18f, 0.05f, 0f, 2.5f)
            }
        };

        private static SceneSpec PassSpec() => new SceneSpec
        {
            MapId = PassMapId,
            ScenePath = PassScenePath,
            RootName = "Proto_MountainPass",
            Bounds = new Rect(-25f, -14f, 50f, 30f),
            PlayerSpawn = new Vector2(-22f, 0.6f),
            Ground = new List<Vector4>
            {
                new Vector4(-25f, -3.5f, 0f, 12f),
                new Vector4(3.5f, 25f, 0f, 12f),
                new Vector4(-3.5f, 3.5f, -8f, 6f)
            },
            Platforms = new List<Vector3>
            {
                new Vector3(-8f, -3f, 3f),
                new Vector3(8f, -3f, 3f)
            },
            Ports = new List<PortSpec>
            {
                new PortSpec { AnchorId = "port.pass.west", Position = new Vector2(-22f, 0.6f), IsDefault = true },
                new PortSpec { AnchorId = "port.pass.east", Position = new Vector2(22f, 0.6f) }
            },
            Triggers = new List<TriggerSpec>
            {
                new TriggerSpec { ConnectionId = "conn.grassland_pass", Position = new Vector2(-24f, 1.5f), Size = new Vector2(2f, 5f) },
                // The gate at the east end is the locked bridge: crossing it before the key exists is refused by
                // the transition service, which is the whole point of the condition on that connection.
                new TriggerSpec { ConnectionId = PassBridgeConnectionId, Position = new Vector2(24f, 1.5f), Size = new Vector2(2f, 5f) }
            },
            Enemies = new List<Vector4>
            {
                new Vector4(-6f, 0.05f, 0f, 4f),
                new Vector4(12f, 0.05f, 0f, 3f)
            }
        };

        private static SceneSpec ForestSpec() => new SceneSpec
        {
            MapId = ForestMapId,
            ScenePath = ForestScenePath,
            RootName = "Proto_Forest",
            Bounds = new Rect(-28f, -10f, 56f, 28f),
            PlayerSpawn = new Vector2(-26f, 0.6f),
            Ground = new List<Vector4>
            {
                new Vector4(-28f, -7f, 0f, 8f),
                new Vector4(7f, 28f, 0f, 8f)
            },
            Platforms = new List<Vector3>
            {
                new Vector3(-9f, 3.5f, 2.5f),
                new Vector3(-3f, 6.5f, 2.5f),
                new Vector3(3f, 6.5f, 2.5f),
                new Vector3(9f, 3.5f, 2.5f)
            },
            Ports = new List<PortSpec>
            {
                new PortSpec { AnchorId = "port.forest.west", Position = new Vector2(-26f, 0.6f), IsDefault = true },
                new PortSpec { AnchorId = "port.forest.east", Position = new Vector2(26f, 0.6f) },
                new PortSpec { AnchorId = "port.forest.ferry", Position = new Vector2(22f, 0.6f) }
            },
            Triggers = new List<TriggerSpec>
            {
                new TriggerSpec { ConnectionId = PassBridgeConnectionId, Position = new Vector2(-27f, 1.5f), Size = new Vector2(2f, 5f) },
                new TriggerSpec { ConnectionId = "conn.grassland_forest_ferry", Position = new Vector2(22f, 4f), Size = new Vector2(3f, 2f) },
                new TriggerSpec { ConnectionId = "conn.forest_river_current", Position = new Vector2(-27f, 4f), Size = new Vector2(3f, 2f) }
            },
            Enemies = new List<Vector4>
            {
                new Vector4(16f, 0.05f, 0f, 3.5f),
                new Vector4(-20f, 0.05f, 0f, 2.5f)
            }
        };

        // ------------------------------------------------------------------ scene building

        private static void BuildScene(SceneSpec spec)
        {
            if (spec == null)
                return;

            WorldAtlasDefinition atlas = LoadAtlas();
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject(spec.RootName).transform;

            // -------- managers
            Transform managers = SceneBuilder.Group("--- MANAGERS ---", root);
            GameObject spawn = SceneBuilder.CreateMarker("PlayerSpawn", managers, new Vector3(spec.PlayerSpawn.x, spec.PlayerSpawn.y, 0f));
            SceneBuilder.CreateGameManager(managers, spawn.transform);

            var identityObject = new GameObject("MapIdentity");
            identityObject.transform.SetParent(managers, false);
            var identity = identityObject.AddComponent<MapIdentity>();
            identity.EditorConfigure(spec.MapId, spec.ScenePath);

            var runtimeObject = new GameObject("WorldAtlasRuntime");
            runtimeObject.transform.SetParent(managers, false);
            var runtime = runtimeObject.AddComponent<WorldAtlasRuntime>();
            if (atlas != null)
                SceneBuilder.SetObject(runtime, "atlas", atlas);

            Transform ports = SceneBuilder.Group("EntryPorts", managers);
            foreach (PortSpec port in spec.Ports)
            {
                var portObject = new GameObject("Port_" + port.AnchorId);
                portObject.transform.SetParent(ports, false);
                portObject.transform.position = new Vector3(port.Position.x, port.Position.y, 0f);
                var entry = portObject.AddComponent<MapEntryPoint>();
                entry.EditorConfigure(port.AnchorId, port.IsDefault);
            }

            // -------- camera (the follow component is wired once the player exists, below)
            Camera camera = SceneBuilder.BuildCamera(root, scene);

            // -------- environment
            Transform environment = SceneBuilder.Group("--- ENVIRONMENT ---", root);
            SceneBuilder.BuildLighting(environment);
            Transform terrain = SceneBuilder.Group("Terrain", environment);
            for (int i = 0; i < spec.Ground.Count; i++)
            {
                Vector4 segment = spec.Ground[i];
                SceneBuilder.MakeGroundSegment("Ground_" + i, terrain, segment.x, segment.y, segment.z, segment.w);
            }

            for (int i = 0; i < spec.Platforms.Count; i++)
            {
                Vector3 platform = spec.Platforms[i];
                SceneBuilder.MakePlatform("Platform_" + i, terrain, platform.x, platform.y, platform.z);
            }

            // -------- gameplay
            Transform gameplay = SceneBuilder.Group("--- GAMEPLAY ---", root);
            GameObject player = SceneBuilder.BuildPlayer(gameplay);
            player.transform.position = new Vector3(spec.PlayerSpawn.x, spec.PlayerSpawn.y, 0f);

            SceneBuilder.WireCamera(camera, player);
            var follow = camera.gameObject.GetComponent<CameraFollow>();
            if (follow != null)
            {
                SceneBuilder.SetVector2(follow, "boundsMin", new Vector2(spec.Bounds.xMin, spec.Bounds.yMin));
                SceneBuilder.SetVector2(follow, "boundsMax", new Vector2(spec.Bounds.xMax, spec.Bounds.yMax));
            }

            Transform exits = SceneBuilder.Group("Exits", gameplay);
            foreach (TriggerSpec trigger in spec.Triggers)
                BuildTravelTrigger(exits, trigger);

            if (spec.Enemies.Count > 0)
            {
                Transform enemies = SceneBuilder.Group("Enemies", gameplay);
                for (int i = 0; i < spec.Enemies.Count; i++)
                {
                    Vector4 enemy = spec.Enemies[i];
                    SceneBuilder.BuildEnemy("Enemy_" + i, enemies, new Vector3(enemy.x, enemy.y, 0f), player.transform, enemy.w);
                }
            }

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, spec.ScenePath);
            SceneBuilder.RegisterSceneInBuildSettings(spec.ScenePath);

            Debug.Log($"[WorldPrototypeBuilder] Built '{spec.ScenePath}' for map '{spec.MapId}' " +
                      $"({spec.Ports.Count} ports, {spec.Triggers.Count} travel boundaries).");
        }

        private static void BuildTravelTrigger(Transform parent, TriggerSpec spec)
        {
            var go = new GameObject("Exit_" + spec.ConnectionId);
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(spec.Position.x, spec.Position.y, 0f);

            var collider = go.AddComponent<BoxCollider2D>();
            collider.isTrigger = true;
            collider.size = spec.Size;

            var trigger = go.AddComponent<MapTravelTrigger>();
            trigger.EditorConfigure(spec.ConnectionId);
        }

        // ------------------------------------------------------------------ small constructors

        private static AtlasTagDefinition Tag(string id, string name, AtlasTagCategory category) => new AtlasTagDefinition
        {
            id = id,
            displayName = name,
            category = category
        };

        private static AtlasLocationDefinition Location(string id, string name, LocationKind kind, string parent, params string[] tags) =>
            new AtlasLocationDefinition
            {
                id = id,
                displayName = name,
                kind = kind,
                parentId = parent,
                tags = tags
            };

        private static MapAnchorDefinition Anchor(string id, string name, MapAnchorKind kind, string roadNodeId, Vector2 position) =>
            new MapAnchorDefinition
            {
                id = id,
                displayName = name,
                kind = kind,
                roadNodeId = roadNodeId,
                position = position
            };

        private static MapRoadNodeDefinition Node(string id, float x, float y, bool landmark = false) => new MapRoadNodeDefinition
        {
            id = id,
            position = new Vector2(x, y),
            isLandmark = landmark
        };

        private static MapRoadSegmentDefinition Seg(string id, string from, string to, TravelMode mode, float distance, float risk,
            float time, Vector2[] polyline) => new MapRoadSegmentDefinition
        {
            id = id,
            fromNodeId = from,
            toNodeId = to,
            bidirectional = true,
            mode = mode,
            distance = distance,
            riskCost = risk,
            travelTime = time,
            polyline = polyline
        };

        private static MapPoiDefinition Poi(string id, string mapId, PoiKind kind, string name, Vector2 position,
            string species = "", string chapter = "", string pathAction = "", string npcRouteId = "",
            int danger = 0, int level = 0, bool objective = false) => new MapPoiDefinition
        {
            id = id,
            mapId = mapId,
            kind = kind,
            displayName = name,
            position = position,
            enemySpecies = species,
            storyChapter = chapter,
            pathAction = pathAction,
            npcRouteId = npcRouteId,
            dangerLevel = danger,
            recommendedLevel = level,
            countsAsObjective = objective
        };

        private static AtlasBlockerDefinition Blocker(string id, string label, string mapId, Vector2[] points) =>
            new AtlasBlockerDefinition
            {
                id = id,
                label = label,
                mapId = mapId,
                points = points
            };

        internal static void EnsureFolders()
        {
            SceneBuilder.EnsureFolder("Assets/Game");
            SceneBuilder.EnsureFolder(WorldFolder);
        }
    }
}
#endif

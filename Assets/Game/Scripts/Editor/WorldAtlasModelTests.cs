#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DarkRealms.World;
using UnityEditor;
using UnityEngine;

namespace DarkRealms.EditorTools
{
    /// <summary>
    /// Deterministic tests for the parts of the world feature that do not need a running game: the authored atlas,
    /// the query system, the route planner, route geometry and the save store.
    ///
    /// Every expectation is either hand-computed (small graphs, exact costs) or derived from the atlas itself, so a
    /// failure points at behaviour rather than at a hard-coded copy of the data.
    /// </summary>
    public static class WorldAtlasModelTests
    {
        public const string ReportPath = @"D:\Unity Project\My project\WorldAtlasModelTestsReport.txt";

        private static readonly List<string> Lines = new List<string>();
        private static int pass;
        private static int fail;

        public static int PassCount => pass;
        public static int FailCount => fail;

        private static void Log(string text)
        {
            Lines.Add(text);
            Debug.Log("[AtlasTests] " + text);
        }

        private static void Check(string label, bool ok, string detail)
        {
            if (ok)
            {
                pass++;
                Log("  [PASS] " + label + " :: " + detail);
            }
            else
            {
                fail++;
                Log("  [FAIL] " + label + " :: " + detail);
            }
        }

        private static void Flush() => File.WriteAllText(ReportPath, string.Join("\n", Lines) + "\n");

        // ------------------------------------------------------------------ entry point

        public static bool RunAll()
        {
            Lines.Clear();
            pass = 0;
            fail = 0;

            WorldAtlasDefinition atlas = WorldPrototypeBuilder.LoadAtlas();
            if (atlas == null)
            {
                Log("SETUP FAILED: no prototype atlas on disk");
                Flush();
                return false;
            }

            atlas.RebuildIndex();

            Log("=== section 1: atlas content ===");
            TestAtlasContent(atlas);

            Log("=== section 2: progress, conditions and completion ===");
            TestProgress(atlas);

            Log("=== section 3: save store ===");
            TestSaveStore();

            Log("=== section 4: route planning on hand-computed graphs ===");
            TestHandComputedGraph();

            Log("=== section 5: route planning on the prototype world ===");
            TestPrototypeRouting(atlas);

            Log("=== section 6: route geometry, blockers and smoothing ===");
            TestGeometry(atlas);

            Log("=== section 7: search and filters ===");
            TestFilters(atlas);

            Log("=== section 8: validator on a deliberately broken atlas ===");
            TestValidatorMutations(atlas);

            Log($"=== result: {pass} PASS / {fail} FAIL ===");
            Flush();
            return fail == 0;
        }

        // ------------------------------------------------------------------ 1. content

        private static void TestAtlasContent(WorldAtlasDefinition atlas)
        {
            Check("four prototype maps", atlas.maps.Length == 4, $"found {atlas.maps.Length}");

            foreach (string id in new[]
                     {
                         WorldPrototypeBuilder.HubMapId, WorldPrototypeBuilder.GrasslandMapId,
                         WorldPrototypeBuilder.PassMapId, WorldPrototypeBuilder.ForestMapId
                     })
            {
                MapDefinition map = atlas.GetMap(id);
                Check($"map '{id}' exists", map != null, map != null ? $"scene {map.scenePath}" : "missing");
            }

            Check("the hub anchors exactly one connection",
                atlas.ConnectionsOf(WorldPrototypeBuilder.HubMapId).Count == 1,
                $"hub has {atlas.ConnectionsOf(WorldPrototypeBuilder.HubMapId).Count} connection(s)");

            // Chest totals are a sum of authored markers, never a stored number.
            ChestSummary grass = atlas.ChestSummaryOf(WorldPrototypeBuilder.GrasslandMapId);
            int grassChests = 0;
            foreach (MapPoiDefinition poi in atlas.PoisOf(WorldPrototypeBuilder.GrasslandMapId))
                if (poi.IsChest)
                    grassChests++;

            Check("grassland chest total is the sum of its markers",
                grass.Total == grassChests && grass.Common == 2 && grass.Purple == 1 && grass.Special == 0,
                $"{grass} vs {grassChests} marker(s)");

            ChestSummary forest = atlas.ChestSummaryOf(WorldPrototypeBuilder.ForestMapId);
            Check("forest chest total is the sum of its markers",
                forest.Total == 4 && forest.Common == 3 && forest.Purple == 1 && forest.Special == 0,
                forest.ToString());

            DangerRange passDanger = atlas.DangerRangeOf(WorldPrototypeBuilder.PassMapId);
            Check("danger range widens to include marker danger",
                passDanger.IsKnown && passDanger.Min == 3 && passDanger.Max == 6,
                passDanger.ToString() + " (map 3-5, boss marker 6)");

            List<string> species = atlas.EnemySpeciesOf(WorldPrototypeBuilder.GrasslandMapId);
            Check("enemy species are collected", species.Count == 2 && species.Contains("wolf") && species.Contains("bandit"),
                string.Join(", ", species));

            Check("breadcrumb walks the location hierarchy",
                atlas.LocationBreadcrumb(WorldPrototypeBuilder.HubMapId) ==
                "World / Prototype Continent / Freeholds / South Vale / Old Keep District / Fallen Keep",
                atlas.LocationBreadcrumb(WorldPrototypeBuilder.HubMapId));

            Check("effective tags include inherited location tags",
                atlas.EffectiveTagsOf(WorldPrototypeBuilder.GrasslandMapId).Contains("nation.proto_freeholds"),
                string.Join(", ", atlas.EffectiveTagsOf(WorldPrototypeBuilder.GrasslandMapId)));

            // One-way semantics live in the data, not in the UI.
            MapConnectionDefinition oneWay = atlas.GetConnection("conn.forest_river_current");
            Check("a one-way connection resolves only in its own direction",
                oneWay != null &&
                !oneWay.TryResolveDestination(WorldPrototypeBuilder.GrasslandMapId, out _, out _) &&
                oneWay.TryResolveDestination(WorldPrototypeBuilder.ForestMapId, out string downstream, out _) &&
                downstream == WorldPrototypeBuilder.GrasslandMapId,
                oneWay != null ? $"{oneWay.direction}" : "missing");
        }

        // ------------------------------------------------------------------ 2. progress

        private static void TestProgress(WorldAtlasDefinition atlas)
        {
            var progress = new WorldProgress();

            MapCompletion hub = CompletionOf(atlas, progress, WorldPrototypeBuilder.HubMapId);
            Check("a map with no objectives reports Unknown / N/A",
                !hub.IsKnown && hub.ToString() == "Unknown / N/A", hub.ToString());

            MapCompletion grass0 = CompletionOf(atlas, progress, WorldPrototypeBuilder.GrasslandMapId);
            Check("grassland starts at 0 of its 4 authored objectives",
                grass0.IsKnown && grass0.Completed == 0 && grass0.Eligible == 4, grass0.ToString());

            progress.SetPoiState("poi.grass.story_main", DiscoveryState.Completed);
            MapCompletion grass1 = CompletionOf(atlas, progress, WorldPrototypeBuilder.GrasslandMapId);
            Check("completing one objective moves the count", grass1.Completed == 1 && grass1.Eligible == 4, grass1.ToString());

            progress.SetPoiState("poi.grass.story_main", DiscoveryState.Discovered);
            MapCompletion grass2 = CompletionOf(atlas, progress, WorldPrototypeBuilder.GrasslandMapId);
            Check("marker progress is monotonic (an older state cannot un-complete a marker)",
                grass2.Completed == 1, grass2.ToString());

            var flagCondition = new AtlasCondition { kind = AtlasConditionKind.FlagSet, id = WorldPrototypeBuilder.BridgeKeyFlag };
            Check("a flag condition is false until the flag is set",
                !flagCondition.IsSatisfied(progress) &&
                SetFlagAndTest(progress, WorldPrototypeBuilder.BridgeKeyFlag, flagCondition),
                "flag " + WorldPrototypeBuilder.BridgeKeyFlag);

            var nightCondition = new AtlasCondition { kind = AtlasConditionKind.NightOnly };
            Check("day/night conditions follow the progress clock",
                !nightCondition.IsSatisfied(progress) &&
                nightCondition.IsSatisfied(progress, true) &&
                !nightCondition.IsSatisfied(progress, false),
                "night-only rule");

            progress.Discover("map.proto.hub");
            Check("discovery does not imply having visited",
                progress.IsDiscovered("map.proto.hub") && !progress.HasVisited("map.proto.hub"),
                "discovered but not visited");

            progress.Visit("map.proto.hub");
            Check("visiting implies discovery",
                progress.HasVisited("map.proto.hub") && progress.IsDiscovered("map.proto.hub"),
                "visited");

            Check("NPC routes are known only when progress says so",
                !progress.IsNpcRouteKnown("npcroute.grass.elder"),
                "unknown by default");
        }

        private static bool SetFlagAndTest(WorldProgress progress, string flagId, AtlasCondition condition)
        {
            progress.SetFlag(flagId);
            return condition.IsSatisfied(progress);
        }

        private static MapCompletion CompletionOf(WorldAtlasDefinition atlas, WorldProgress progress, string mapId)
        {
            List<MapPoiDefinition> objectives = atlas.ObjectivesOf(mapId);
            if (objectives.Count == 0)
                return new MapCompletion(0, 0, false);

            int completed = 0;
            foreach (MapPoiDefinition poi in objectives)
                if (progress.PoiState(poi.id) == DiscoveryState.Completed)
                    completed++;

            return new MapCompletion(completed, objectives.Count, true);
        }

        // ------------------------------------------------------------------ 3. save store

        private static void TestSaveStore()
        {
            string folder = Path.Combine(Path.GetTempPath(), "darkrealms_atlas_tests_" + Guid.NewGuid().ToString("N"));
            string previousOverride = WorldSaveStore.DirectoryOverride;
            WorldSaveStore.DirectoryOverride = folder;

            try
            {
                var progress = new WorldProgress();
                progress.SetCurrentMap(WorldPrototypeBuilder.GrasslandMapId, "port.grassland.east");
                progress.SetFlag(WorldPrototypeBuilder.BridgeKeyFlag);
                progress.UnlockConnection(WorldPrototypeBuilder.PassBridgeConnectionId);
                progress.SetPoiState("poi.grass.chest_purple", DiscoveryState.Completed);
                progress.CompleteStoryMarker("ch1");
                progress.MarkNpcRouteKnown("npcroute.grass.elder");
                progress.UnlockAltar("poi.forest.altar");
                progress.ToggleFavorite(WorldPrototypeBuilder.ForestMapId);
                progress.NoteDestination(WorldPrototypeBuilder.ForestMapId);
                progress.IsNight = true;

                bool written = WorldSaveStore.Save(progress);
                Check("a save writes to the slot", written && File.Exists(WorldSaveStore.SavePath), WorldSaveStore.SavePath);
                Check("the temporary file is not left behind", !File.Exists(WorldSaveStore.TempPath), "temp cleaned");

                var reloaded = new WorldProgress();
                WorldSaveLoadResult result = WorldSaveStore.Load(reloaded);
                Check("a save round-trips",
                    result.Outcome == WorldSaveLoadOutcome.Loaded &&
                    reloaded.currentMapId == progress.currentMapId &&
                    reloaded.lastSafeAnchorId == progress.lastSafeAnchorId &&
                    reloaded.HasFlag(WorldPrototypeBuilder.BridgeKeyFlag) &&
                    reloaded.IsConnectionUnlocked(WorldPrototypeBuilder.PassBridgeConnectionId) &&
                    reloaded.PoiState("poi.grass.chest_purple") == DiscoveryState.Completed &&
                    reloaded.IsStoryMarkerCompleted("ch1") &&
                    reloaded.IsNpcRouteKnown("npcroute.grass.elder") &&
                    reloaded.IsAltarUnlocked("poi.forest.altar") &&
                    reloaded.IsFavorite(WorldPrototypeBuilder.ForestMapId) &&
                    reloaded.IsNight,
                    result.Outcome + " / " + result.Detail);

                // A second write creates the backup that corruption recovery needs.
                progress.SetFlag("flag.second_write");
                WorldSaveStore.Save(progress);

                File.WriteAllText(WorldSaveStore.SavePath, "{ this is not json");
                var recovered = new WorldProgress();
                WorldSaveLoadResult recovery = WorldSaveStore.Load(recovered);
                Check("a damaged save falls back to the backup",
                    recovery.Outcome == WorldSaveLoadOutcome.RecoveredAfterCorruption &&
                    recovered.currentMapId == progress.currentMapId,
                    recovery.Outcome + " / " + recovery.Detail);
                Check("the damaged file is preserved for inspection", File.Exists(WorldSaveStore.CorruptPath), WorldSaveStore.CorruptPath);

                // A save from a newer schema must be refused rather than half-read.
                var future = new WorldSaveData { schemaVersion = WorldProgress.CurrentSchemaVersion + 1 };
                File.WriteAllText(WorldSaveStore.SavePath, JsonUtility.ToJson(future));
                var futureProgress = new WorldProgress();
                WorldSaveLoadResult rejected = WorldSaveStore.Load(futureProgress);
                Check("a newer schema is rejected explicitly",
                    rejected.Outcome == WorldSaveLoadOutcome.RejectedNewerSchema && futureProgress.currentMapId == string.Empty,
                    rejected.Outcome + " / " + rejected.Detail);

                WorldSaveStore.DeleteAll();
                Check("the slot can be cleared", !File.Exists(WorldSaveStore.SavePath) && !File.Exists(WorldSaveStore.BackupPath),
                    "deleted");
            }
            finally
            {
                WorldSaveStore.DirectoryOverride = previousOverride;
                try
                {
                    if (Directory.Exists(folder))
                        Directory.Delete(folder, true);
                }
                catch (Exception ex)
                {
                    Log("  (cleanup note: " + ex.Message + ")");
                }
            }
        }

        // ------------------------------------------------------------------ 4. hand-computed graph

        private static void TestHandComputedGraph()
        {
            WorldAtlasDefinition atlas = BuildHandGraph();
            var progress = new WorldProgress();
            progress.SetCurrentMap("m1", "a");

            try
            {
                RoutePlan shortest = RoutePlanner.Plan(atlas, progress, "m1", "m2",
                    new RouteOptions { Objective = RouteObjective.ShortestDistance });

                Check("hand graph: shortest cost is the hand-computed 3.1",
                    shortest.Found && Mathf.Abs(shortest.TotalCost - 3.1f) < 0.001f,
                    shortest.Found ? $"cost {shortest.TotalCost:0.###}" : shortest.FailureReason);

                Check("hand graph: shortest route is A-B-C-D then the connection",
                    PathSummary(shortest) == "s.ab,s.bc,s.cd,c.12",
                    PathSummary(shortest));

                RoutePlan safest = RoutePlanner.Plan(atlas, progress, "m1", "m2",
                    new RouteOptions { Objective = RouteObjective.Safest });

                Check("hand graph: safest route avoids the risky segments",
                    safest.Found && Mathf.Abs(safest.TotalRisk - 0f) < 0.001f &&
                    PathSummary(safest) == "s.ac,s.cd,c.12",
                    safest.Found ? $"risk {safest.TotalRisk:0.###}, path {PathSummary(safest)}" : safest.FailureReason);

                RoutePlan fastest = RoutePlanner.Plan(atlas, progress, "m1", "m2",
                    new RouteOptions { Objective = RouteObjective.Fastest });

                Check("hand graph: fastest route uses the recorded times",
                    fastest.Found && Mathf.Abs(fastest.TotalTime - 3.1f) < 0.001f,
                    fastest.Found ? $"time {fastest.TotalTime:0.###}" : fastest.FailureReason);

                RoutePlan heuristic = RoutePlanner.Plan(atlas, progress, "m1", "m2", new RouteOptions
                {
                    Objective = RouteObjective.ShortestDistance,
                    Heuristic = RouteHeuristic.AtlasDistanceLowerBound
                });

                Check("hand graph: the distance heuristic is accepted and changes nothing",
                    heuristic.Found &&
                    heuristic.HeuristicUsed == RouteHeuristic.AtlasDistanceLowerBound &&
                    Mathf.Abs(heuristic.TotalCost - shortest.TotalCost) < 0.001f &&
                    PathSummary(heuristic) == PathSummary(shortest),
                    $"{heuristic.HeuristicUsed}, cost {heuristic.TotalCost:0.###}");

                // A heuristic that would not be a lower bound must be refused, not used.
                WorldAtlasDefinition cheat = BuildHandGraph(cheatDistances: true);
                try
                {
                    RoutePlan refused = RoutePlanner.Plan(cheat, progress, "m1", "m2", new RouteOptions
                    {
                        Objective = RouteObjective.ShortestDistance,
                        Heuristic = RouteHeuristic.AtlasDistanceLowerBound
                    });

                    Check("hand graph: an inadmissible heuristic is refused with a reason",
                        refused.HeuristicUsed == RouteHeuristic.None && !string.IsNullOrEmpty(refused.HeuristicNote),
                        refused.HeuristicNote);

                    Check("hand graph: the refused heuristic still yields a real route",
                        refused.Found, PathSummary(refused));
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(cheat);
                }
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(atlas);
            }
        }

        /// <summary>The route as an ordered list of segment and connection ids, which is what an exact path
        /// expectation can be written against.</summary>
        private static string PathSummary(RoutePlan plan)
        {
            var parts = new List<string>();
            foreach (RouteLeg leg in plan.Legs)
            {
                if (leg.IsConnection)
                {
                    parts.Add(leg.ConnectionId);
                    continue;
                }

                parts.AddRange(leg.SegmentIds);
            }

            return string.Join(",", parts);
        }

        /// <summary>Two maps and a five-segment road graph whose optimal routes are computed by hand in the
        /// test above: distance A-B=1, B-C=1, C-D=1, A-C=4, B-D=10.</summary>
        private static WorldAtlasDefinition BuildHandGraph(float atlasScale = 1f, bool cheatDistances = false)
        {
            var atlas = ScriptableObject.CreateInstance<WorldAtlasDefinition>();
            atlas.atlasId = "atlas.test";
            atlas.startMapId = "m1";
            atlas.declaredFlagIds = Array.Empty<string>();
            atlas.tags = Array.Empty<AtlasTagDefinition>();
            atlas.locations = new[]
            {
                new AtlasLocationDefinition { id = "loc", displayName = "Test", kind = LocationKind.World, parentId = "" }
            };
            atlas.blockers = Array.Empty<AtlasBlockerDefinition>();
            atlas.pois = Array.Empty<MapPoiDefinition>();
            atlas.npcRoutes = Array.Empty<NpcRouteDefinition>();
            atlas.routeSettings = new AtlasRouteSettings { smoothingPasses = 0 };

            atlas.maps = new[]
            {
                new MapDefinition
                {
                    id = "m1", sceneMapId = "m1", displayName = "M1", locationId = "loc", atlasPosition = new Vector2(0.3f, 0.5f),
                    atlasScale = atlasScale, scenePath = "Assets/Game/Scenes/Proto_Grassland.unity",
                    localBounds = new Rect(0f, 0f, 10f, 10f), defaultEntryAnchorId = "a",
                    anchors = new[]
                    {
                        new MapAnchorDefinition { id = "a", kind = MapAnchorKind.Both, roadNodeId = "a" },
                        new MapAnchorDefinition { id = "d", kind = MapAnchorKind.Both, roadNodeId = "d" }
                    },
                    roadNodes = new[]
                    {
                        new MapRoadNodeDefinition { id = "a", position = new Vector2(0f, 0f) },
                        new MapRoadNodeDefinition { id = "b", position = new Vector2(1f, 0f) },
                        new MapRoadNodeDefinition { id = "c", position = new Vector2(2f, 0f) },
                        new MapRoadNodeDefinition { id = "d", position = new Vector2(3f, 0f) }
                    },
                    roadSegments = new[]
                    {
                        // 'cheatDistances' declares a cost below the real geometry length, which is exactly the
                        // case in which a straight-line heuristic is not a lower bound.
                        Segment("s.ab", "a", "b", cheatDistances ? 0.5f : 1f, 5f, 1f),
                        Segment("s.bc", "b", "c", 1f, 5f, 1f),
                        Segment("s.cd", "c", "d", 1f, 0f, 1f),
                        Segment("s.ac", "a", "c", 4f, 0f, 4f),
                        Segment("s.bd", "b", "d", 10f, 0f, 10f)
                    }
                },
                new MapDefinition
                {
                    id = "m2", sceneMapId = "m2", displayName = "M2", locationId = "loc", atlasPosition = new Vector2(0.6f, 0.5f),
                    atlasScale = atlasScale, scenePath = "Assets/Game/Scenes/Proto_Forest.unity",
                    localBounds = new Rect(0f, 0f, 10f, 10f), defaultEntryAnchorId = "z",
                    anchors = new[]
                    {
                        new MapAnchorDefinition { id = "z", kind = MapAnchorKind.Both, roadNodeId = "z" }
                    },
                    roadNodes = new[] { new MapRoadNodeDefinition { id = "z", position = new Vector2(0f, 0f) } },
                    roadSegments = Array.Empty<MapRoadSegmentDefinition>()
                }
            };

            atlas.connections = new[]
            {
                new MapConnectionDefinition
                {
                    id = "c.12", fromMapId = "m1", fromAnchorId = "d", toMapId = "m2", toAnchorId = "z",
                    direction = ConnectionDirection.Bidirectional, baseDistance = 0.1f, riskCost = 0f, travelTime = 0.1f,
                    atlasPolyline = Array.Empty<Vector2>()
                }
            };

            atlas.RebuildIndex();
            return atlas;
        }

        private static MapRoadSegmentDefinition Segment(string id, string from, string to, float distance, float risk, float time) =>
            new MapRoadSegmentDefinition
            {
                id = id,
                fromNodeId = from,
                toNodeId = to,
                bidirectional = true,
                distance = distance,
                riskCost = risk,
                travelTime = time,
                polyline = Array.Empty<Vector2>()
            };

        // ------------------------------------------------------------------ 5. prototype routing

        private static void TestPrototypeRouting(WorldAtlasDefinition atlas)
        {
            string hub = WorldPrototypeBuilder.HubMapId;
            string grass = WorldPrototypeBuilder.GrasslandMapId;
            string pass = WorldPrototypeBuilder.PassMapId;
            string forest = WorldPrototypeBuilder.ForestMapId;

            var progress = new WorldProgress();
            progress.SetCurrentMap(hub, "port.hub.hub");

            // Locked bridge: the only route is the long ferry.
            // Hand-computed from the authored content:
            //   hub leg (centre -> west gate)            5.0
            //   grassland leg (west gate -> river ferry) 50.04
            //   connection hub->grassland               60
            //   connection grassland->forest (ferry)    200
            const float LockedTotal = 315.04f;

            //   hub leg                                  5.0
            //   grassland leg (west gate -> east road)   53.04
            //   pass leg (west track -> downhill)        47.80
            //   connections 60 + 60 + 70                190
            const float UnlockedTotal = 295.84f;

            RoutePlan locked = RoutePlanner.Plan(atlas, progress, hub, forest, new RouteOptions());
            Check("locked bridge: the planner takes the alternate route",
                locked.Found && Mathf.Abs(locked.TotalDistance - LockedTotal) < 0.5f &&
                string.Join(",", locked.MapSequence) == hub + "," + grass + "," + forest,
                locked.Found ? $"distance {locked.TotalDistance:0.#} via {locked.MapSequenceText}" : locked.FailureReason + " | " + string.Join("; ", locked.Requirements));

            Check("locked bridge: no requirement is reported when a route does exist",
                locked.Found && locked.Requirements.Count == 0, "route found, nothing outstanding");

            // Locked bridge with the ferry also unavailable: the route must explain the lock.
            WorldAtlasDefinition ferryLocked = CloneAtlas(atlas, "AtlasTest_FerryLocked");
            try
            {
                MapConnectionDefinition ferry = ferryLocked.GetConnection("conn.grassland_forest_ferry");
                ferry.condition = new AtlasCondition { kind = AtlasConditionKind.FlagSet, id = WorldPrototypeBuilder.BridgeKeyFlag, description = "the ferry timetable" };
                ferryLocked.RebuildIndex();

                RoutePlan blocked = RoutePlanner.Plan(ferryLocked, progress, hub, forest, new RouteOptions());
                Check("when every route is locked the planner reports the requirement",
                    !blocked.Found && blocked.Requirements.Count > 0 && blocked.FailureReason.Contains("not available"),
                    blocked.FailureReason + " | requires: " + string.Join("; ", blocked.Requirements));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(ferryLocked);
            }

            // Unlocking the bridge makes the shorter pass route reachable, and the plan changes accordingly.
            progress.SetFlag(WorldPrototypeBuilder.BridgeKeyFlag);
            RoutePlan unlocked = RoutePlanner.Plan(atlas, progress, hub, forest, new RouteOptions());
            Check("unlocking the gate makes the shorter route reachable",
                unlocked.Found && Mathf.Abs(unlocked.TotalDistance - UnlockedTotal) < 0.5f &&
                string.Join(",", unlocked.MapSequence) == hub + "," + grass + "," + pass + "," + forest,
                unlocked.Found
                    ? $"distance {unlocked.TotalDistance:0.####} (expected {UnlockedTotal:0.####}) via [{string.Join(",", unlocked.MapSequence)}] expected [{hub},{grass},{pass},{forest}]"
                    : unlocked.FailureReason);

            RoutePlan safest = RoutePlanner.Plan(atlas, progress, hub, forest,
                new RouteOptions { Objective = RouteObjective.Safest });
            // Ferry route risk: grassland legs 5 + hub connection 1 + ferry 2 = 8, against 21 for the pass route.
            Check("safest objective prefers the low-risk ferry even though it is longer",
                safest.Found && Mathf.Abs(safest.TotalRisk - 8f) < 0.01f &&
                string.Join(",", safest.MapSequence) == hub + "," + grass + "," + forest,
                safest.Found ? $"risk {safest.TotalRisk:0.#} via {safest.MapSequenceText}" : safest.FailureReason);

            RoutePlan fastest = RoutePlanner.Plan(atlas, progress, hub, forest,
                new RouteOptions { Objective = RouteObjective.Fastest });
            // 6 (hub leg) + 157 (grassland legs) + 20 + 60 + 45 (connections) + 210 (pass legs) = 498 s.
            Check("fastest objective works when time data exists",
                fastest.Found && Mathf.Abs(fastest.TotalTime - 498f) < 0.01f &&
                string.Join(",", fastest.MapSequence) == hub + "," + grass + "," + pass + "," + forest,
                fastest.Found ? $"time {fastest.TotalTime:0.#} s via {fastest.MapSequenceText}" : fastest.FailureReason);

            // With the bridge locked, Fastest has no usable route and says why.
            var lockedProgress = new WorldProgress();
            lockedProgress.SetCurrentMap(hub, "port.hub.hub");
            RoutePlan fastestLocked = RoutePlanner.Plan(atlas, lockedProgress, hub, forest,
                new RouteOptions { Objective = RouteObjective.Fastest });
            Check("fastest objective refuses while the time-bearing route is locked",
                !fastestLocked.Found && !string.IsNullOrEmpty(fastestLocked.FailureReason),
                fastestLocked.FailureReason);

            // With no travel times anywhere, Fastest must refuse and name the missing data.
            WorldAtlasDefinition noTimes = CloneAtlas(atlas, "AtlasTest_NoTimes");
            try
            {
                foreach (MapDefinition map in noTimes.maps)
                {
                    foreach (MapRoadSegmentDefinition segment in map.roadSegments)
                        segment.travelTime = -1f;
                }

                noTimes.connections[0].travelTime = -1f;
                noTimes.RebuildIndex();

                RoutePlan noTimePlan = RoutePlanner.Plan(noTimes, progress, hub, forest,
                    new RouteOptions { Objective = RouteObjective.Fastest });
                Check("fastest objective refuses when no edge has recorded travel times",
                    !noTimePlan.Found && noTimePlan.FailureReason.Contains("travel times"),
                    noTimePlan.FailureReason);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(noTimes);
            }

            // The heuristic is requested but only kept if it is a proven lower bound for this data.
            RoutePlan heuristic = RoutePlanner.Plan(atlas, progress, hub, forest,
                new RouteOptions { Objective = RouteObjective.ShortestDistance, Heuristic = RouteHeuristic.AtlasDistanceLowerBound });
            Check("prototype: the heuristic is kept only when it is admissible",
                heuristic.Found && Mathf.Abs(heuristic.TotalDistance - unlocked.TotalDistance) < 0.01f &&
                heuristic.HeuristicUsed == RouteHeuristic.AtlasDistanceLowerBound,
                $"{heuristic.HeuristicUsed} ({heuristic.HeuristicNote})");

            // Safest must not use the distance heuristic, because it does not lower-bound risk.
            RoutePlan safestHeuristic = RoutePlanner.Plan(atlas, progress, hub, forest, new RouteOptions
            {
                Objective = RouteObjective.Safest,
                Heuristic = RouteHeuristic.AtlasDistanceLowerBound
            });
            Check("a distance heuristic is never used for a non-distance objective",
                safestHeuristic.HeuristicUsed == RouteHeuristic.None && !string.IsNullOrEmpty(safestHeuristic.HeuristicNote),
                safestHeuristic.HeuristicNote);

            RoutePlan hubToPass = RoutePlanner.Plan(atlas, progress, hub, pass, new RouteOptions());
            Check("adjacent maps route through one connection",
                hubToPass.Found && hubToPass.MapSequence.Count == 3,
                hubToPass.MapSequenceText);

            RoutePlan sameMap = RoutePlanner.Plan(atlas, progress, grass, grass, new RouteOptions());
            Check("a route to the current map is a zero-length plan",
                sameMap.Found && sameMap.TotalDistance == 0f, sameMap.Describe());
        }

        private static WorldAtlasDefinition CloneAtlas(WorldAtlasDefinition source, string name)
        {
            WorldAtlasDefinition clone = UnityEngine.Object.Instantiate(source);
            clone.name = name;
            clone.RebuildIndex();
            return clone;
        }

        // ------------------------------------------------------------------ 6. geometry

        private static void TestGeometry(WorldAtlasDefinition atlas)
        {
            string pass = WorldPrototypeBuilder.PassMapId;
            MapDefinition passMap = atlas.GetMap(pass);

            MapRoadSegmentDefinition firstLeg = passMap.FindRoadSegment("seg.pass.west_ridge");
            Vector2[] authored = RouteGeometry.ResolveSegmentPoints(passMap, firstLeg);
            Check("authored geometry is used when present", firstLeg.HasAuthoredGeometry && authored.Length == 4,
                $"{authored.Length} points");

            Check("the authored pass road does not cross the ridge",
                !RouteGeometry.CrossesAnyBlocker(authored, atlas, pass),
                "clear of blocker.pass.ridge");

            Vector2[] chord = { passMap.FindRoadNode("node.pass.west").position, passMap.FindRoadNode("node.pass.east").position };
            Check("a centre-to-centre chord would cross the ridge",
                RouteGeometry.CrossesAnyBlocker(chord, atlas, pass),
                "the chord is correctly rejected by the blocker");

            var progress = new WorldProgress();
            progress.SetCurrentMap(WorldPrototypeBuilder.HubMapId, "port.hub.hub");
            progress.SetFlag(WorldPrototypeBuilder.BridgeKeyFlag);
            RoutePlan plan = RoutePlanner.Plan(atlas, progress, WorldPrototypeBuilder.HubMapId, WorldPrototypeBuilder.ForestMapId,
                new RouteOptions());

            RouteLeg passLeg = null;
            foreach (RouteLeg leg in plan.Legs)
                if (!leg.IsConnection && leg.MapId == pass)
                    passLeg = leg;

            Check("the planned route has a leg inside the pass map", passLeg != null,
                passLeg != null ? $"{passLeg.SegmentIds.Count} segment(s)" : "missing");

            if (passLeg != null)
            {
                Check("the route follows the pass waypoint chain, not a chord",
                    passLeg.Points.Count > 4 && !RouteGeometry.CrossesAnyBlocker(passLeg.Points, atlas, pass),
                    $"{passLeg.Points.Count} points, clear of blockers");

                Check("the road leg is not schematic", !passLeg.IsSchematic, "authored geometry present");
            }

            // Smoothing: accepted when clear, refused when it would cut a corner into a blocker.
            var road = new List<Vector2> { new Vector2(0f, 0f), new Vector2(2f, 0f), new Vector2(4f, 0f), new Vector2(6f, 0f) };
            SmoothingResult smoothFree = RouteGeometry.Smooth(road, 1, 0.2f, Array.Empty<AtlasBlockerDefinition>());
            Check("smoothing is applied when nothing is in the way",
                smoothFree.Applied && smoothFree.Points.Count > road.Count, smoothFree.Note);

            var blockers = new List<AtlasBlockerDefinition>
            {
                new AtlasBlockerDefinition
                {
                    id = "b", mapId = pass, label = "block",
                    points = new[] { new Vector2(2.5f, -1f), new Vector2(3.5f, -1f), new Vector2(3.5f, 1f), new Vector2(2.5f, 1f) }
                }
            };
            SmoothingResult smoothBlocked = RouteGeometry.Smooth(road, 1, 0.2f, blockers);
            Check("smoothing is refused when it would cross a blocker",
                !smoothBlocked.Applied && smoothBlocked.Note.Contains("rejected"), smoothBlocked.Note);

            Check("clearance is enforced even without a crossing",
                RouteGeometry.FindClearanceViolation(new List<Vector2> { new Vector2(2.5f, 0.05f) }, 0.5f, blockers) != null,
                "a point inside the clearance band is rejected");

            // Schematic geometry: a segment with no authored polyline is labelled, not hidden.
            WorldAtlasDefinition schematic = CloneAtlas(atlas, "AtlasTest_Schematic");
            try
            {
                schematic.GetMap(pass).FindRoadSegment("seg.pass.west_ridge").polyline = Array.Empty<Vector2>();
                schematic.RebuildIndex();

                RoutePlan schematicPlan = RoutePlanner.Plan(schematic, progress, WorldPrototypeBuilder.HubMapId,
                    WorldPrototypeBuilder.ForestMapId, new RouteOptions());

                bool hasSchematicLeg = false;
                foreach (RouteLeg leg in schematicPlan.Legs)
                    hasSchematicLeg |= leg.IsSchematic;

                Check("a segment without authored geometry is marked schematic",
                    schematicPlan.Found && hasSchematicLeg && schematicPlan.IsSchematic,
                    "plan.IsSchematic = " + schematicPlan.IsSchematic);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(schematic);
            }
        }

        // ------------------------------------------------------------------ 7. filters

        private static void TestFilters(WorldAtlasDefinition atlas)
        {
            var progress = new WorldProgress();
            progress.SetCurrentMap(WorldPrototypeBuilder.HubMapId, "port.hub.hub");
            var context = new AtlasQueryContext(atlas, progress, WorldPrototypeBuilder.HubMapId);

            // (Plains OR Highlands) AND HasDungeon
            var builder = new AtlasQueryBuilder();
            builder.BiomeTags.Add("biome.plains");
            builder.BiomeTags.Add("biome.highlands");
            builder.RequiredFlags.Add(MapFlag.HasDungeon);
            AtlasQueryResults plainsAndDungeon = builder.Evaluate(context);
            Check("(Plains OR Highlands) AND HasDungeon returns exactly the grassland",
                plainsAndDungeon.Count == 1 && plainsAndDungeon.Items[0].MapId == WorldPrototypeBuilder.GrasslandMapId,
                string.Join(", ", Names(plainsAndDungeon)) + " :: " + plainsAndDungeon.Description);

            // Nation AND danger range.
            var nation = new AtlasQueryBuilder
            {
                WithinLocationId = "loc.nation_freeholds",
                Danger = new NumericComparison { op = NumericOp.Between, value = 3, secondValue = 5 }
            };
            AtlasQueryResults nationDanger = nation.Evaluate(context);
            Check("Nation=X AND Danger between 3..5 filters correctly",
                nationDanger.Count == 1 && nationDanger.Items[0].MapId == WorldPrototypeBuilder.GrasslandMapId,
                string.Join(", ", Names(nationDanger)) + " :: " + nationDanger.Description);

            // NOT / exclude.
            var noTown = new AtlasQueryBuilder();
            noTown.ExcludedFlags.Add(MapFlag.HasTown);
            AtlasQueryResults towns = noTown.Evaluate(context);
            Check("NOT has town excludes the town map",
                towns.Count == 3 && !Names(towns).Contains(atlas.GetMap(WorldPrototypeBuilder.GrasslandMapId).displayName),
                string.Join(", ", Names(towns)));

            var advancedNot = new AtlasQueryBuilder
            {
                Advanced = new AtlasFilterGroup(FilterGroupOp.Not).Add(new FlagPredicate(MapFlag.HasTown))
            };
            AtlasQueryResults advancedResult = advancedNot.Evaluate(context);
            Check("a nested NOT group excludes as documented",
                advancedResult.Count == 3 && advancedResult.Description.Contains("NOT"),
                advancedResult.Description);

            var advancedAnd = new AtlasQueryBuilder
            {
                Advanced = new AtlasFilterGroup(FilterGroupOp.And)
                    .Add(new FlagPredicate(MapFlag.HasDungeon))
                    .Add(new FlagPredicate(MapFlag.HasGuild))
            };
            Check("a nested AND group requires every child",
                advancedAnd.Evaluate(context).Count == 1, advancedAnd.Evaluate(context).Description);

            var advancedOr = new AtlasQueryBuilder
            {
                Advanced = new AtlasFilterGroup(FilterGroupOp.Or)
                    .Add(new FlagPredicate(MapFlag.HasGuild))
                    .Add(new FlagPredicate(MapFlag.HasAltar))
            };
            Check("a nested OR group accepts either child",
                advancedOr.Evaluate(context).Count == 2, string.Join(", ", Names(advancedOr.Evaluate(context))));

            // Aliases and determinism.
            var alias = new AtlasQueryBuilder { Text = "mistwood" };
            AtlasQueryResults aliasResults = alias.Evaluate(context);
            AtlasQueryResults aliasAgain = alias.Evaluate(context);
            Check("search matches an alias",
                aliasResults.Count == 1 && aliasResults.Items[0].MapId == WorldPrototypeBuilder.ForestMapId,
                string.Join(", ", Names(aliasResults)));
            Check("search is deterministic",
                string.Join(",", Names(aliasResults)) == string.Join(",", Names(aliasAgain)),
                aliasResults.Description);

            // Chest tier facets.
            var chests = new AtlasQueryBuilder();
            chests.ChestTiers.Add(ChestTier.Purple);
            Check("a purple chest facet matches only maps with purple chests",
                chests.Evaluate(context).Count == 2, string.Join(", ", Names(chests.Evaluate(context))));

            // Route reachability respects locks.
            var ferryLocked = CloneAtlas(atlas, "AtlasTest_Reachability");
            try
            {
                MapConnectionDefinition ferry = ferryLocked.GetConnection("conn.grassland_forest_ferry");
                ferry.condition = new AtlasCondition { kind = AtlasConditionKind.FlagSet, id = WorldPrototypeBuilder.BridgeKeyFlag };
                ferryLocked.RebuildIndex();

                var lockedContext = new AtlasQueryContext(ferryLocked, progress, WorldPrototypeBuilder.HubMapId);
                var reachable = new AtlasQueryBuilder { RouteReachableOnly = true };
                AtlasQueryResults reachableResults = reachable.Evaluate(lockedContext);
                Check("reachable-only respects locked connections",
                    reachableResults.Count == 3 &&
                    !Names(reachableResults).Contains(atlas.GetMap(WorldPrototypeBuilder.ForestMapId).displayName),
                    string.Join(", ", Names(reachableResults)));

                var connected = new AtlasQueryBuilder { RouteReachableOnly = true, ReachableIgnoringLocks = true };
                Check("reachability can ignore locks when asked explicitly",
                    connected.Evaluate(lockedContext).Count == 4, string.Join(", ", Names(connected.Evaluate(lockedContext))));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(ferryLocked);
            }

            // Unlocking in progress changes the answer immediately.
            var visited = new AtlasQueryBuilder();
            visited.RequiredFlags.Add(MapFlag.IsVisited);
            var visitorProgress = new WorldProgress();
            visitorProgress.SetCurrentMap(WorldPrototypeBuilder.HubMapId, "port.hub.hub");
            AtlasQueryResults visitedNow = visited.Evaluate(new AtlasQueryContext(atlas, visitorProgress, WorldPrototypeBuilder.HubMapId));
            Check("progress changes are visible to queries",
                visitedNow.Count == 1 && visitedNow.Items[0].MapId == WorldPrototypeBuilder.HubMapId,
                string.Join(", ", Names(visitedNow)));

            // Unknown data never satisfies a positive comparison.
            WorldAtlasDefinition unknownAtlas = CloneAtlas(atlas, "AtlasTest_Unknown");
            try
            {
                MapDefinition hub = unknownAtlas.GetMap(WorldPrototypeBuilder.HubMapId);
                hub.dangerMin = 0;
                hub.dangerMax = 0;

                // Strip the hub's markers so "no enemy data" and "no chest data" are genuinely unknown there.
                var stripped = new List<MapPoiDefinition>();
                foreach (MapPoiDefinition poi in unknownAtlas.pois)
                    if (poi != null && poi.mapId != hub.id)
                        stripped.Add(poi);

                unknownAtlas.pois = stripped.ToArray();
                unknownAtlas.RebuildIndex();

                var unknownContext = new AtlasQueryContext(unknownAtlas, new WorldProgress(), WorldPrototypeBuilder.HubMapId);
                var dangerLess = new AtlasQueryBuilder { Danger = new NumericComparison { op = NumericOp.LessOrEqual, value = 5 } };
                AtlasQueryResults unknownDangerResults = dangerLess.Evaluate(unknownContext);
                Check("unknown danger does not pass a positive comparison",
                    !Names(unknownDangerResults).Contains(hub.displayName),
                    string.Join(", ", Names(unknownDangerResults)));

                var unknownOnly = new AtlasQueryBuilder { IncludeUnknownDanger = true };
                AtlasQueryResults unknownOnlyResults = unknownOnly.Evaluate(unknownContext);
                Check("unknown danger can be asked for explicitly",
                    unknownOnlyResults.Count == 1 && unknownOnlyResults.Items[0].MapId == hub.id,
                    string.Join(", ", Names(unknownOnlyResults)));

                var enemyData = new AtlasQueryBuilder();
                enemyData.RequiredFlags.Add(MapFlag.HasEnemyData);
                AtlasQueryResults enemyResults = enemyData.Evaluate(unknownContext);
                Check("maps without recorded enemies do not claim enemy data",
                    !Names(enemyResults).Contains(hub.displayName),
                    string.Join(", ", Names(enemyResults)));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(unknownAtlas);
            }

            // NPC route policy: hidden unless the data and the policy allow it.
            NpcRouteDefinition complete = atlas.GetNpcRoute("npcroute.forest.trapper");
            NpcRouteDefinition incomplete = atlas.GetNpcRoute("npcroute.pass.scout_incomplete");
            NpcRouteDefinition hidden = atlas.GetNpcRoute("npcroute.grass.elder");

            var emptyProgress = new WorldProgress();
            Check("an NPC route with usable data and an open policy is drawn",
                WorldMapScreen.ShouldDrawNpcRoute(complete, emptyProgress), "forest trapper route");
            Check("an NPC route with too little data is never drawn",
                !WorldMapScreen.ShouldDrawNpcRoute(incomplete, emptyProgress), "pass scout route (1 waypoint)");
            Check("an NPC route stays hidden until the player knows it",
                !WorldMapScreen.ShouldDrawNpcRoute(hidden, emptyProgress), "elder route (unknown)");

            emptyProgress.MarkNpcRouteKnown("npcroute.grass.elder");
            Check("a known NPC route becomes drawable",
                WorldMapScreen.ShouldDrawNpcRoute(hidden, emptyProgress), "elder route after discovery");
        }

        private static List<string> Names(AtlasQueryResults results)
        {
            var names = new List<string>();
            foreach (AtlasQueryResult result in results.Items)
                names.Add(result.Name);

            return names;
        }

        // ------------------------------------------------------------------ 8. validator mutations

        private static void TestValidatorMutations(WorldAtlasDefinition atlas)
        {
            List<WorldAtlasValidator.Entry> clean = WorldAtlasValidator.Validate(atlas, includeSceneChecks: false);
            Check("the shipped atlas has no validation errors", WorldAtlasValidator.ErrorCount == 0,
                $"{WorldAtlasValidator.ErrorCount} error(s), {WorldAtlasValidator.WarningCount} warning(s)");

            // An obstacle added across a route must fail validation until the geometry is updated.
            WorldAtlasDefinition obstacle = CloneAtlas(atlas, "AtlasTest_Obstacle");
            try
            {
                var blockers = new List<AtlasBlockerDefinition>(obstacle.blockers)
                {
                    new AtlasBlockerDefinition
                    {
                        id = "blocker.test.corridor",
                        label = "New rockfall",
                        mapId = WorldPrototypeBuilder.PassMapId,
                        points = new[]
                        {
                            new Vector2(-3f, -8.5f), new Vector2(3f, -8.5f), new Vector2(3f, -5.5f), new Vector2(-3f, -5.5f)
                        }
                    }
                };
                obstacle.blockers = blockers.ToArray();
                obstacle.RebuildIndex();

                List<WorldAtlasValidator.Entry> broken = WorldAtlasValidator.Validate(obstacle, includeSceneChecks: false);
                bool blockedError = false;
                foreach (WorldAtlasValidator.Entry entry in broken)
                    blockedError |= entry.IsError && entry.Code == "geometry.crosses_blocker";

                Check("an obstacle across a route fails validation", blockedError,
                    $"{WorldAtlasValidator.ErrorCount} error(s)");

                // Second obstacle on the world map, across the ferry link: now every route crosses a blocked
                // area, so the failure has to name the geometry rather than the graph shape.
                var atlasBlockers = new List<AtlasBlockerDefinition>(obstacle.blockers)
                {
                    new AtlasBlockerDefinition
                    {
                        id = "blocker.test.atlas_sea",
                        label = "Storm sea",
                        mapId = string.Empty,
                        points = new[]
                        {
                            new Vector2(0.44f, 0.24f), new Vector2(0.72f, 0.24f), new Vector2(0.72f, 0.38f), new Vector2(0.44f, 0.38f)
                        }
                    }
                };
                obstacle.blockers = atlasBlockers.ToArray();
                obstacle.RebuildIndex();

                var progress = new WorldProgress();
                progress.SetCurrentMap(WorldPrototypeBuilder.HubMapId, "port.hub.hub");
                progress.SetFlag(WorldPrototypeBuilder.BridgeKeyFlag);

                MapConnectionDefinition ferryLink = obstacle.GetConnection("conn.grassland_forest_ferry");
                MapRoadSegmentDefinition corridor = obstacle.GetMap(WorldPrototypeBuilder.PassMapId)
                    .FindRoadSegment("seg.pass.ridge_gap");

                Check("the new world-map blocker is detected on the ferry link",
                    RouteGeometry.CrossesAnyAtlasBlocker(ferryLink.atlasPolyline, obstacle),
                    "ferry link geometry blocked");

                Check("the rockfall is detected on the pass corridor",
                    RouteGeometry.CrossesAnyBlocker(
                        RouteGeometry.ResolveSegmentPoints(obstacle.GetMap(WorldPrototypeBuilder.PassMapId), corridor),
                        obstacle, WorldPrototypeBuilder.PassMapId),
                    "pass corridor geometry blocked");

                RoutePlan blockedPlan = RoutePlanner.Plan(obstacle, progress, WorldPrototypeBuilder.HubMapId,
                    WorldPrototypeBuilder.ForestMapId, new RouteOptions());
                Check("the planner refuses geometry that crosses a blocker",
                    !blockedPlan.Found && blockedPlan.FailureReason.Contains("crosses a blocked area"),
                    blockedPlan.Found ? "unexpectedly found: " + blockedPlan.MapSequenceText + " " + blockedPlan.Describe() : blockedPlan.FailureReason);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(obstacle);
            }

            // Duplicate ids, missing maps and parent cycles must all be reported.
            WorldAtlasDefinition corrupt = CloneAtlas(atlas, "AtlasTest_Corrupt");
            try
            {
                corrupt.pois = new List<MapPoiDefinition>(corrupt.pois)
                {
                    new MapPoiDefinition { id = corrupt.pois[0].id, mapId = "map.does.not.exist", kind = PoiKind.Landmark, displayName = "duplicate" }
                }.ToArray();

                corrupt.locations = new List<AtlasLocationDefinition>(corrupt.locations)
                {
                    new AtlasLocationDefinition { id = "loc.cycle_a", displayName = "A", kind = LocationKind.Region, parentId = "loc.cycle_b" },
                    new AtlasLocationDefinition { id = "loc.cycle_b", displayName = "B", kind = LocationKind.Region, parentId = "loc.cycle_a" }
                }.ToArray();

                corrupt.GetMap(WorldPrototypeBuilder.GrasslandMapId).dangerMin = 9;
                corrupt.GetMap(WorldPrototypeBuilder.GrasslandMapId).dangerMax = 2;
                corrupt.RebuildIndex();

                List<WorldAtlasValidator.Entry> entries = WorldAtlasValidator.Validate(corrupt, includeSceneChecks: false);
                var codes = new HashSet<string>(StringComparer.Ordinal);
                foreach (WorldAtlasValidator.Entry entry in entries)
                    if (entry.IsError)
                        codes.Add(entry.Code);

                Check("duplicate marker ids are reported", codes.Contains("poi.duplicate_id"), string.Join(", ", codes));
                Check("markers on unknown maps are reported", codes.Contains("poi.missing_map"), "reported");
                Check("location parent cycles are reported", codes.Contains("location.parent_cycle"), "reported");
                Check("inverted danger ranges are reported", codes.Contains("map.danger_range"), "reported");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(corrupt);
            }

            // An availability rule that names an undeclared flag is an error, not a silent no-op.
            WorldAtlasDefinition undeclared = CloneAtlas(atlas, "AtlasTest_UndeclaredFlag");
            try
            {
                undeclared.connections[0].condition = new AtlasCondition { kind = AtlasConditionKind.FlagSet, id = "flag.nobody_sets_this" };
                undeclared.RebuildIndex();

                List<WorldAtlasValidator.Entry> entries = WorldAtlasValidator.Validate(undeclared, includeSceneChecks: false);
                bool found = false;
                foreach (WorldAtlasValidator.Entry entry in entries)
                    found |= entry.IsError && entry.Code == "condition.undeclared_flag";

                Check("an availability rule naming an undeclared flag is an error", found, "condition.undeclared_flag");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(undeclared);
            }
        }
    }
}
#endif

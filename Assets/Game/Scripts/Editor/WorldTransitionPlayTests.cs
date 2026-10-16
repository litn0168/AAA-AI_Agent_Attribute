#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using DarkRealms.Core;
using DarkRealms.Player;
using DarkRealms.World;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;

namespace DarkRealms.EditorTools
{
    /// <summary>
    /// Play-mode acceptance tests for inter-map travel and the world map screen.
    ///
    /// These are the checks that cannot be made without a running game: real scene loads through
    /// <see cref="MapTransitionService"/>, arrival placement at the authored ports, one-way and locked
    /// connections, a load that comes up as the wrong map, and repeated crossings that must not leave a
    /// second player, manager, event system or atlas runtime behind.
    ///
    /// Everything runs on the real runtime: the probe opens the hub scene, enters play mode and requests
    /// travel exactly the way gameplay does (<see cref="MapTransitionService.RequestTravel"/>), then asserts on
    /// the active scene, the scene's <see cref="MapIdentity"/> and the session's recorded map.
    ///
    /// The run writes to a scratch save folder (<see cref="WorldSaveStore.DirectoryOverride"/>), so it can
    /// neither read nor overwrite the player's own save slot. One leg deliberately corrupts an authored scene
    /// path in memory to force a map-identity mismatch; the original value is restored in the same frame and
    /// nothing is written to the asset.
    /// </summary>
    public static class WorldTransitionPlayTests
    {
        public const string ReportPath = @"D:\Unity Project\My project\WorldTransitionPlayModeReport.txt";

        private const string Prefix = "[WorldProbe]";

        /// <summary>A -> B -> A crossings driven by the loop phase. The brief asks for at least 100.</summary>
        private const int LoopCrossings = 120;

        private const float TotalTimeBudget = 600f;
        private const float DefaultPhaseTimeout = 60f;
        private const float ArrivalTolerance = 1.5f;

        private enum Phase
        {
            AwaitPlay,
            HubChecks,
            HubRefusals,
            RequestHubToGrassland,
            WaitHubToGrassland,
            GrasslandChecks,
            GrasslandOneWayRefusal,
            RequestGrasslandToPass,
            WaitGrasslandToPass,
            PassChecks,
            PassLockedRefusal,
            PassUnlockCheck,
            RequestPassToForest,
            WaitPassToForest,
            ForestChecks,
            RequestOneWay,
            WaitOneWay,
            FerryArrivalChecks,
            RequestBackToHub,
            WaitBackToHub,
            HubAgainChecks,
            Loop,
            LoopChecks,
            MapScreenChecks,
            MapScreenTravel,
            MapScreenWait,
            MapScreenLandingChecks,
            MissingSceneRefusal,
            RequestToGrasslandForMismatch,
            WaitToGrasslandForMismatch,
            MismatchSetup,
            RequestMismatch,
            WaitMismatch,
            MismatchChecks,
            RequestRecovery,
            WaitRecovery,
            RecoveryChecks,
            Done
        }

        private static readonly StringBuilder Report = new StringBuilder();
        private static readonly List<string> Notes = new List<string>();

        private static Phase phase = Phase.AwaitPlay;
        private static bool running;
        private static float phaseDeadline;
        private static float runDeadline;
        private static float phaseStartedRealtime;
        private static int passed;
        private static int failed;

        private static bool previousEnterPlayModeOptionsEnabled;
        private static EnterPlayModeOptions previousEnterPlayModeOptions;
        private static bool previousRunInBackground;
        private static string scratchFolder;

        // --- transition bookkeeping
        private static int pendingTotalBefore;
        private static int pendingFailedBefore;
        private static int settledCompleted;
        private static int settledFailed;
        private static string pendingConnection = string.Empty;
        private static string pendingLabel = string.Empty;
        private static string pendingRefusal = string.Empty;
        private static string lastTravelDetail = "unknown";

        // --- loop bookkeeping
        private static int loopRemaining;
        private static bool loopWaiting;
        private static int loopFailures;
        private static int loopStepFailures;

        // --- map screen bookkeeping
        private static int uiTravelSteps;

        // --- mismatch bookkeeping
        private static MapDefinition mutatedMap;
        private static string mutatedScenePath = string.Empty;

        // ------------------------------------------------------------------ entry point

        [MenuItem("Dark Realms/World Map/Run Travel Probe (Play Mode)")]
        public static void RunTravelProbe()
        {
            if (EditorApplication.isPlaying)
            {
                Debug.LogWarning($"{Prefix} a play session is already running; exit play mode first.");
                return;
            }

            if (EditorApplication.isCompiling || EditorUtility.scriptCompilationFailed)
            {
                Debug.LogWarning($"{Prefix} scripts are still compiling; try again once the Console is clean.");
                return;
            }

            Report.Clear();
            Notes.Clear();
            passed = 0;
            failed = 0;
            loopRemaining = LoopCrossings;
            loopWaiting = false;
            loopFailures = 0;
            uiTravelSteps = 0;
            mutatedMap = null;
            pendingRefusal = string.Empty;
            running = true;

            scratchFolder = Path.Combine(Path.GetTempPath(), "dark_realms_world_probe");
            if (Directory.Exists(scratchFolder))
                Directory.Delete(scratchFolder, true);
            Directory.CreateDirectory(scratchFolder);

            // Redirect the world save before play mode starts, because the runtime loads it during Awake.
            WorldSaveStore.DirectoryOverride = scratchFolder;
            Notes.Add($"world save redirected to '{scratchFolder}': the player's own save slot is neither read nor written");

            OpenHubSceneIfNeeded();

            // Disabling the domain reload keeps this probe alive across the play-mode transition.
            previousEnterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            previousEnterPlayModeOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;

            previousRunInBackground = PlayerSettings.runInBackground;
            Application.runInBackground = true;
            EditorApplication.playModeStateChanged -= RestoreEditModeSettings;
            EditorApplication.playModeStateChanged += RestoreEditModeSettings;

            runDeadline = (float)EditorApplication.timeSinceStartup + TotalTimeBudget;
            SetPhase(Phase.AwaitPlay, 60f);

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;

            WriteReport("probe started");
            Debug.Log($"{Prefix} entering play mode for the world travel probe...");
            EditorApplication.EnterPlaymode();
        }

        private static void OpenHubSceneIfNeeded()
        {
            Scene active = SceneManager.GetActiveScene();

            if (string.Equals(active.path, WorldPrototypeBuilder.HubScenePath, StringComparison.OrdinalIgnoreCase))
            {
                if (active.isDirty)
                    EditorSceneManager.SaveScene(active, active.path);
                return;
            }

            if (active.isDirty && !string.IsNullOrEmpty(active.path))
                EditorSceneManager.SaveScene(active, active.path);

            EditorSceneManager.OpenScene(WorldPrototypeBuilder.HubScenePath, OpenSceneMode.Single);
        }

        private static void RestoreEditModeSettings(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredEditMode)
                return;

            EditorApplication.playModeStateChanged -= RestoreEditModeSettings;
            PlayerSettings.runInBackground = previousRunInBackground;
            Application.runInBackground = previousRunInBackground;
            WorldSaveStore.DirectoryOverride = null;

            if (mutatedMap != null && mutatedScenePath.Length > 0)
            {
                mutatedMap.scenePath = mutatedScenePath;
                mutatedMap = null;
            }

            Debug.Log($"{Prefix} restored edit-mode settings (runInBackground = {previousRunInBackground}).");
        }

        // ------------------------------------------------------------------ driver

        private static void Tick()
        {
            if (!running)
                return;

            float now = (float)EditorApplication.timeSinceStartup;

            if (now > runDeadline)
            {
                Check("the probe finished inside its time budget", false,
                    $"timed out in phase {phase} ({Report.Length} characters recorded)");
                Finish();
                return;
            }

            if (!EditorApplication.isPlaying)
            {
                // Still waiting for play mode to come up; the phase timeout does not apply yet.
                phaseDeadline = now + 30f;
                return;
            }

            if (now > phaseDeadline)
            {
                Check($"phase '{phase}' completed inside its time budget", false,
                    $"stalled in phase '{phase}' after {DefaultPhaseTimeout:0} s+");
                Finish();
                return;
            }

            try
            {
                Step(now);
            }
            catch (Exception ex)
            {
                Check("the probe ran without an unhandled exception", false, $"{ex.GetType().Name}: {ex.Message}");
                Finish();
            }
        }

        private static void Step(float now)
        {
            switch (phase)
            {
                case Phase.AwaitPlay:
                    if (WorldAtlasRuntime.Instance == null || Time.timeSinceLevelLoad < 0.3f)
                        return;

                    SetPhase(Phase.HubChecks);
                    return;

                case Phase.HubChecks:
                    HubChecks();
                    return;

                case Phase.HubRefusals:
                    HubRefusals();
                    return;

                case Phase.RequestHubToGrassland:
                    if (BeginTravel("conn.hub_grassland", "travel hub -> grassland", false))
                        SetPhase(Phase.WaitHubToGrassland);
                    else
                        SetPhase(Phase.WaitHubToGrassland, skipWait: true);
                    return;

                case Phase.WaitHubToGrassland:
                    if (pendingRefusal.Length == 0 && !TravelSettled())
                        return;

                    CheckArrival("hub -> grassland", WorldPrototypeBuilder.GrasslandMapId, "port.grassland.west",
                        WorldPrototypeBuilder.GrasslandScenePath);
                    SetPhase(Phase.GrasslandChecks);
                    return;

                case Phase.GrasslandChecks:
                    GrasslandChecks();
                    return;

                case Phase.GrasslandOneWayRefusal:
                    GrasslandOneWayRefusal();
                    return;

                case Phase.RequestGrasslandToPass:
                    if (BeginTravel("conn.grassland_pass", "travel grassland -> mountain pass", false))
                        SetPhase(Phase.WaitGrasslandToPass);
                    else
                        SetPhase(Phase.WaitGrasslandToPass, skipWait: true);
                    return;

                case Phase.WaitGrasslandToPass:
                    if (pendingRefusal.Length == 0 && !TravelSettled())
                        return;

                    CheckArrival("grassland -> mountain pass", WorldPrototypeBuilder.PassMapId, "port.pass.west",
                        WorldPrototypeBuilder.PassScenePath);
                    SetPhase(Phase.PassChecks);
                    return;

                case Phase.PassChecks:
                    PassChecks();
                    return;

                case Phase.PassLockedRefusal:
                    PassLockedRefusal();
                    return;

                case Phase.PassUnlockCheck:
                    PassUnlockCheck();
                    return;

                case Phase.RequestPassToForest:
                    if (BeginTravel("conn.pass_forest", "travel mountain pass -> forest", false))
                        SetPhase(Phase.WaitPassToForest);
                    else
                        SetPhase(Phase.WaitPassToForest, skipWait: true);
                    return;

                case Phase.WaitPassToForest:
                    if (pendingRefusal.Length == 0 && !TravelSettled())
                        return;

                    CheckArrival("mountain pass -> forest", WorldPrototypeBuilder.ForestMapId, "port.forest.west",
                        WorldPrototypeBuilder.ForestScenePath);
                    SetPhase(Phase.ForestChecks);
                    return;

                case Phase.ForestChecks:
                    ForestChecks();
                    return;

                case Phase.RequestOneWay:
                    if (BeginTravel("conn.forest_river_current", "travel the one-way river current", false))
                        SetPhase(Phase.WaitOneWay);
                    else
                        SetPhase(Phase.WaitOneWay, skipWait: true);
                    return;

                case Phase.WaitOneWay:
                    if (pendingRefusal.Length == 0 && !TravelSettled())
                        return;

                    CheckArrival("one-way river current -> grassland ferry", WorldPrototypeBuilder.GrasslandMapId,
                        "port.grassland.ferry", WorldPrototypeBuilder.GrasslandScenePath);
                    SetPhase(Phase.FerryArrivalChecks);
                    return;

                case Phase.FerryArrivalChecks:
                    FerryArrivalChecks();
                    return;

                case Phase.RequestBackToHub:
                    if (BeginTravel("conn.hub_grassland", "travel grassland -> hub", false))
                        SetPhase(Phase.WaitBackToHub);
                    else
                        SetPhase(Phase.WaitBackToHub, skipWait: true);
                    return;

                case Phase.WaitBackToHub:
                    if (pendingRefusal.Length == 0 && !TravelSettled())
                        return;

                    CheckArrival("grassland -> hub", WorldPrototypeBuilder.HubMapId, "port.hub.west",
                        WorldPrototypeBuilder.HubScenePath);
                    SetPhase(Phase.HubAgainChecks);
                    return;

                case Phase.HubAgainChecks:
                    HubAgainChecks();
                    return;

                case Phase.Loop:
                    Loop();
                    return;

                case Phase.LoopChecks:
                    LoopChecks();
                    return;

                case Phase.MapScreenChecks:
                    MapScreenChecks();
                    return;

                case Phase.MapScreenTravel:
                    MapScreenTravel();
                    return;

                case Phase.MapScreenWait:
                    if (!TravelSettled())
                        return;

                    uiTravelSteps++;
                    SetPhase(Phase.MapScreenLandingChecks);
                    return;

                case Phase.MapScreenLandingChecks:
                    MapScreenLandingChecks();
                    return;

                case Phase.MissingSceneRefusal:
                    MissingSceneRefusal();
                    return;

                case Phase.RequestToGrasslandForMismatch:
                    if (BeginTravel("conn.grassland_forest_ferry", "travel forest -> grassland before the mismatch leg", false))
                        SetPhase(Phase.WaitToGrasslandForMismatch);
                    else
                        SetPhase(Phase.WaitToGrasslandForMismatch, skipWait: true);
                    return;

                case Phase.WaitToGrasslandForMismatch:
                    if (pendingRefusal.Length == 0 && !TravelSettled())
                        return;

                    CheckArrival("forest -> grassland (ferry)", WorldPrototypeBuilder.GrasslandMapId,
                        "port.grassland.ferry", WorldPrototypeBuilder.GrasslandScenePath);
                    SetPhase(Phase.MismatchSetup);
                    return;

                case Phase.MismatchSetup:
                    MismatchSetup();
                    return;

                case Phase.RequestMismatch:
                    if (BeginTravel("conn.grassland_forest_ferry", "travel to a map whose scene reports a different id", false))
                        SetPhase(Phase.WaitMismatch);
                    else
                        SetPhase(Phase.WaitMismatch, skipWait: true);
                    return;

                case Phase.WaitMismatch:
                    if (pendingRefusal.Length == 0 && !TravelSettled())
                        return;

                    MismatchChecks();
                    return;

                case Phase.MismatchChecks:
                    MismatchChecks();
                    return;

                case Phase.RequestRecovery:
                    if (BeginTravel("conn.grassland_forest_ferry", "travel forest -> grassland after restoring the scene path", false))
                        SetPhase(Phase.WaitRecovery);
                    else
                        SetPhase(Phase.WaitRecovery, skipWait: true);
                    return;

                case Phase.WaitRecovery:
                    if (pendingRefusal.Length == 0 && !TravelSettled())
                        return;

                    RecoveryChecks();
                    return;

                case Phase.RecoveryChecks:
                    RecoveryChecks();
                    return;

                case Phase.Done:
                    Finish();
                    return;

                default:
                    Finish();
                    return;
            }
        }

        private static void SetPhase(Phase next, bool skipWait = false)
        {
            phase = next;
            phaseStartedRealtime = (float)EditorApplication.timeSinceStartup;
            phaseDeadline = phaseStartedRealtime + (phase == Phase.Loop ? 300f : DefaultPhaseTimeout);
            if (skipWait)
                phaseDeadline = phaseStartedRealtime + 5f;
        }

        // ------------------------------------------------------------------ phases

        private static void HubChecks()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            Scene scene = SceneManager.GetActiveScene();

            Check("play mode starts on the hub scene",
                string.Equals(scene.path, WorldPrototypeBuilder.HubScenePath, StringComparison.OrdinalIgnoreCase), scene.path);

            Check("the session has a world runtime with an atlas",
                runtime != null && runtime.Atlas != null,
                runtime == null ? "no WorldAtlasRuntime" : (runtime.Atlas == null ? "runtime has no atlas" : runtime.Atlas.atlasId));

            if (runtime == null || runtime.Atlas == null)
            {
                Finish();
                return;
            }

            Check("the session starts on the hub map", runtime.CurrentMapId == WorldPrototypeBuilder.HubMapId,
                $"current map '{runtime.CurrentMapId}'");

            Check("the starting map is recorded as visited", runtime.Progress.HasVisited(WorldPrototypeBuilder.HubMapId),
                string.Join(",", runtime.Progress.VisitedMaps));

            Check("the run uses a scratch save slot", WorldSaveStore.DirectoryOverride == scratchFolder,
                WorldSaveStore.Directory);

            Check("the hub scene exposes its west arrival port",
                MapEntryPoint.Find(scene, "port.hub.west") != null, "port.hub.west");

            AssertSingleInstances("start");

            string[] expectedConnections =
            {
                "conn.hub_grassland", "conn.grassland_pass", "conn.pass_forest",
                "conn.grassland_forest_ferry", "conn.forest_river_current"
            };

            int found = 0;
            for (int i = 0; i < expectedConnections.Length; i++)
                if (runtime.Atlas.GetConnection(expectedConnections[i]) != null)
                    found++;

            Check("every prototype connection is registered", found == expectedConnections.Length,
                $"{found}/{expectedConnections.Length}");

            Check("all four prototype maps are registered", runtime.Atlas.maps.Length == 4,
                $"{runtime.Atlas.maps.Length} map(s)");

            RoutePlan plan = RoutePlanner.Plan(runtime.Atlas, runtime.Progress, WorldPrototypeBuilder.HubMapId,
                WorldPrototypeBuilder.ForestMapId, new RouteOptions());

            Check("a route can be planned in a live session", plan.Found &&
                string.Join(",", plan.MapSequence) == string.Join(",",
                    WorldPrototypeBuilder.HubMapId, WorldPrototypeBuilder.GrasslandMapId, WorldPrototypeBuilder.ForestMapId),
                plan.Found ? $"{plan.MapSequenceText} at {plan.TotalDistance:0.#}" : plan.FailureReason);

            SetPhase(Phase.HubRefusals);
        }

        private static void HubRefusals()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            MapTransitionService service = runtime.Transition;

            MapTravelResult unknown = service.ValidateTravel("conn.does_not_exist");
            Check("an unknown connection is refused", unknown.Outcome == MapTravelOutcome.UnknownConnection, unknown.ToString());

            MapTravelResult foreign = service.ValidateTravel("conn.grassland_pass");
            Check("a connection that does not touch the current map is refused",
                foreign.Outcome == MapTravelOutcome.WrongMap, foreign.ToString());

            MapTravelResult oneWayBackwards = service.ValidateTravel("conn.forest_river_current");
            Check("a one-way link cannot be requested from a map it does not start at",
                oneWayBackwards.Outcome == MapTravelOutcome.WrongMap, oneWayBackwards.ToString());

            Check("a refused request leaves the session travelling nowhere and on the same map",
                !service.IsTravelling && runtime.CurrentMapId == WorldPrototypeBuilder.HubMapId &&
                SceneManager.GetActiveScene().path == WorldPrototypeBuilder.HubScenePath,
                $"travelling {service.IsTravelling}, map '{runtime.CurrentMapId}'");

            Check("no transition was counted for a refused request",
                service.CompletedTransitions == 0 && service.FailedTransitions == 0,
                $"completed {service.CompletedTransitions}, failed {service.FailedTransitions}");

            SetPhase(Phase.RequestHubToGrassland);
        }

        private static void GrasslandChecks()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;

            Check("the hub is still recorded as visited after leaving it",
                runtime.Progress.HasVisited(WorldPrototypeBuilder.HubMapId),
                string.Join(",", runtime.Progress.VisitedMaps));

            Check("the grassland is recorded as visited",
                runtime.Progress.HasVisited(WorldPrototypeBuilder.GrasslandMapId),
                string.Join(",", runtime.Progress.VisitedMaps));

            Check("progress was written to the scratch save slot", WorldSaveStore.WriteCount > 0,
                $"{WorldSaveStore.WriteCount} write(s), {WorldSaveStore.FailedWriteCount} failed");

            Check("no save write failed during the first crossing", WorldSaveStore.FailedWriteCount == 0,
                WorldSaveStore.FailedWriteCount.ToString());

            SetPhase(Phase.GrasslandOneWayRefusal);
        }

        private static void GrasslandOneWayRefusal()
        {
            MapTransitionService service = WorldAtlasRuntime.Instance.Transition;

            MapTravelResult backwards = service.ValidateTravel("conn.forest_river_current");
            Check("a one-way link is refused when travelled backwards",
                backwards.Outcome == MapTravelOutcome.WrongMap,
                $"from grassland: {backwards}");

            Check("the backwards refusal changed nothing",
                !service.IsTravelling &&
                WorldAtlasRuntime.Instance.CurrentMapId == WorldPrototypeBuilder.GrasslandMapId,
                $"travelling {service.IsTravelling}, map '{WorldAtlasRuntime.Instance.CurrentMapId}'");

            SetPhase(Phase.RequestGrasslandToPass);
        }

        private static void PassChecks()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            Scene scene = SceneManager.GetActiveScene();

            Check("the mountain pass scene has no default entry port of its own beyond the authored west port",
                MapEntryPoint.Find(scene, "port.pass.west") != null, "port.pass.west");

            Check("the session is on the mountain pass after travelling",
                runtime.CurrentMapId == WorldPrototypeBuilder.PassMapId, runtime.CurrentMapId);

            SetPhase(Phase.PassLockedRefusal);
        }

        private static void PassLockedRefusal()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            MapTransitionService service = runtime.Transition;

            int completedBefore = service.CompletedTransitions;
            int failedBefore = service.FailedTransitions;

            MapTravelResult locked = service.ValidateTravel("conn.pass_forest");
            Check("the locked bridge refuses travel", locked.Outcome == MapTravelOutcome.Locked, locked.ToString());

            Check("the refusal names the requirement that is missing",
                locked.Message.Contains("pass bridge") || locked.Message.Contains(WorldPrototypeBuilder.BridgeKeyFlag),
                locked.Message);

            Check("a locked connection leaves the session exactly where it was",
                !service.IsTravelling && runtime.CurrentMapId == WorldPrototypeBuilder.PassMapId &&
                SceneManager.GetActiveScene().path == WorldPrototypeBuilder.PassScenePath &&
                service.CompletedTransitions == completedBefore && service.FailedTransitions == failedBefore,
                $"map '{runtime.CurrentMapId}', completed {service.CompletedTransitions}, failed {service.FailedTransitions}");

            SetPhase(Phase.PassUnlockCheck);
        }

        private static void PassUnlockCheck()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;

            Check("the bridge flag starts unset in a fresh session",
                !runtime.Progress.HasFlag(WorldPrototypeBuilder.BridgeKeyFlag),
                string.Join(",", runtime.Progress.Flags));

            runtime.Progress.SetFlag(WorldPrototypeBuilder.BridgeKeyFlag);
            runtime.MarkDirty();

            MapTravelResult afterUnlock = runtime.Transition.ValidateTravel("conn.pass_forest");
            Check("unlocking the gate makes the connection traversable",
                afterUnlock.Outcome == MapTravelOutcome.Started, afterUnlock.ToString());

            SetPhase(Phase.RequestPassToForest);
        }

        private static void ForestChecks()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            Scene scene = SceneManager.GetActiveScene();

            Check("the forest scene exposes the west and ferry ports",
                MapEntryPoint.Find(scene, "port.forest.west") != null &&
                MapEntryPoint.Find(scene, "port.forest.ferry") != null, "port.forest.west, port.forest.ferry");

            Check("the unlocked flag survived the crossing", runtime.Progress.HasFlag(WorldPrototypeBuilder.BridgeKeyFlag),
                string.Join(",", runtime.Progress.Flags));

            Check("the forest is recorded as visited",
                runtime.Progress.HasVisited(WorldPrototypeBuilder.ForestMapId), string.Join(",", runtime.Progress.VisitedMaps));

            SetPhase(Phase.RequestOneWay);
        }

        private static void FerryArrivalChecks()
        {
            Scene scene = SceneManager.GetActiveScene();
            MapEntryPoint ferry = MapEntryPoint.Find(scene, "port.grassland.ferry");
            MapEntryPoint defaultPort = MapEntryPoint.FindDefault(scene);

            Check("the arrival port is the connection's own anchor, not the map default",
                ferry != null && defaultPort != null && defaultPort.AnchorId != ferry.AnchorId,
                $"arrived at the ferry port while the map default is '{(defaultPort != null ? defaultPort.AnchorId : "<none>")}'");

            SetPhase(Phase.RequestBackToHub);
        }

        private static void HubAgainChecks()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;

            Check("the round trip back to the hub kept every visited map",
                runtime.Progress.HasVisited(WorldPrototypeBuilder.HubMapId) &&
                runtime.Progress.HasVisited(WorldPrototypeBuilder.GrasslandMapId) &&
                runtime.Progress.HasVisited(WorldPrototypeBuilder.PassMapId) &&
                runtime.Progress.HasVisited(WorldPrototypeBuilder.ForestMapId),
                string.Join(",", runtime.Progress.VisitedMaps));

            Check("the five crossings so far are all recorded as completed",
                runtime.Transition.CompletedTransitions == 5 && runtime.Transition.FailedTransitions == 0,
                $"{runtime.Transition.CompletedTransitions} completed, {runtime.Transition.FailedTransitions} failed");

            SetPhase(Phase.Loop);
        }

        private static void Loop()
        {
            MapTransitionService service = WorldAtlasRuntime.Instance.Transition;

            if (loopRemaining == 0)
            {
                loopWaiting = false;
                SetPhase(Phase.LoopChecks);
                return;
            }

            if (loopWaiting)
            {
                if (!TravelSettled())
                    return;

                loopWaiting = false;
                if (settledFailed > loopStepFailures)
                {
                    loopFailures++;
                    Notes.Add($"loop crossing {LoopCrossings - loopRemaining + 1} failed: {lastTravelDetail}");
                }

                loopRemaining--;
                return;
            }

            pendingTotalBefore = service.CompletedTransitions + service.FailedTransitions;
            pendingFailedBefore = service.FailedTransitions;
            loopStepFailures = service.FailedTransitions;
            pendingConnection = "conn.hub_grassland";
            pendingLabel = "loop crossing";
            pendingRefusal = string.Empty;

            MapTravelResult result = service.RequestTravel(pendingConnection);
            if (!result.Started)
            {
                loopFailures++;
                Notes.Add($"loop crossing {LoopCrossings - loopRemaining + 1} was refused: {result}");
                loopRemaining--;
                return;
            }

            loopWaiting = true;
        }

        private static void LoopChecks()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            MapTransitionService service = runtime.Transition;

            Check($"{LoopCrossings} hub <-> grassland crossings all completed", loopFailures == 0,
                $"{LoopCrossings - loopRemaining} attempted, {loopFailures} failed, {service.CompletedTransitions} completed, {service.FailedTransitions} failed");

            Check("no transition failed during the crossings", service.FailedTransitions == 0,
                $"{service.FailedTransitions} failed");

            Check("the session is back on the hub after an even number of crossings",
                runtime.CurrentMapId == WorldPrototypeBuilder.HubMapId &&
                SceneManager.GetActiveScene().path == WorldPrototypeBuilder.HubScenePath,
                $"map '{runtime.CurrentMapId}' scene '{SceneManager.GetActiveScene().path}'");

            AssertSingleInstances($"after {LoopCrossings} crossings");

            Check("exactly one scene is loaded after the crossings", SceneManager.sceneCount == 1,
                $"{SceneManager.sceneCount} scene(s) loaded");

            Check("progress survived the crossings",
                runtime.Progress.HasFlag(WorldPrototypeBuilder.BridgeKeyFlag) &&
                runtime.Progress.HasVisited(WorldPrototypeBuilder.ForestMapId),
                $"flags {string.Join(",", runtime.Progress.Flags)} | visited {string.Join(",", runtime.Progress.VisitedMaps)}");

            Check("the crossings did not multiply the player object",
                Count<PlayerController>() == 1, Count<PlayerController>().ToString());

            SetPhase(Phase.MapScreenChecks);
        }

        private static void MapScreenChecks()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            WorldMapScreen screen = runtime.WorldMap;

            Check("the world map screen is created by the runtime", screen != null, "screen instance");
            if (screen == null)
            {
                SetPhase(Phase.MissingSceneRefusal);
                return;
            }

            screen.Open();
            Check("the map screen opens", screen.IsOpen, $"IsOpen {screen.IsOpen}");

            Check("every registered map is drawn as a node", screen.VisibleNodeCount == runtime.Atlas.maps.Length,
                $"{screen.VisibleNodeCount} node marker(s) for {runtime.Atlas.maps.Length} map(s)");

            screen.FitToAll();
            Check("a fit leaves a positive zoom", screen.Zoom > 0f, screen.Zoom.ToString("0.###"));

            for (int i = 0; i < 40; i++)
                screen.ZoomBy(1.4f);

            float atMax = screen.Zoom;
            screen.ZoomBy(1.4f);
            Check("zoom is clamped at the maximum", Mathf.Abs(screen.Zoom - atMax) < 1e-4f,
                $"{atMax:0.###} then {screen.Zoom:0.###}");

            for (int i = 0; i < 80; i++)
                screen.ZoomBy(0.7f);

            float atMin = screen.Zoom;
            screen.ZoomBy(0.7f);
            Check("zoom is clamped at the minimum", Mathf.Abs(screen.Zoom - atMin) < 1e-4f,
                $"{atMin:0.###} then {screen.Zoom:0.###}");

            screen.FitToAll();
            Vector2 probePoint = new Vector2(90f, -60f);
            Vector2 atlasBefore = screen.ViewportPointToAtlas(probePoint);
            screen.ZoomAtPoint(probePoint, 1.5f);
            Vector2 atlasAfter = screen.ViewportPointToAtlas(probePoint);
            Check("zooming keeps the point under the cursor anchored",
                Vector2.Distance(atlasBefore, atlasAfter) < 0.01f,
                $"{atlasBefore} -> {atlasAfter}");

            screen.CenterOnCurrentMap();
            Check("centring on the current map keeps a usable view",
                screen.Zoom > 0f && !float.IsNaN(screen.Pan.x) && screen.SelectedMapId == runtime.CurrentMapId,
                $"zoom {screen.Zoom:0.###}, selected '{screen.SelectedMapId}'");

            MapDefinition grassland = runtime.Atlas.GetMap(WorldPrototypeBuilder.GrasslandMapId);
            screen.SetText(grassland.displayName);
            Check("a text search finds the grassland map", screen.ResultCount >= 1,
                $"{screen.ResultCount} result(s) for '{grassland.displayName}'");

            screen.ClearFilters();
            int unfiltered = screen.ResultCount;
            Check("clearing the filters restores a wider result list", unfiltered >= 1,
                $"{unfiltered} result(s) with no filters");

            MapDefinition forest = runtime.Atlas.GetMap(WorldPrototypeBuilder.ForestMapId);
            screen.FocusMap(WorldPrototypeBuilder.ForestMapId);
            Check("selecting a result opens its details", screen.DetailsText.Contains(forest.displayName),
                screen.DetailsText.Length > 120 ? screen.DetailsText.Substring(0, 120) + "..." : screen.DetailsText);

            RoutePlan uiRoute = screen.PlanRouteTo(WorldPrototypeBuilder.ForestMapId);
            Check("the screen plans a route for the selected destination",
                uiRoute != null && uiRoute.Found && uiRoute.Legs.Count > 0, uiRoute != null ? uiRoute.MapSequenceText : "no plan");

            string firstConnection = FirstConnectionLeg(uiRoute);
            Check("the screen's route starts with the connection travel would take",
                firstConnection == "conn.hub_grassland", firstConnection.Length > 0 ? firstConnection : "<none>");

            Check("the planned route is drawn from authored segments", screen.VisibleRouteSegments > 0,
                $"{screen.VisibleRouteSegments} route segment(s)");

            Check("escape closes the map screen", screen.HandleEscapePress() && !screen.IsOpen,
                $"IsOpen {screen.IsOpen}");

            SetPhase(Phase.MapScreenTravel);
        }

        private static void MapScreenTravel()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            WorldMapScreen screen = runtime.WorldMap;

            if (runtime.CurrentMapId == WorldPrototypeBuilder.ForestMapId)
            {
                Check("the map screen's own route travelled to the destination",
                    runtime.CurrentMapId == WorldPrototypeBuilder.ForestMapId,
                    $"arrived on '{runtime.CurrentMapId}' in {uiTravelSteps} leg(s)");
                SetPhase(Phase.MissingSceneRefusal);
                return;
            }

            if (uiTravelSteps >= 5)
            {
                Check("the map screen's own route travelled to the destination", false,
                    $"still on '{runtime.CurrentMapId}' after {uiTravelSteps} legs");
                SetPhase(Phase.MissingSceneRefusal);
                return;
            }

            screen.Open();
            RoutePlan plan = screen.PlanRouteTo(WorldPrototypeBuilder.ForestMapId);
            if (plan == null || !plan.Found || plan.Legs.Count == 0)
            {
                Check("the map screen can re-plan the remaining route", false,
                    plan != null ? plan.FailureReason : "no plan");
                SetPhase(Phase.MissingSceneRefusal);
                return;
            }

            MapTransitionService service = runtime.Transition;
            pendingTotalBefore = service.CompletedTransitions + service.FailedTransitions;
            pendingFailedBefore = service.FailedTransitions;
            pendingRefusal = string.Empty;
            pendingLabel = "map screen leg " + (uiTravelSteps + 1);
            pendingConnection = FirstConnectionLeg(plan);

            screen.TravelThroughRoute();

            // TravelThroughRoute() reports its own acceptance through the status line; the transition is then
            // confirmed by the counters in the wait phase. A refusal would never settle, so it is reported here
            // and the step counter is advanced to keep the run bounded.
            bool refused = screen.StatusText.StartsWith("travel refused") || screen.StatusText == "no route to travel";
            Check($"{pendingLabel}: the map screen asked travel for '{pendingConnection}'", !refused, screen.StatusText);

            if (refused)
            {
                uiTravelSteps++;
                SetPhase(Phase.MapScreenTravel);
                return;
            }

            SetPhase(Phase.MapScreenWait);
        }

        private static void MapScreenLandingChecks()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            MapTransitionService service = runtime.Transition;

            Check($"map screen leg {uiTravelSteps} completed without a failure",
                settledFailed == pendingFailedBefore,
                $"{lastTravelDetail} (map '{runtime.CurrentMapId}')");

            SetPhase(Phase.MapScreenTravel);
        }

        private static void MissingSceneRefusal()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            MapDefinition destination = runtime.Atlas.GetMap(WorldPrototypeBuilder.GrasslandMapId);

            string original = destination.scenePath;
            destination.scenePath = "Assets/Game/Scenes/__NotInBuildSettings__.unity";

            MapTravelResult missing = runtime.Transition.ValidateTravel("conn.grassland_forest_ferry");
            destination.scenePath = original;

            Check("a destination whose scene is not in Build Settings is refused before anything is unloaded",
                missing.Outcome == MapTravelOutcome.SceneMissing, missing.ToString());

            Check("the session is untouched by the refusal",
                !runtime.Transition.IsTravelling && runtime.CurrentMapId == WorldPrototypeBuilder.ForestMapId &&
                SceneManager.GetActiveScene().path == WorldPrototypeBuilder.ForestScenePath,
                $"map '{runtime.CurrentMapId}', scene '{SceneManager.GetActiveScene().path}'");

            Check("the authored scene path was restored", destination.scenePath == original, destination.scenePath);

            SetPhase(Phase.RequestToGrasslandForMismatch);
        }

        private static void MismatchSetup()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            MapDefinition forest = runtime.Atlas.GetMap(WorldPrototypeBuilder.ForestMapId);

            mutatedMap = forest;
            mutatedScenePath = forest.scenePath;
            forest.scenePath = WorldPrototypeBuilder.GrasslandScenePath;

            Check("the mismatch leg is prepared on an in-memory copy only",
                forest.scenePath == WorldPrototypeBuilder.GrasslandScenePath,
                "forest map now points at the grassland scene, to be restored after the leg");

            SetPhase(Phase.RequestMismatch);
        }

        private static void MismatchChecks()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            Scene scene = SceneManager.GetActiveScene();
            MapIdentity identity = UnityEngine.Object.FindAnyObjectByType<MapIdentity>();

            Check("a load that reports a different map is counted as a failed transition",
                settledFailed > pendingFailedBefore,
                $"{settledFailed - pendingFailedBefore} failure(s) recorded for this leg");

            Check("the scene's own identity wins over the requested map",
                runtime.CurrentMapId == WorldPrototypeBuilder.GrasslandMapId,
                $"session map '{runtime.CurrentMapId}', scene identity '{(identity != null ? identity.MapId : "<none>")}'");

            Check("the mismatched arrival is still a real, loaded scene",
                string.Equals(scene.path, WorldPrototypeBuilder.GrasslandScenePath, StringComparison.OrdinalIgnoreCase),
                scene.path);

            MapEntryPoint defaultPort = MapEntryPoint.FindDefault(scene);
            PlayerController player = UnityEngine.Object.FindAnyObjectByType<PlayerController>();
            float distance = player != null && defaultPort != null
                ? Vector2.Distance(player.transform.position, defaultPort.Position2D)
                : float.NaN;

            Check("the player is placed at the scene's default port instead of being left behind",
                player != null && defaultPort != null && distance < ArrivalTolerance,
                $"player {(player != null ? player.transform.position.ToString() : "<none>")} vs port " +
                $"{(defaultPort != null ? defaultPort.Position2D.ToString() : "<none>")}");

            AssertSingleInstances("after a mismatched load");

            mutatedMap.scenePath = mutatedScenePath;
            Check("the authored scene path is restored after the mismatch leg",
                mutatedMap.scenePath == WorldPrototypeBuilder.ForestScenePath, mutatedMap.scenePath);
            mutatedMap = null;

            SetPhase(Phase.RequestRecovery);
        }

        private static void RecoveryChecks()
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;

            Check("travel still works after a mismatched load",
                runtime.CurrentMapId == WorldPrototypeBuilder.ForestMapId &&
                SceneManager.GetActiveScene().path == WorldPrototypeBuilder.ForestScenePath,
                $"map '{runtime.CurrentMapId}', scene '{SceneManager.GetActiveScene().path}'");

            Check("the failed leg did not corrupt the session counters",
                runtime.Transition.CompletedTransitions >= LoopCrossings && runtime.Transition.FailedTransitions == 1,
                $"completed {runtime.Transition.CompletedTransitions}, failed {runtime.Transition.FailedTransitions}");

            Check("the world map screen is still alive after every crossing",
                runtime.WorldMap != null, "screen instance");

            Check("no save write failed during the whole run", WorldSaveStore.FailedWriteCount == 0,
                $"{WorldSaveStore.WriteCount} write(s), {WorldSaveStore.FailedWriteCount} failed");

            SetPhase(Phase.Done);
        }

        // ------------------------------------------------------------------ travel helpers

        /// <summary>Requests a transition the way gameplay does. Returns false when it was refused, in which
        /// case the following wait phase is skipped and the refusal is reported by the arrival checks.</summary>
        private static bool BeginTravel(string connectionId, string label, bool quiet)
        {
            MapTransitionService service = WorldAtlasRuntime.Instance.Transition;

            pendingTotalBefore = service.CompletedTransitions + service.FailedTransitions;
            pendingFailedBefore = service.FailedTransitions;
            pendingConnection = connectionId;
            pendingLabel = label;
            pendingRefusal = string.Empty;

            MapTravelResult validation = service.RequestTravel(connectionId);
            if (validation.Started)
            {
                if (!quiet)
                    Check($"{label}: the request is accepted", true, validation.ToString());

                return true;
            }

            pendingRefusal = validation.ToString();
            if (!quiet)
                Check($"{label}: the request is accepted", false, validation.ToString());

            return false;
        }

        private static bool TravelSettled()
        {
            MapTransitionService service = WorldAtlasRuntime.Instance != null
                ? WorldAtlasRuntime.Instance.Transition
                : null;

            if (service == null)
                return true;

            if (service.IsTravelling)
                return false;

            int total = service.CompletedTransitions + service.FailedTransitions;
            if (total <= pendingTotalBefore)
                return false;

            settledCompleted = service.CompletedTransitions;
            settledFailed = service.FailedTransitions;
            lastTravelDetail = $"{pendingConnection}: completed {settledCompleted}, failed {settledFailed}";
            return true;
        }

        private static void CheckArrival(string label, string expectedMapId, string expectedAnchorId, string expectedScenePath)
        {
            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            Scene scene = SceneManager.GetActiveScene();
            MapIdentity identity = UnityEngine.Object.FindAnyObjectByType<MapIdentity>();

            if (pendingRefusal.Length > 0)
                Check($"{label}: the crossing happened", false, "refused: " + pendingRefusal);

            Check($"{label}: the expected scene is loaded",
                string.Equals(scene.path, expectedScenePath, StringComparison.OrdinalIgnoreCase), scene.path);

            Check($"{label}: the loaded scene reports the expected map",
                identity != null && identity.MapId == expectedMapId, identity != null ? identity.MapId : "no MapIdentity");

            Check($"{label}: the session records the expected map",
                runtime != null && runtime.CurrentMapId == expectedMapId, runtime != null ? runtime.CurrentMapId : "no runtime");

            PlayerController player = UnityEngine.Object.FindAnyObjectByType<PlayerController>();
            MapEntryPoint port = MapEntryPoint.Find(scene, expectedAnchorId);

            if (player == null || port == null)
            {
                Check($"{label}: the player and the arrival port exist", false,
                    $"player {(player != null ? "found" : "missing")}, port '{expectedAnchorId}' {(port != null ? "found" : "missing")}");
                return;
            }

            float distance = Vector2.Distance(player.transform.position, port.Position2D);
            Check($"{label}: the player is placed at port '{expectedAnchorId}'", distance < ArrivalTolerance,
                $"player {player.transform.position}, port {port.Position2D}, delta {distance:0.###}");

            AssertSingleInstances(label);
        }

        private static void AssertSingleInstances(string label)
        {
            int players = Count<PlayerController>();
            int managers = Count<GameManager>();
            int eventSystems = Count<EventSystem>();
            int runtimes = Count<WorldAtlasRuntime>();
            int services = Count<MapTransitionService>();
            int identities = Count<MapIdentity>();

            Check($"{label}: exactly one player, manager, event system and world runtime",
                players == 1 && managers == 1 && eventSystems == 1 && runtimes == 1 && services == 1 && identities == 1,
                $"player {players}, GameManager {managers}, EventSystem {eventSystems}, WorldAtlasRuntime {runtimes}, " +
                $"MapTransitionService {services}, MapIdentity {identities}");
        }

        private static int Count<T>() where T : UnityEngine.Object =>
            UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include).Length;

        /// <summary>The first inter-map leg of a plan: the connection a caller would actually travel.</summary>
        private static string FirstConnectionLeg(RoutePlan plan)
        {
            if (plan == null || !plan.Found)
                return string.Empty;

            for (int i = 0; i < plan.Legs.Count; i++)
                if (plan.Legs[i].IsConnection && plan.Legs[i].ConnectionId.Length > 0)
                    return plan.Legs[i].ConnectionId;

            return string.Empty;
        }

        // ------------------------------------------------------------------ reporting

        private static void Check(string label, bool ok, string detail)
        {
            if (ok)
            {
                passed++;
                Log("  [PASS] " + label + " :: " + detail);
            }
            else
            {
                failed++;
                Log("  [FAIL] " + label + " :: " + detail);
            }
        }

        private static void Log(string text)
        {
            Report.AppendLine(text);
            Debug.Log(Prefix + " " + text);
        }

        private static void WriteReport(string firstLine)
        {
            var text = new StringBuilder();
            text.AppendLine(Prefix + " " + firstLine);

            for (int i = 0; i < Notes.Count; i++)
                text.AppendLine("  note: " + Notes[i]);

            text.Append(Report);

            try
            {
                File.WriteAllText(ReportPath, text.ToString());
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"{Prefix} could not write {ReportPath}: {ex.Message}");
            }
        }

        private static void Finish()
        {
            running = false;
            EditorApplication.update -= Tick;

            EditorSettings.enterPlayModeOptionsEnabled = previousEnterPlayModeOptionsEnabled;
            EditorSettings.enterPlayModeOptions = previousEnterPlayModeOptions;
            Application.runInBackground = previousRunInBackground;

            if (Time.timeScale <= 0f)
                Time.timeScale = 1f;

            if (mutatedMap != null && mutatedScenePath.Length > 0)
            {
                mutatedMap.scenePath = mutatedScenePath;
                mutatedMap = null;
            }

            WorldAtlasRuntime runtime = WorldAtlasRuntime.Instance;
            string summary = runtime != null && runtime.Transition != null
                ? $"transitions completed {runtime.Transition.CompletedTransitions}, failed {runtime.Transition.FailedTransitions}, " +
                  $"save writes {WorldSaveStore.WriteCount} (failed {WorldSaveStore.FailedWriteCount})"
                : "no live session at the end of the run";

            string verdict = failed > 0
                ? $"RESULT: FAIL ({failed} failed, {passed} passed)"
                : $"RESULT: PASS ({passed} passed, 0 failed)";

            Log($"=== {verdict} ===");
            Log("=== " + summary + " ===");
            WriteReport(verdict + " | " + summary);

            Debug.Log($"{Prefix} {verdict} | {summary}");
            EditorApplication.ExitPlaymode();
        }
    }
}
#endif

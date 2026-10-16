using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using DarkRealms.Core;
using DarkRealms.Enemies;
using DarkRealms.Player;

namespace DarkRealms.EditorTools
{
    /// <summary>
    /// Automated play-mode verification harness. Enters play mode, injects synthetic keyboard
    /// events into the Input System and reports whether the gameplay loop actually works.
    ///
    /// Waits are expressed in game seconds (Time.timeSinceLevelLoad) rather than editor ticks,
    /// because EditorApplication.update fires at a different rate than game frames.
    ///
    /// This is an automated check only. It cannot judge feel, animation quality, camera
    /// framing or audio; those still require hands-on play testing.
    /// </summary>
    public static class PlayModeProbe
    {
        private const string Prefix = "[Probe]";
        private const float TotalTimeout = 240f;

        /// <summary>
        /// Fixed-step tolerance for the coyote window. Eligibility is evaluated once per physics step, so a
        /// legitimate coyote launch can be observed up to one step (0.02 s) after the window boundary. This
        /// is the ONLY slack allowed on the acceptance check; it is not tuned to make a run pass.
        /// </summary>
        private const float CoyoteStepTolerance = 0.02f;

        private enum Phase
        {
            Boot,
            Settle,
            Walk,
            Run,
            Stop,
            Jump,
            Land,
            MeleeFace,
            MeleeStrike,
            MeleeWait,
            EnemyDamageSetup,
            EnemyDamageWait,
            HudCheck,
            CameraCheck,
            CameraBoundsCheck,
            Kill,
            Respawn,

            // --- Phase 7 additions: jump / movement / lock policy -------------------------------
            InputPipelineCheck,
            JumpTapSingle,
            JumpHoldAuto,
            JumpHoldRelease,
            JumpRapidPress,
            JumpBuffer,
            JumpCoyote,
            MoveWhileJumping,
            AttackWhileMoving,
            AttackSpamRecover,
            PauseRecovery,
            KnockbackRecover,
            StaleAfterRespawn,

            Done
        }

        private static readonly StringBuilder Report = new();

        private static Phase phase;
        private static bool phaseInitialized;
        private static float phaseStart;
        private static double lastProgressRealtime;
        private static bool previousEnterPlayModeOptionsEnabled;
        private static EnterPlayModeOptions previousEnterPlayModeOptions;
        private static float levelStart;
        private static bool running;
        private static bool focusedJumpVerification;
        private static bool previousRunInBackground;

        private static PlayerController player;
        private static PlayerHealth playerHealth;
        private static PlayerCombat playerCombat;
        private static Rigidbody2D enemyBody;
        private static RigidbodyType2D enemyOriginalBodyType;
        private static bool sawAttack;
        private static EnemyController[] enemies = System.Array.Empty<EnemyController>();
        private static EnemyController enemy;
        private static Health enemyHealth;
        private static Keyboard keyboard;
        private static Key[] desiredKeys = System.Array.Empty<Key>();

        private static Vector2 settledPosition;
        private static float walkSpeed;
        private static float runSpeed;
        private static float jumpStartY;
        private static float peakY;
        private static float enemyHealthBeforeAttack;
        private static float playerHealthBeforeEnemyAttack;
        private static float cameraBeforeX;
        private static Vector2 respawnPosition;
        private static bool respawnPassed;

        private static PlayerInputReader inputReader;
        private static Collider2D playerCollider;
        private static int groundMaskValue;
        private static bool phaseArmed;
        private static string kbInitial = "none";

        // --- flight sampling (real transitions observed every editor tick) ---
        private static bool prevGrounded;
        private static bool wasFalling;
        private static float lastFixedSample = -1f;
        private static int fixedStepsSincePlace;
        private static int airborneSteps;
        private static bool airRunLift;
        private static int airRelaunchSteps;
        private static bool countingAirSamples;
        private static int airSamplesAfterLaunch;
        private static float airMaxVy;        // peak upward velocity observed in the current airborne run
        private static float takeoffPeakVy;   // peak vy of the most recent counted takeoff
        private static int takeoffs;          // ground -> airborne
        private static int landings;          // airborne -> ground
        private static int airRelaunches;     // launched upward again while still airborne

        // --- per-phase sub state (all reset in SetPhase) ---
        private static bool jumpStarted;
        private static int takeoffsAtRelease;
        private static bool bufferPressed;
        private static int takeoffsAtPress;
        private static float pressGap;
        private static bool coyotePressed;
        private static int coyoteEdgesAtPress;
        private static float coyoteLeaveTime;
        private static float coyoteVyAfter;
        private static bool airborneMarked;
        private static float maxAirSpeed;
        private static float measureStartX;
        private static bool resumeMarked;
        private static float pauseRealStart;
        private static float resumeRealTime;
        private static int takeoffsAtResume;
        private static bool knockbackApplied;
        private static float knockbackStartX;
        private static bool respawnSeen;
        private static int takeoffsAtRespawn;
        private static bool knockbackReported;
        private static float walkStartX;
        private static bool respawnBaselineDone;
        private static float stageStartTime;
        private static float minGap;
        private static float lastFallVy;
        private static float coyoteStartY;
        private static int tapLogged;

        // --- authoritative launch record ---
        // Taken from the controller's own JumpReason/JumpLaunchCount rather than inferred from a
        // velocity threshold, so an unexpected relaunch is attributable instead of guessed at.
        private static int launchCountSeen;
        private static int launchCountAtArm;
        private static int launchIdAtArm;
        private static string launchLog = "";
        private static int jumpEdgesAtArm;
        private static JumpReason lastLaunchReason;

        // --- confirmed-delivery press state (see MomentaryPress) ---
        private static bool rapidDown;
        private static int rapidEdgesSeen;
        private static float rapidNextDown;
        private static float rapidDownStart;
        private static bool bufferReleased;
        private static int bufferEdgesAtPress;
        private static float bufferPressTime;
        private static bool releaseIssued;
        private static float releaseTime;
        private static bool releaseNotObserved;
        private static bool repairAttempted;

        private static string ReportPath =>
            System.IO.Path.GetFullPath(System.IO.Path.Combine(Application.dataPath, "../ProbeReport.txt"));

        [MenuItem("Dark Realms/Run Play Mode Probe")]
        public static void RunProbe() => Run(Phase.Boot, focusedJump: false);

        /// <summary>
        /// Runs ONLY the buffered-jump and coyote-jump verification phases, so that verification can be
        /// closed out in isolation. It uses exactly the same physical sampling, event trace and assertions
        /// as the full probe - it neither skips nor weakens a check.
        /// </summary>
        [MenuItem("Dark Realms/Run Focused Jump Verification")]
        public static void RunFocusedJumpVerification() => Run(Phase.JumpBuffer, focusedJump: true);

        private static void Run(Phase startPhase, bool focusedJump)
        {
            Report.Clear();
            phase = startPhase;
            focusedJumpVerification = focusedJump;
            phaseStart = 0f;
            running = true;
            desiredKeys = System.Array.Empty<Key>();
            respawnPassed = false;

            WriteReport("probe started");

            // Entering play mode normally triggers a domain reload, which wipes the static
            // state this harness runs on and drops the update subscription. Disabling the
            // reload keeps the probe alive; the previous setting is restored in Finish().
            previousEnterPlayModeOptionsEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            previousEnterPlayModeOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions = EnterPlayModeOptions.DisableDomainReload;

            // The project ships with PlayerSettings.runInBackground = false, so an UNFOCUSED Editor
            // stops the player loop. That freezes Time.timeSinceLevelLoad (every phase here waits on
            // game time, so they never advance) and stops the Input System from processing the state
            // events queued in ApplyKeys(), so synthetic input silently never arrives. Both symptoms
            // were observed: ProbeReport.txt recorded "STALLED in phase Settle" with no game-time
            // progress, and DiagState.txt recorded EditorApplication.isFocused = False mid-run. This
            // overrides the flag for the duration of the run ONLY (runtime flag, restored in Finish);
            // the PlayerSettings asset is not modified.
            // NOTE: in the Editor the Application.runInBackground setter writes through to
            // PlayerSettings.runInBackground (in memory). The original project value is captured here
            // and written back on EnteredEditMode via the hook below, so a run - including one that
            // stalls - never leaves the project setting modified.
            previousRunInBackground = PlayerSettings.runInBackground;
            Application.runInBackground = true;
            EditorApplication.playModeStateChanged -= RestoreRunInBackground;
            EditorApplication.playModeStateChanged += RestoreRunInBackground;

            lastProgressRealtime = EditorApplication.timeSinceStartup;

            EditorApplication.update -= Tick;
            EditorApplication.update += Tick;

            Debug.Log($"{Prefix} Entering play mode...");
            EditorApplication.EnterPlaymode();
        }

        private static void SetPhase(Phase next)
        {
            phase = next;
            phaseStart = Time.timeSinceLevelLoad;
            phaseInitialized = false;
            lastProgressRealtime = EditorApplication.timeSinceStartup;

            // Tag every event the controller records from here on with this phase, so a launch can never be
            // attributed to a test that has already finished.
            if (player != null)
                player.TracePhaseId = (int)next;

            // Every per-phase sub flag must start clean, or a later phase would inherit the
            // previous phase's arm state and silently skip its own measurement.
            phaseArmed = false;
            jumpStarted = false;
            bufferPressed = false;
            coyotePressed = false;
            airborneMarked = false;
            maxAirSpeed = 0f;
            resumeMarked = false;
            knockbackApplied = false;
            respawnSeen = false;
            knockbackReported = false;
            respawnBaselineDone = false;

            // Cleared explicitly: an accumulator that is only ever Max()'d (coyoteVyAfter) would
            // otherwise carry a value across phases and could report a false pass.
            coyoteVyAfter = 0f;
            coyoteLeaveTime = 0f;
            pressGap = 0f;
            takeoffsAtPress = 0;
            takeoffsAtRelease = 0;
            takeoffsAtResume = 0;
            takeoffsAtRespawn = 0;
            stageStartTime = 0f;
            minGap = 999f;
            lastFallVy = 0f;
            coyoteStartY = 0f;
            tapLogged = 0;
            rapidDown = false;
            rapidEdgesSeen = 0;
            rapidNextDown = 0f;
            rapidDownStart = 0f;
            bufferReleased = false;
            bufferEdgesAtPress = 0;
            bufferPressTime = 0f;
            releaseIssued = false;
            releaseTime = 0f;
            releaseNotObserved = false;
            lastLaunchReason = JumpReason.None;
            coyoteEdgesAtPress = 0;
            repairAttempted = false;

            FlushProgress();
        }

        /// <summary>Jump press edges the reader has observed during the current phase.</summary>
        private static int JumpEdgesSeen =>
            (inputReader != null ? inputReader.JumpPressCount : 0) - jumpEdgesAtArm;

        /// <summary>
        /// The controller's own recorded launch event: reason, post-launch velocity, grounded/held state and
        /// the input-request age, all captured at the launch itself. This is an event snapshot, not a sample
        /// taken afterwards, so it cannot disagree with what the launch actually did.
        /// </summary>
        private static MotionEvent LaunchEvent()
        {
            if (player == null)
                return default;

            var events = player.TraceEvents;

            for (int i = events.Count - 1; i >= 0; i--)
            {
                // Phase-scoped: an unfiltered search would happily return the previous test's launch,
                // which is exactly how an old event satisfies a new assertion.
                if (events[i].Kind == MotionEventKind.JumpLaunch && events[i].PhaseId == (int)phase)
                    return events[i];
            }

            return default;
        }

        /// <summary>
        /// The PRESS event that CAUSED the given launch, or a default when the launch was not driven by an
        /// input press. Correlation is by the request timestamp the launch itself recorded, so the press is
        /// provably the same request - not merely "the last press that happened before some launch". Both
        /// events must belong to the current phase, so a leftover press cannot satisfy a new test.
        /// </summary>
        private static MotionEvent PressForLaunch(MotionEvent launch)
        {
            if (player == null || launch.Kind != MotionEventKind.JumpLaunch || float.IsNaN(launch.JumpRequestTime))
                return default;

            var events = player.TraceEvents;

            for (int i = events.Count - 1; i >= 0; i--)
            {
                var e = events[i];
                if (e.Kind == MotionEventKind.JumpPress &&
                    e.PhaseId == (int)phase &&
                    Mathf.Abs(e.GameTime - launch.JumpRequestTime) <= 0.001f)
                    return e;
            }

            return default;
        }

        /// <summary>
        /// The most recent event of the given kind recorded in the CURRENT phase, or a default when none was.
        /// Scoping to the phase is what stops an event from an earlier test being read as this test's.
        /// </summary>
        private static MotionEvent LastEventOfKind(MotionEventKind kind)
        {
            if (player == null)
                return default;

            var events = player.TraceEvents;

            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].Kind == kind && events[i].PhaseId == (int)phase)
                    return events[i];
            }

            return default;
        }

        /// <summary>
        /// The most recent event of the given kind recorded in the CURRENT phase at or before the given
        /// game time, or a default when none was. Used to bind an event to the landing/departure that
        /// actually preceded it: a phase can contain several landings, and the later one is not the one
        /// that made a buffered launch eligible.
        /// </summary>
        private static MotionEvent LastEventOfKindBefore(MotionEventKind kind, float gameTime)
        {
            if (player == null)
                return default;

            var events = player.TraceEvents;

            for (int i = events.Count - 1; i >= 0; i--)
            {
                if (events[i].Kind == kind && events[i].PhaseId == (int)phase && events[i].GameTime <= gameTime)
                    return events[i];
            }

            return default;
        }

        /// <summary>Prints the controller's recorded events belonging to the current phase.</summary>
        private static void LinePhaseEvents()
        {
            if (player == null)
                return;

            var events = player.TraceEvents;
            int shown = 0;

            Line($"  --- controller events for phase {(int)phase} ---");

            for (int i = 0; i < events.Count; i++)
            {
                if (events[i].PhaseId != (int)phase)
                    continue;

                Line($"    {events[i].ToLine()}");
                shown++;
            }

            if (shown == 0)
                Line("    (none recorded)");

            Line("  --- end events ---");
        }

        /// <summary>
        /// Keys for a MOMENTARY press: the key stays down until the reader has observed its press edge,
        /// then it is released. Editor update ticks are throttled while the Editor is unfocused, so a
        /// fixed short press window can fall entirely between two Input System updates and be dropped -
        /// which is indistinguishable from lost player input. Holding until the edge is observed makes a
        /// synthetic press deterministic without bypassing the real actions or the reader. Bounded, so a
        /// press that is never observed is reported instead of hanging the phase.
        /// </summary>
        private static Key[] MomentaryPress(Key key, int edgesAlreadySeen, float heldFor, float maxHold)
        {
            if (edgesAlreadySeen > 0 || heldFor >= maxHold)
                return System.Array.Empty<Key>();

            return new[] { key };
        }

        /// <summary>
        /// Enables the controller's bounded motion trace for the phase about to run and records the
        /// launch baseline, so the phase can attribute every launch that happens during it.
        /// </summary>
        private static void ResetTrace()
        {
            if (player == null)
                return;

            player.TraceEnabled = true;
            player.ClearTrace();
            // Re-assert the phase tag at the moment the trace is armed. SetPhase normally sets this, but
            // arming is the point that MUST be right: a focused run that starts on a jump phase (without a
            // SetPhase transition) would otherwise tag its events with the default phase and every
            // phase-scoped lookup would miss them.
            player.TracePhaseId = (int)phase;
            launchCountAtArm = player.JumpLaunchCount;
            launchIdAtArm = player.LastLaunchId;
            launchCountSeen = player.JumpLaunchCount;
            launchLog = "";
            jumpEdgesAtArm = inputReader != null ? inputReader.JumpPressCount : 0;
        }

        /// <summary>Appends the controller's motion trace. Called only for phases that fail.</summary>
        private static void DumpTrace(string label)
        {
            if (player == null)
                return;

            string[] lines = player.DumpTrace().Split('\n');
            int from = Mathf.Max(0, lines.Length - 45);

            Line($"  --- {label}: controller motion trace ({lines.Length - from} of {lines.Length} entries) ---");
            for (int i = from; i < lines.Length; i++)
                Line($"    {lines[i]}");
            Line("  --- end trace ---");
        }

        private static float Elapsed => Time.timeSinceLevelLoad - phaseStart;

        private static void Line(string text) => Report.AppendLine(text);

        private static void FlushProgress() => WriteReport("[in progress]\n" + Report);

        private static void WriteReport(string text)
        {
            try
            {
                System.IO.File.WriteAllText(ReportPath, text);
            }
            catch { }
        }

        private static int CountOccurrences(string text, string token)
        {
            int count = 0;
            int index = 0;

            while ((index = text.IndexOf(token, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += token.Length;
            }

            return count;
        }

        /// <summary>
        /// Restores the project's runInBackground flag once play mode has fully exited. Doing this on
        /// EnteredEditMode (rather than in Finish, which runs while still playing) matters: Unity
        /// re-applies the value snapshotted at play-mode entry when leaving play mode, which would
        /// otherwise re-enable the flag and leave the project setting changed.
        /// </summary>
        private static void RestoreRunInBackground(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredEditMode)
                return;

            EditorApplication.playModeStateChanged -= RestoreRunInBackground;
            PlayerSettings.runInBackground = previousRunInBackground;
            Application.runInBackground = previousRunInBackground;
            Debug.Log($"{Prefix} restored PlayerSettings.runInBackground = {previousRunInBackground}");
        }

        private static void Finish(string verdict)
        {
            running = false;
            desiredKeys = System.Array.Empty<Key>();
            ApplyKeys();
            EditorApplication.update -= Tick;

            EditorSettings.enterPlayModeOptionsEnabled = previousEnterPlayModeOptionsEnabled;
            EditorSettings.enterPlayModeOptions = previousEnterPlayModeOptions;
            Application.runInBackground = previousRunInBackground;

            // Never leave the run in a paused or input-disabled state for the next one.
            if (Time.timeScale <= 0f)
                Time.timeScale = 1f;

            if (inputReader != null)
                inputReader.Enabled = true;

            // Leave no verification-only state behind: the motion trace is a test tool, so it is turned
            // off again once the run is over.
            if (player != null)
            {
                player.TraceEnabled = false;
                player.ClearTrace();
            }

            // Make the verdict derived from the recorded check lines instead of a hand-written string,
            // so a failing check can never be reported as an overall pass.
            string body = Report.ToString();
            int passed = CountOccurrences(body, "[PASS]");
            int failed = CountOccurrences(body, "[FAIL]");
            int skipped = CountOccurrences(body, "[SKIP]");

            string finalVerdict = failed > 0
                ? $"RESULT: FAIL ({failed} failed, {passed} passed, {skipped} skipped)"
                : $"{verdict} ({passed} passed, {skipped} skipped)";

            string text = $"{Prefix} {finalVerdict}\n{Report}";
            Debug.Log(text);
            WriteReport(text);

            EditorApplication.ExitPlaymode();
        }

        /// <summary>Counts real ground/air transitions so jump tests assert on observed physics.</summary>
        private static void SampleFlight()
        {
            // IsGrounded is only ever written in FixedUpdate, so sampling faster than the physics
            // step just re-reads the same value. Sampling once per EDITOR tick turned that into
            // phantom takeoffs/landings (a "takeoff" observed with vy=0 on still-grounded ground).
            // One sample per physics step makes every counted transition real.
            if (Time.fixedTime == lastFixedSample)
                return;

            lastFixedSample = Time.fixedTime;

            if (fixedStepsSincePlace < 8)
                fixedStepsSincePlace++;

            // The controller labels every launch with its reason, so a phantom relaunch is attributable
            // (and distinguishable from a land/contact artefact) instead of inferred from a velocity.
            if (player.JumpLaunchCount != launchCountSeen)
            {
                int delta = player.JumpLaunchCount - launchCountSeen;
                for (int i = 1; i <= delta; i++)
                    launchLog += $" [{(launchCountSeen + i)}) {player.LastJumpReason} @{player.LastJumpTime:F3} " +
                                  $"keysAtLaunch={desiredKeys.Length} heldAtLaunch={(inputReader != null && inputReader.JumpHeld)}]";
                launchCountSeen = player.JumpLaunchCount;
                lastLaunchReason = player.LastJumpReason;

                // Start counting the physics samples this probe actually observes after a launch, so a
                // takeoff the counter reports as 0 while the controller recorded a launch can be explained
                // by sampling instead of blamed on gameplay.
                countingAirSamples = true;
                airSamplesAfterLaunch = 0;

                // A launch also begins a NEW airborne run. A buffered jump lands and launches in the SAME
                // physics step, so this once-per-step ground sample never observes the grounded frame and
                // the fall's step count survives into the jump. Without this reset the `airborneSteps == 5`
                // trigger would never fire and the independent counter would miss a real takeoff (observed:
                // controllerLaunches=1, takeoffs=0). The takeoff still has to be physically observed - five
                // sustained airborne samples with a launch-scale velocity - so it stays an independent check.
                airborneSteps = 0;
                airRunLift = false;
                airMaxVy = 0f;
            }

            bool grounded = player.IsGrounded;
            float vy = player.VerticalVelocity;

            if (grounded)
                countingAirSamples = false;
            else if (countingAirSamples)
                airSamplesAfterLaunch++;

            // A short airborne run is a contact-resolution artefact, not a jump: on a hard landing the
            // solver can separate the capsule from the floor for a few steps while the contact is
            // resolved, which looks exactly like a takeoff-and-landing pair. Requiring BOTH a
            // launch-scale velocity AND a sustained airborne run filters that out while still catching
            // every real jump, which keeps the player airborne for dozens of steps.
            if (grounded)
            {
                if (airborneSteps >= 3) landings++;
                airborneSteps = 0;
                airRunLift = false;
                airMaxVy = 0f;
            }
            else
            {
                airborneSteps++;

                // A TAKEOFF means leaving the ground under real upward power. A jump exits at
                // ~jumpVelocity; a landing artefact separates with only a fraction of that. Requiring
                // half the configured launch speed is what separates a real launch from contact noise.
                float launchScale = player.JumpLaunchVelocity * 0.5f;
                if (vy > launchScale) airRunLift = true;
                airMaxVy = Mathf.Max(airMaxVy, vy);

                if (airRunLift && airborneSteps == 5)
                {
                    takeoffs++;
                    takeoffPeakVy = airMaxVy;
                }
            }

            if (!grounded)
            {
                if (vy < -1f)
                {
                    wasFalling = true;
                    airRelaunchSteps = 0;
                }
                else if (wasFalling && vy > 5f)
                {
                    // Launched upward again without ever touching down: an unintended double jump.
                    // Two consecutive upward samples are required, so a single-sample blip in the
                    // sampling (an editor tick that fell between two physics steps) is not mistaken for
                    // an airborne relaunch.
                    airRelaunchSteps++;

                    if (airRelaunchSteps >= 2)
                    {
                        airRelaunches++;
                        wasFalling = false;
                        airRelaunchSteps = 0;
                    }
                }
            }
            else
            {
                wasFalling = false;
                airRelaunchSteps = 0;
            }

            prevGrounded = grounded;
        }

        private static void ResetFlight()
        {
            takeoffs = 0;
            landings = 0;
            airRelaunches = 0;
            wasFalling = false;
            airborneSteps = 0;
            airRelaunchSteps = 0;
            airRunLift = false;
            airMaxVy = 0f;
            prevGrounded = player.IsGrounded;
        }

        private static void SuspendEnemies()
        {
            foreach (var e in enemies)
                if (e != null)
                    e.gameObject.SetActive(false);
        }

        private static void PlacePlayerAt(float x, float y)
        {
            player.transform.position = new Vector2(x, y);
            player.Body.linearVelocity = Vector2.zero;

            // Physics only reflects a teleport on the next step, so until then player.IsGrounded
            // still holds the pre-teleport value. Phases must wait for the physics to catch up.
            fixedStepsSincePlace = 0;
        }

        /// <summary>True once the physics state reflects the most recent teleport.</summary>
        private static bool PhysicsSettled => fixedStepsSincePlace >= 2;

        private static float FeetWorldY() =>
            playerCollider != null ? playerCollider.bounds.min.y : player.transform.position.y;

        /// <summary>Top of the first Ground-layer surface below the given x, or a low fallback.</summary>
        private static float GroundTopBelow(float x)
        {
            var hit = Physics2D.Raycast(new Vector2(x, player.transform.position.y + 6f), Vector2.down, 40f, groundMaskValue);
            return hit.collider != null ? hit.point.y : player.transform.position.y - 50f;
        }

        private static void ApplyKeys()
        {
            // Resolve ONE device and then keep it current. The project's actions resolve against
            // Keyboard.current, which can change while the (unfocused) Editor gains/loses focus, so
            // queuing to a stale device makes presses vanish. Re-resolving every tick is NOT the fix:
            // that oscillates between devices and strands the actions, leaving input permanently dead.
            if (keyboard == null || !keyboard.added)
                keyboard = Keyboard.current ?? InputSystem.AddDevice<Keyboard>();

            if (keyboard == null)
                return;

            // The Input System disables non-background devices when the application loses OS focus
            // (InputSystem.settings.backgroundBehavior). A disabled device silently swallows synthetic
            // state events, which is indistinguishable from lost player input. Re-enabling the harness's
            // own device is device management, not a gameplay or acceptance change.
            if (!keyboard.enabled)
                InputSystem.EnableDevice(keyboard);

            if (Keyboard.current != keyboard)
                keyboard.MakeCurrent();

            InputSystem.QueueStateEvent(keyboard, new KeyboardState(desiredKeys));
        }

        /// <summary>Teleports the player to a clear stretch of ground and kills its momentum.</summary>
        private static void PlacePlayer(float x) => PlacePlayerAt(x, 0.05f);

        private static void Tick()
        {
            if (!running || !EditorApplication.isPlaying)
                return;

            // Real-time watchdog FIRST, so a paused player loop can never hang this harness silently.
            // A stall is never a pass: it is reported and the run ends.
            if (EditorApplication.timeSinceStartup - lastProgressRealtime > 25.0)
            {
                Line($"STALLED in phase {phase} (no phase progress for 25s of real time)");
                Line($"  evidence: focus={EditorApplication.isFocused} runInBackground={Application.runInBackground} " +
                     $"gameTime={Time.timeSinceLevelLoad:F2}s timeScale={Time.timeScale} paused={EditorApplication.isPaused}");
                Finish("RESULT: FAIL (stalled)");
                return;
            }

            if (player == null)
            {
                player = Object.FindAnyObjectByType<PlayerController>();
                if (player == null)
                    return;

                playerHealth = player.GetComponent<PlayerHealth>();
                playerCombat = player.GetComponent<PlayerCombat>();
                // Non-deprecated overload: same semantics (active objects only, unsorted).
                enemies = Object.FindObjectsByType<EnemyController>();
                enemy = enemies.Length > 0 ? enemies[0] : null;
                enemyHealth = enemy != null ? enemy.GetComponent<Health>() : null;
                keyboard = Keyboard.current ?? InputSystem.AddDevice<Keyboard>();

                inputReader = player.GetComponent<PlayerInputReader>();
                playerCollider = player.GetComponent<Collider2D>();

                // Read the ground mask from the component so the probe's ground-truth raycast uses
                // exactly the same layers the controller probes.
                var playerSerialized = new SerializedObject(player);
                var maskProperty = playerSerialized.FindProperty("groundMask");
                groundMaskValue = maskProperty != null ? maskProperty.intValue : LayerMask.GetMask("Ground");

                // Baseline the controller's launch counter so the very first phase attributes correctly.
                launchCountSeen = player.JumpLaunchCount;

                levelStart = Time.timeSinceLevelLoad;
            }

            if (Time.timeScale <= 0f)
                Time.timeScale = 1f;

            float totalElapsed = Time.timeSinceLevelLoad - levelStart;
            if (totalElapsed > TotalTimeout)
            {
                Line($"TIMEOUT in phase {phase} after {totalElapsed:F1}s of game time");
                Finish("RESULT: FAIL (timeout)");
                return;
            }

            ApplyKeys();

            // Sample the real grounded/velocity transitions once per editor tick. Assertions are made
            // at the reader/physics layer this way, never by re-deriving controller math.
            SampleFlight();

            if (playerCombat != null && playerCombat.IsAttacking)
                sawAttack = true;

            RunPhase();
        }

        private static void RunPhase()
        {
            switch (phase)
            {
                case Phase.Boot:
                    // Take every test enemy offline for the movement/jump checks so their aggro
                    // and attacks cannot perturb the locomotion measurements. They are switched
                    // back on for the melee test.
                    foreach (var e in enemies)
                        e.gameObject.SetActive(false);
                    Line(enemies.Length > 0
                        ? $"Boot: {enemies.Length} test enemy/ies disabled for locomotion checks"
                        : "Boot: no enemy found");
                    SetPhase(Phase.Settle);
                    break;

                // ------------------------------------------------------------------ settle
                case Phase.Settle:
                    if (Elapsed < 1f)
                        return;

                    settledPosition = player.transform.position;
                    Line($"Settle: pos=({settledPosition.x:F2},{settledPosition.y:F2}) grounded={player.IsGrounded} state={player.State}");
                    Line(player.IsGrounded && Mathf.Abs(settledPosition.y) < 0.6f
                        ? "  [PASS] player rests on the ground"
                        : "  [FAIL] player did not settle on the ground");

                    if (enemies.Length > 0)
                    {
                        var stillActive = false;
                        foreach (var e in enemies)
                            stillActive |= e.gameObject.activeInHierarchy;
                        Line(stillActive ? "  [WARN] an enemy is still active" : "  [OK] all enemies suspended");
                    }
                    SetPhase(Phase.Walk);
                    break;

                // ------------------------------------------------------------------ walk
                // Locomotion runs on a deliberately clear stretch of ground (x -9..0) so solid
                // props such as the crate at x=8 cannot stop the player and skew the measurement.
                case Phase.Walk:
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        PlacePlayer(-9f);
                        return;
                    }

                    desiredKeys = new[] { Key.D };
                    if (Elapsed < 1.2f)
                        return;

                    walkSpeed = player.HorizontalSpeed;
                    float walkedTo = player.transform.position.x;
                    Line($"Walk: x -9.00 -> {walkedTo:F2} (moved {walkedTo + 9f:F2}) speed={walkSpeed:F2} state={player.State}");
                    Line(walkedTo + 9f > 4f && player.State == PlayerState.Walk
                        ? "  [PASS] walking moves the player and reports the Walk state"
                        : "  [FAIL] walking did not move the player correctly");
                    SetPhase(Phase.Run);
                    break;

                // ------------------------------------------------------------------ run
                case Phase.Run:
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        PlacePlayer(30f);
                        return;
                    }

                    desiredKeys = new[] { Key.D, Key.LeftShift };
                    if (Elapsed < 0.9f)
                        return;

                    runSpeed = player.HorizontalSpeed;
                    float ranDistance = player.transform.position.x - 30f;
                    Line($"Run: speed={runSpeed:F2} walkSpeed={walkSpeed:F2} state={player.State} distance={ranDistance:F2}");
                    Line(runSpeed > 10f && player.State == PlayerState.Run
                        ? "  [PASS] sprinting reaches run speed and reports the Run state"
                        : "  [FAIL] sprint did not reach run speed");
                    SetPhase(Phase.Stop);
                    break;

                case Phase.Stop:
                    desiredKeys = System.Array.Empty<Key>();
                    if (Elapsed < 0.6f)
                        return;

                    Line($"Stop: speed={player.HorizontalSpeed:F2} grounded={player.IsGrounded}");
                    SetPhase(Phase.Jump);
                    break;

                // ------------------------------------------------------------------ jump
                case Phase.Jump:
                    jumpStartY = player.transform.position.y;
                    peakY = jumpStartY;
                    desiredKeys = new[] { Key.Space };
                    SetPhase(Phase.Land);
                    break;

                case Phase.Land:
                    peakY = Mathf.Max(peakY, player.transform.position.y);

                    // Release jump after a short hold so the jump reaches close to full height.
                    if (Elapsed > 0.14f && desiredKeys.Length > 0)
                        desiredKeys = System.Array.Empty<Key>();

                    if (Elapsed < 1.6f && !(player.IsGrounded && Elapsed > 0.4f))
                        return;

                    Line($"Jump: startY={jumpStartY:F2} peakY={peakY:F2} rise={peakY - jumpStartY:F2}");
                    Line($"Land: y={player.transform.position.y:F2} grounded={player.IsGrounded}");
                    Line(peakY - jumpStartY > 1.5f
                        ? "  [PASS] jump lifts the player"
                        : "  [FAIL] jump did not lift the player");
                    Line(player.IsGrounded
                        ? "  [PASS] player lands back on the ground"
                        : "  [FAIL] player never landed");
                    SetPhase(Phase.MeleeFace);
                    break;

                // ------------------------------------------------------------------ melee
                case Phase.MeleeFace:
                    if (enemy == null || enemyHealth == null)
                    {
                        Line("Melee: [SKIP] no enemy in scene");
                        SetPhase(Phase.Kill);
                        return;
                    }

                    foreach (var e in enemies)
                        e.gameObject.SetActive(true);

                    // One-shot setup: the original body type must only be captured before the
                    // first freeze, otherwise it would record Static and never be restored.
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;

                        enemyBody = enemy.GetComponent<Rigidbody2D>();
                        if (enemyBody != null)
                        {
                            enemyOriginalBodyType = enemyBody.bodyType;
                            enemyBody.bodyType = RigidbodyType2D.Static;
                        }
                    }

                    // Place the knight just right of the enemy, then hold A so facing turns left.
                    Vector2 enemyPos = enemy.transform.position;
                    player.transform.position = new Vector2(enemyPos.x + 1.15f, enemyPos.y + 0.05f);
                    player.Body.linearVelocity = Vector2.zero;
                    enemyHealthBeforeAttack = enemyHealth.CurrentHealth;

                    sawAttack = false;
                    desiredKeys = new[] { Key.A };
                    if (Elapsed < 0.12f)
                        return;

                    Line($"Melee: facing={player.Facing} enemyHealth={enemyHealthBeforeAttack:F0} " +
                         $"enemyActive={enemy.gameObject.activeInHierarchy} dist={Vector2.Distance(player.transform.position, enemy.transform.position):F2}");
                    SetPhase(Phase.MeleeStrike);
                    break;

                case Phase.MeleeStrike:
                    desiredKeys = new[] { Key.Enter };
                    if (Elapsed < 0.2f)
                        return;

                    desiredKeys = System.Array.Empty<Key>();
                    SetPhase(Phase.MeleeWait);
                    break;

                case Phase.MeleeWait:
                    if (Elapsed < 0.8f)
                        return;

                    float after = enemyHealth != null ? enemyHealth.CurrentHealth : 0f;
                    Line($"Melee diag: attackSeen={sawAttack} player=({player.transform.position.x:F2},{player.transform.position.y:F2}) " +
                         $"enemy=({enemy.transform.position.x:F2},{enemy.transform.position.y:F2}) " +
                         $"dist={Vector2.Distance(player.transform.position, enemy.transform.position):F2} facing={player.Facing} " +
                         $"playerState={player.State} enemyState={enemy.State}");
                    Line($"Melee result: enemyHealth {enemyHealthBeforeAttack:F0} -> {after:F0}");
                    Line(after < enemyHealthBeforeAttack
                        ? "  [PASS] player melee attack damages the enemy"
                        : "  [FAIL] player melee attack dealt no damage");

                    if (enemyBody != null)
                        enemyBody.bodyType = enemyOriginalBodyType;

                    SetPhase(Phase.EnemyDamageSetup);
                    break;

                // ------------------------------------------------------------------ enemy damages player
                case Phase.EnemyDamageSetup:
                    // Stand still next to the enemy and let it swing; verifies the enemy attack
                    // pipeline and the player's damage/i-frame handling.
                    playerHealthBeforeEnemyAttack = playerHealth.CurrentHealth;
                    player.transform.position = new Vector2(enemy.transform.position.x + 1.2f, enemy.transform.position.y + 0.05f);
                    player.Body.linearVelocity = Vector2.zero;
                    desiredKeys = System.Array.Empty<Key>();
                    SetPhase(Phase.EnemyDamageWait);
                    break;

                case Phase.EnemyDamageWait:
                    // Keep the knight parked next to the enemy so distance cannot cause a miss.
                    if (playerHealth.CurrentHealth >= playerHealthBeforeEnemyAttack && Elapsed < 6f)
                    {
                        var gap = player.transform.position.x - enemy.transform.position.x;
                        if (Mathf.Abs(gap) > 1.4f)
                        {
                            player.transform.position = new Vector2(enemy.transform.position.x + 1.2f, enemy.transform.position.y + 0.05f);
                            player.Body.linearVelocity = Vector2.zero;
                        }
                        return;
                    }

                    float nowHealth = playerHealth.CurrentHealth;
                    Line($"Enemy damage: playerHealth {playerHealthBeforeEnemyAttack:F0} -> {nowHealth:F0} (enemyState={enemy.State})");
                    Line(nowHealth < playerHealthBeforeEnemyAttack
                        ? "  [PASS] enemy attack damages the player"
                        : "  [FAIL] enemy never damaged the player");
                    SetPhase(Phase.HudCheck);
                    break;

                // ------------------------------------------------------------------ HUD
                case Phase.HudCheck:
                    var fillObject = GameObject.Find("HealthBarFill");
                    var fill = fillObject != null ? fillObject.GetComponent<UnityEngine.UI.Image>() : null;
                    float expectedFill = playerHealth.MaxHealth <= 0f ? 0f : playerHealth.CurrentHealth / playerHealth.MaxHealth;
                    float actualFill = fill != null ? fill.fillAmount : -1f;
                    Line($"HUD: healthBarFill={actualFill:F2} expected={expectedFill:F2} health={playerHealth.CurrentHealth:F0}/{playerHealth.MaxHealth:F0}");
                    Line(fill != null && actualFill < 1f && Mathf.Abs(actualFill - expectedFill) < 0.02f
                        ? "  [PASS] HUD health bar tracks player health"
                        : "  [FAIL] HUD health bar did not track player health");
                    SetPhase(Phase.CameraCheck);
                    break;

                // ------------------------------------------------------------------ camera follow
                case Phase.CameraCheck:
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        cameraBeforeX = Camera.main.transform.position.x;
                        PlacePlayer(40f);
                        return;
                    }

                    if (Elapsed < 1.5f)
                        return;

                    float camX = Camera.main.transform.position.x;
                    float halfW = Camera.main.orthographicSize * Camera.main.aspect;
                    float clampMax = 62f - halfW;

                    // The camera may legitimately stop short of the player when the viewport is
                    // wider than the level bounds allow (the editor Game View is very wide).
                    bool movedTowardPlayer = camX > cameraBeforeX + 5f;
                    bool reachedPlayerOrClamp = Mathf.Abs(camX - 40f) < 6f || camX >= clampMax - 0.6f;

                    Line($"Camera follow: x {cameraBeforeX:F2} -> {camX:F2} (player x=40.00, moved {camX - cameraBeforeX:F2}, clampMax={clampMax:F2})");
                    Line(movedTowardPlayer && reachedPlayerOrClamp
                        ? "  [PASS] camera follows the player horizontally"
                        : "  [FAIL] camera did not follow the player");
                    SetPhase(Phase.CameraBoundsCheck);
                    break;

                case Phase.CameraBoundsCheck:
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        // Teleport past the right-hand bound; the camera must clamp, not follow.
                        player.transform.position = new Vector2(60f, 3.2f);
                        player.Body.linearVelocity = Vector2.zero;
                        return;
                    }

                    if (Elapsed < 1.5f)
                        return;

                    Camera cam = Camera.main;
                    float halfWidth = cam.orthographicSize * cam.aspect;
                    float maxX = 62f - halfWidth + 0.6f;
                    float boundedX = Camera.main.transform.position.x;
                    Line($"Camera bounds: x={boundedX:F2} limit={maxX:F2} (ortho={cam.orthographicSize}, aspect={cam.aspect:F2})");
                    Line(boundedX <= maxX
                        ? "  [PASS] camera respects its configured world bounds"
                        : "  [FAIL] camera exceeded its configured world bounds");
                    SetPhase(Phase.Kill);
                    break;

                // ------------------------------------------------------------------ death + respawn
                case Phase.Kill:
                    // Wait out any invulnerability frames left over from the enemy's hit, otherwise
                    // the scripted lethal blow is correctly ignored and the test reports a false failure.
                    if (playerHealth.IsInvulnerable && Elapsed < 3f)
                        return;

                    var lethal = new DamageInfo(9999f, player.transform.position, null);
                    playerHealth.TakeDamage(in lethal);
                    SetPhase(Phase.Respawn);
                    break;

                case Phase.Respawn:
                    if (Elapsed < 0.1f)
                        return;

                    if (!respawnPassed && !playerHealth.IsAlive)
                    {
                        respawnPassed = true;
                        Line($"Death: health={playerHealth.CurrentHealth:F0} state={player.State}");
                        Line(player.State == PlayerState.Dead
                            ? "  [PASS] lethal damage kills the player"
                            : "  [FAIL] player did not enter the dead state");
                    }

                    // Wait for the respawn coroutine, up to 6 game seconds.
                    if (!playerHealth.IsAlive && Elapsed < 6f)
                        return;

                    respawnPosition = player.transform.position;
                    Line($"Respawn: pos=({respawnPosition.x:F2},{respawnPosition.y:F2}) health={playerHealth.CurrentHealth:F0} alive={playerHealth.IsAlive} state={player.State}");
                    Line(playerHealth.IsAlive && respawnPosition.x < 0f
                        ? "  [PASS] player respawned at the spawn point with full health"
                        : "  [FAIL] respawn did not restore the player");
                    SetPhase(Phase.InputPipelineCheck);
                    break;

                // ================================================================ input pipeline gate
                // Runs immediately before the jump tests. If the synthetic input path is not
                // delivering, every later jump result would be meaningless, so it is checked first and
                // reported explicitly instead of being mistaken for a controller regression.
                case Phase.InputPipelineCheck:
                {
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        SuspendEnemies();
                        PlacePlayer(0f);
                        desiredKeys = System.Array.Empty<Key>();
                        kbInitial = keyboard != null ? keyboard.name : "none";
                        return;
                    }

                    if (!phaseArmed)
                    {
                        phaseArmed = true;
                        stageStartTime = Elapsed;
                        measureStartX = player.transform.position.x;
                        return;
                    }

                    desiredKeys = new[] { Key.D };

                    float window = Elapsed - stageStartTime;
                    bool deviceOk = keyboard != null && keyboard.added && Keyboard.current == keyboard;
                    bool readerSeesMove = inputReader != null && inputReader.MoveX > 0.5f;

                    if (window < 0.5f)
                        return;

                    // Bounded repair: a device the Input System disabled (it does that whenever the app
                    // loses OS focus) swallows synthetic events silently. Re-arm the harness device once and
                    // retry before declaring the run invalid, and report the device state either way so a
                    // dead pipeline can never be mistaken for a gameplay failure.
                    if (!readerSeesMove && deviceOk && !repairAttempted)
                    {
                        repairAttempted = true;

                        if (!keyboard.enabled)
                            InputSystem.EnableDevice(keyboard);

                        keyboard.MakeCurrent();

                        Line($"InputPipelineCheck: no input delivered - re-armed the synthetic keyboard " +
                             $"(deviceEnabled={keyboard.enabled} backgroundBehavior={InputSystem.settings.backgroundBehavior} " +
                             $"appFocused={Application.isFocused})");

                        stageStartTime = Elapsed;
                        measureStartX = player.transform.position.x;
                        return;
                    }

                    if (!readerSeesMove && window < 3f)
                        return;

                    float moved = player.transform.position.x - measureStartX;

                    Line($"InputPipelineCheck: device='{kbInitial}' added={keyboard != null && keyboard.added} " +
                         $"isCurrent={Keyboard.current == keyboard} currentDev='{(Keyboard.current != null ? Keyboard.current.name : "none")}' " +
                         $"enabled={keyboard != null && keyboard.enabled} backgroundBehavior={InputSystem.settings.backgroundBehavior} " +
                         $"appFocused={Application.isFocused} repaired={repairAttempted} " +
                         $"reader.MoveX={(inputReader != null ? inputReader.MoveX : -2f):F2} reader.Enabled={(inputReader != null && inputReader.Enabled)} moved={moved:F2}");
                    Line(deviceOk && readerSeesMove && moved > 0.4f
                        ? "  [PASS] synthetic keyboard input reaches the reader and drives the player"
                        : $"  [FAIL] the input pipeline is NOT delivering (deviceOk={deviceOk}, readerSawMove={readerSeesMove}, moved={moved:F2}) - jump results below are not meaningful");
                    SetPhase(Phase.JumpTapSingle);
                    break;
                }

                // ================================================================== jump policy tests
                // 1) a fresh Space press initiates exactly one jump.
                case Phase.JumpTapSingle:
                {
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        SuspendEnemies();

                        // Start AIRBORNE: the genuine landing is what re-arms auto-jump, so a phase must
                        // never begin from a state where auto-jump is still disarmed (a teleport onto the
                        // ground does not re-arm it, by design).
                        PlacePlayerAt(0f, 0.9f);
                        desiredKeys = System.Array.Empty<Key>();
                        ResetFlight();
                        return;
                    }

                    if (!PhysicsSettled || !player.IsGrounded)
                        return;

                    if (!phaseArmed)
                    {
                        phaseArmed = true;
                        ResetFlight();
                        ResetTrace();
                        stageStartTime = Elapsed;
                        Line($"JumpTapSingle: armed at y={player.transform.position.y:F3} vy={player.VerticalVelocity:F2} state={player.State} locks={player.ActiveLocks}");
                        return;
                    }

                    float tapT = Elapsed - stageStartTime;

                    // One tap, held only until the reader has actually observed the press edge (bounded),
                    // then released for the rest of the phase.
                    desiredKeys = MomentaryPress(Key.Space, JumpEdgesSeen, tapT, 0.5f);

                    if (desiredKeys.Length > 0)
                        return;

                    // Log every observed takeoff with its launch velocity: a real double jump shows
                    // ~jumpVelocity here, a phantom ground-sample glitch shows a small value.
                    if (takeoffs > tapLogged)
                    {
                        tapLogged = takeoffs;
                        Line($"  JumpTapSingle: takeoff #{takeoffs} at t={tapT:F3}s launchPeakVy={takeoffPeakVy:F2} vyNow={player.VerticalVelocity:F2} grounded={player.IsGrounded}");
                    }

                    if (tapT < 2.0f)
                        return;

                    int launches = player.JumpLaunchCount - launchCountAtArm;
                    int edges = (inputReader != null ? inputReader.JumpPressCount : 0) - jumpEdgesAtArm;
                    bool single = takeoffs == 1 && launches == 1 && airRelaunches == 0;

                    Line($"JumpTapSingle: takeoffs={takeoffs} landings={landings} airRelaunches={airRelaunches} " +
                         $"controllerLaunches={launches} pressEdges={edges} reasons={launchLog} grounded={player.IsGrounded} state={player.State}");

                    // Input fidelity first: if one tap produced several press edges the fault is in event
                    // delivery, and the gameplay assertion below could not be trusted either way.
                    Line(edges == 1
                        ? "  [PASS] the reader observed exactly one Space press edge for one tap"
                        : $"  [FAIL] the reader observed {edges} Space press edge(s) for a single tap");
                    Line(single
                        ? "  [PASS] a single Space tap produced exactly one jump"
                        : $"  [FAIL] a single Space tap produced {takeoffs} observed takeoff(s) / {launches} controller launch(es) " +
                          $"and {airRelaunches} airborne relaunch(es); expected exactly 1 jump");

                    if (!single)
                        DumpTrace("JumpTapSingle");
                    Line(landings >= 1
                        ? "  [PASS] the tap jump landed again"
                        : $"  [FAIL] the tap jump never landed (landings={landings})");
                    SetPhase(Phase.JumpHoldAuto);
                    break;
                }

                // 2) + 7) holding Space re-jumps after every genuine landing, never while airborne.
                case Phase.JumpHoldAuto:
                {
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        SuspendEnemies();
                        PlacePlayerAt(-2f, 0.9f);
                        desiredKeys = System.Array.Empty<Key>();
                        return;
                    }

                    if (!PhysicsSettled || !player.IsGrounded)
                        return;

                    if (!phaseArmed)
                    {
                        phaseArmed = true;
                        ResetFlight();
                        ResetTrace();
                        stageStartTime = Elapsed;
                        return;
                    }

                    // Space is held continuously for the whole window.
                    desiredKeys = new[] { Key.Space };

                    if (Elapsed < stageStartTime + 4f)
                        return;

                    desiredKeys = System.Array.Empty<Key>();

                    Line($"JumpHoldAuto: held Space {Elapsed:F1}s -> takeoffs={takeoffs} landings={landings} airRelaunches={airRelaunches}");
                    Line(takeoffs >= 2 && landings >= 1
                        ? "  [PASS] holding Space triggered a new jump after each genuine landing"
                        : $"  [FAIL] holding Space produced {takeoffs} jump(s) / {landings} landing(s); expected repeated jumps");

                    // The auto-hold path consumes no discrete request, so every one of its launches must
                    // record a not-applicable request age - never a stale positive age that would make an
                    // auto-jump look like a buffered press.
                    int autoHoldLaunches = 0;
                    int autoHoldWithRequest = 0;

                    foreach (var e in player.TraceEvents)
                    {
                        if (e.Kind != MotionEventKind.JumpLaunch || e.PhaseId != (int)phase || e.Reason != JumpReason.AutoHold)
                            continue;

                        autoHoldLaunches++;
                        if (!float.IsNaN(e.JumpRequestTime))
                            autoHoldWithRequest++;
                    }

                    LinePhaseEvents();
                    Line(autoHoldLaunches > 0 && autoHoldWithRequest == 0
                        ? $"  [PASS] all {autoHoldLaunches} auto-hold launch(es) recorded request age as not-applicable"
                        : $"  [FAIL] {autoHoldWithRequest} of {autoHoldLaunches} auto-hold launch(es) recorded a discrete request age");
                    Line(airRelaunches == 0
                        ? "  [PASS] holding Space never produced an airborne relaunch (no double jump)"
                        : $"  [FAIL] holding Space produced {airRelaunches} airborne relaunch(es)");
                    SetPhase(Phase.JumpHoldRelease);
                    break;
                }

                // 3) releasing Space before landing prevents the next automatic jump.
                case Phase.JumpHoldRelease:
                {
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        SuspendEnemies();
                        PlacePlayerAt(-2f, 0.9f);
                        desiredKeys = System.Array.Empty<Key>();
                        return;
                    }

                    if (!PhysicsSettled || !player.IsGrounded)
                    {
                        // Bounded: report the evidence rather than letting the phase stall.
                        if (Elapsed > 5f)
                        {
                            Line($"JumpHoldRelease: [FAIL] the controlled drop never landed " +
                                 $"(y={player.transform.position.y:F2} vy={player.VerticalVelocity:F2} state={player.State})");
                            SetPhase(Phase.JumpRapidPress);
                        }

                        return;
                    }

                    if (!phaseArmed)
                    {
                        phaseArmed = true;
                        ResetFlight();
                        ResetTrace();
                        stageStartTime = Elapsed;
                        return;
                    }

                    if (!jumpStarted)
                    {
                        // Hold only until the jump actually fires; then this becomes a release test.
                        // Bounded, so a phase that never gets a takeoff reports a FAIL with evidence
                        // instead of silently burning the real-time watchdog.
                        desiredKeys = new[] { Key.Space };

                        if (takeoffs >= 1)
                        {
                            takeoffsAtRelease = takeoffs;
                            jumpStarted = true;
                            stageStartTime = Elapsed;
                            return;
                        }

                        if (Elapsed > 4f)
                        {
                            Line($"JumpHoldRelease: [FAIL] holding Space never produced a takeoff " +
                                 $"(grounded={player.IsGrounded} y={player.transform.position.y:F2} vy={player.VerticalVelocity:F2} " +
                                 $"state={player.State} locks={player.ActiveLocks})");
                            SetPhase(Phase.JumpRapidPress);
                        }

                        return;
                    }

                    // Airborne onwards: release immediately and never press again.
                    desiredKeys = System.Array.Empty<Key>();

                    if (!releaseIssued)
                    {
                        releaseIssued = true;
                        releaseTime = Elapsed;
                    }

                    // Input-delivery fidelity, checked independently of gameplay: once the harness has
                    // released Space the reader must observe it. If it does not, this test is invalidated by
                    // event delivery and reporting it as a gameplay verdict would be wrong either way.
                    if (!releaseNotObserved && Elapsed > releaseTime + 0.15f && inputReader != null && inputReader.JumpHeld)
                    {
                        releaseNotObserved = true;
                        Line($"JumpHoldRelease: [FAIL] the harness released Space at t={releaseTime:F3}s but the reader still reports it " +
                             $"held at t={Elapsed:F3}s - input delivery, not gameplay, is the failure");
                    }

                    if (!(player.IsGrounded && Elapsed > stageStartTime + 1.2f) && Elapsed < stageStartTime + 6f)
                        return;

                    int extra = takeoffs - takeoffsAtRelease;
                    int launches = player.JumpLaunchCount - launchCountAtArm;
                    int edges = JumpEdgesSeen;
                    Line($"JumpHoldRelease: takeoffs at release={takeoffsAtRelease}, total={takeoffs}, controllerLaunches={launches} " +
                         $"pressEdges={edges} releaseObserved={!releaseNotObserved} reasons={launchLog} " +
                         $"grounded={player.IsGrounded} state={player.State}");
                    Line(releaseNotObserved
                        ? "  [SKIP] the Space release was not observed by the reader, so the release result is not a gameplay verdict"
                        : (takeoffsAtRelease >= 1 && extra == 0
                            ? "  [PASS] releasing Space before landing prevented the next automatic jump"
                            : $"  [FAIL] release test invalid (jumped {takeoffsAtRelease}x before release, {extra} extra after)"));

                    if (!releaseNotObserved && !(takeoffsAtRelease >= 1 && extra == 0))
                        DumpTrace("JumpHoldRelease");
                    SetPhase(Phase.JumpRapidPress);
                    break;
                }

                // 4) rapid press/release does not lose legitimate presses.
                case Phase.JumpRapidPress:
                {
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        SuspendEnemies();
                        PlacePlayerAt(-2f, 0.9f);
                        desiredKeys = System.Array.Empty<Key>();
                        return;
                    }

                    if (!PhysicsSettled || !player.IsGrounded)
                        return;

                    if (!phaseArmed)
                    {
                        phaseArmed = true;
                        ResetFlight();
                        ResetTrace();
                        stageStartTime = Elapsed;
                        return;
                    }

                    // Each press is held until the reader has observed its edge, then released for a short
                    // gap. A fixed 60 ms window can be dropped entirely by a throttled Editor, which is a
                    // harness artefact rather than lost player input.
                    float cycle = Elapsed - stageStartTime;

                    if (!rapidDown && cycle >= rapidNextDown && cycle < 3.5f)
                    {
                        rapidDown = true;
                        rapidDownStart = cycle;
                    }

                    if (rapidDown)
                    {
                        desiredKeys = MomentaryPress(Key.Space, JumpEdgesSeen - rapidEdgesSeen, cycle - rapidDownStart, 0.3f);

                        if (desiredKeys.Length == 0)
                        {
                            rapidDown = false;
                            rapidEdgesSeen = JumpEdgesSeen;
                            rapidNextDown = cycle + 0.06f;
                        }
                    }
                    else
                    {
                        desiredKeys = System.Array.Empty<Key>();
                    }

                    if (Elapsed < stageStartTime + 3.5f)
                        return;

                    desiredKeys = System.Array.Empty<Key>();
                    int issued = Mathf.Max(1, Mathf.FloorToInt((Elapsed - stageStartTime) / 0.12f / 2f));

                    Line($"JumpRapidPress: ~{issued} discrete presses, takeoffs={takeoffs} landings={landings} airRelaunches={airRelaunches} grounded={player.IsGrounded}");
                    Line(takeoffs >= 2 && airRelaunches == 0
                        ? "  [PASS] rapid Space presses each produced a jump without dropping input"
                        : $"  [FAIL] rapid presses produced only {takeoffs} jump(s) (airborne relaunches={airRelaunches})");
                    SetPhase(Phase.JumpBuffer);
                    break;
                }

                // 5) a jump press shortly before landing is buffered and fires on touchdown.
                case Phase.JumpBuffer:
                {
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        SuspendEnemies();

                        // Deterministic controlled drop. No initial jump and Space is never held, so the
                        // JUMP BUFFER is the only mechanism that can produce the jump under test.
                        PlacePlayerAt(-2f, 4f);
                        desiredKeys = System.Array.Empty<Key>();
                        ResetFlight();
                        // Clear the trace and capture the launch baseline NOW, so this phase cannot read a
                        // launch or press left over from a previous test.
                        ResetTrace();
                        minGap = 999f;
                        return;
                    }

                    // Let the teleport reach the physics state before trusting ground detection.
                    if (!PhysicsSettled)
                        return;

                    if (!phaseArmed)
                    {
                        phaseArmed = true;
                        stageStartTime = Elapsed;
                    }

                    if (!bufferPressed)
                    {
                        desiredKeys = System.Array.Empty<Key>();

                        if (player.IsGrounded)
                        {
                            Line($"JumpBuffer: [FAIL] the drop landed before the press window was reached (min gap seen={minGap:F3}u, vy at that point={lastFallVy:F2})");
                            SetPhase(Phase.JumpCoyote);
                            return;
                        }

                        float gap = FeetWorldY() - GroundTopBelow(player.Body.position.x);
                        float closing = Mathf.Max(0.5f, -player.VerticalVelocity);

                        if (gap < minGap)
                        {
                            minGap = gap;
                            lastFallVy = player.VerticalVelocity;
                        }

                        // Press once, inside the jump-buffer window before touchdown.
                        if (player.VerticalVelocity < -1f && gap <= closing * 0.10f)
                        {
                            desiredKeys = new[] { Key.Space };
                            bufferPressed = true;
                            bufferEdgesAtPress = JumpEdgesSeen;
                            bufferPressTime = Elapsed;
                            pressGap = gap;
                            takeoffsAtPress = takeoffs;
                            return;
                        }

                        return;
                    }

                    // Pressed once: hold only until the reader has observed the edge (bounded), then
                    // release and look for the buffered jump as the player touches down.
                    if (!bufferReleased)
                    {
                        desiredKeys = MomentaryPress(Key.Space, JumpEdgesSeen - bufferEdgesAtPress, Elapsed - bufferPressTime, 0.4f);

                        if (desiredKeys.Length > 0)
                            return;

                        bufferReleased = true;
                    }

                    desiredKeys = System.Array.Empty<Key>();

                    if (takeoffs <= takeoffsAtPress && Elapsed < 3f)
                        return;

                    // Correlate by the request the launch itself recorded, with every lookup scoped to this
                    // phase, and require the launch to be NEWER than the phase's arm baseline. A shared
                    // "last press before the last launch" would let a leftover event satisfy a new test.
                    MotionEvent launch = LaunchEvent();
                    MotionEvent press = PressForLaunch(launch);
                    // The touchdown that made the buffered request eligible is the last one AT OR BEFORE the
                    // launch - not simply the last landing in the phase, which may be the landing after it.
                    MotionEvent land = launch.Kind == MotionEventKind.JumpLaunch
                        ? LastEventOfKindBefore(MotionEventKind.Land, launch.GameTime)
                        : default;
                    MotionEvent expired = LastEventOfKind(MotionEventKind.JumpRequestExpired);

                    bool pressInPhase = press.Kind == MotionEventKind.JumpPress;
                    bool landInPhase = land.Kind == MotionEventKind.Land;
                    bool launchInPhase = launch.Kind == MotionEventKind.JumpLaunch;
                    bool freshLaunch = launchInPhase && launch.LaunchId > launchIdAtArm;

                    bool pressBeforeLanding = pressInPhase && landInPhase && press.GameTime <= land.GameTime;
                    bool launchedAfterLanding = freshLaunch && landInPhase &&
                                                launch.GameTime >= land.GameTime &&
                                                launch.PhysicsStep >= land.PhysicsStep;
                    // Request age measured from the launch's OWN recorded request timestamp.
                    float requestAge = launchInPhase && !float.IsNaN(launch.JumpRequestTime)
                        ? launch.GameTime - launch.JumpRequestTime
                        : -1f;
                    bool withinBuffer = requestAge >= 0f && requestAge <= player.JumpBufferWindow + 0.02f;
                    bool correlated = pressInPhase && launchInPhase &&
                                      Mathf.Abs(launch.JumpRequestTime - press.GameTime) <= 0.001f;
                    bool velocityOk = launchInPhase && launch.VelocityY >= player.JumpLaunchVelocity * 0.9f;
                    bool reasonOk = launchInPhase && launch.Reason == JumpReason.Buffered;

                    int launchesInPhase = player.JumpLaunchCount - launchCountAtArm;
                    int takeoffsInPhase = takeoffs - takeoffsAtPress;

                    Line($"JumpBuffer: pressGap={pressGap:F3}u takeoffs {takeoffsAtPress} -> {takeoffs} " +
                         $"controllerLaunches={launchesInPhase} airSamplesAfterLaunch={airSamplesAfterLaunch} " +
                         $"bufferWindow={player.JumpBufferWindow:F2}s " +
                         $"requestAge={(requestAge >= 0f ? requestAge.ToString("F3") : "n/a")} " +
                         $"grounded={player.IsGrounded} state={player.State}");
                    LinePhaseEvents();

                    if (!pressInPhase && expired.Kind == MotionEventKind.JumpRequestExpired)
                    {
                        Line($"  [SKIP] the harness press never reached the controller within the phase, while a " +
                             $"request expired at t={expired.GameTime:F3} - result is not a gameplay verdict");
                        SetPhase(Phase.JumpCoyote);
                        break;
                    }

                    Line(pressBeforeLanding
                        ? $"  [PASS] the jump press was registered before landing (press t={press.GameTime:F3}, land t={land.GameTime:F3})"
                        : $"  [FAIL] the jump press was NOT registered before landing (press={pressInPhase}, land={landInPhase})");
                    Line(correlated
                        ? $"  [PASS] the launch consumed the same request the press latched (request t={launch.JumpRequestTime:F3})"
                        : $"  [FAIL] the launch was not correlated with the press request (launch request t={(float.IsNaN(launch.JumpRequestTime) ? "n/a" : launch.JumpRequestTime.ToString("F3"))}, press t={(pressInPhase ? press.GameTime.ToString("F3") : "n/a")})");
                    Line(launchedAfterLanding
                        ? $"  [PASS] the buffered launch happened after touchdown (launch step={launch.PhysicsStep} >= land step={land.PhysicsStep}, launchId={launch.LaunchId}>{launchIdAtArm})"
                        : $"  [FAIL] the launch did not follow a touchdown (launch step={launch.PhysicsStep}, land step={land.PhysicsStep}, fresh={freshLaunch})");
                    Line(withinBuffer
                        ? $"  [PASS] the request was consumed inside the {player.JumpBufferWindow:F2}s buffer window (age={requestAge:F3}s)"
                        : $"  [FAIL] the request was consumed outside the buffer window (age={(requestAge >= 0f ? requestAge.ToString("F3") : "n/a")}s, window={player.JumpBufferWindow:F2}s)");
                    Line(velocityOk
                        ? $"  [PASS] the buffered launch applied the full jump velocity (vyAfterLaunch={launch.VelocityY:F2})"
                        : $"  [FAIL] the buffered launch velocity was wrong (vyAfterLaunch={launch.VelocityY:F2}, expected ~{player.JumpLaunchVelocity:F2})");
                    Line(reasonOk
                        ? "  [PASS] the launch was labelled Buffered, not a coincidental grounded jump"
                        : $"  [FAIL] the launch reason was {launch.Reason}, expected Buffered");
                    // Independent physical observation: the takeoff counter samples real ground/air
                    // transitions. If it disagrees with the controller's own count that is evidence about
                    // physics or sampling - it must be investigated, never waved away as an artefact.
                    Line(launchesInPhase == 1 && takeoffsInPhase >= 1
                        ? $"  [PASS] the physical takeoff counter independently observed the launch (takeoffs={takeoffsInPhase}, airSamplesAfterLaunch={airSamplesAfterLaunch})"
                        : $"  [FAIL] controller launches={launchesInPhase} but the physical takeoff counter saw {takeoffsInPhase} (airSamplesAfterLaunch={airSamplesAfterLaunch}) - investigate sampling/ground detection, do not assume an artefact");
                    SetPhase(Phase.JumpCoyote);
                    break;
                }

                // 6) a jump shortly after walking off a ledge works inside the coyote window.
                case Phase.JumpCoyote:
                {
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        SuspendEnemies();

                        // Stand on Platform_10 (x 7.5..12.5, top y 4.5). Stepping off it drops onto the
                        // flat run below, so a failed coyote jump cannot kill the player.
                        PlacePlayerAt(10.5f, 4.7f);
                        desiredKeys = System.Array.Empty<Key>();
                        return;
                    }

                    if (!PhysicsSettled || !player.IsGrounded)
                        return;

                    if (!phaseArmed)
                    {
                        phaseArmed = true;
                        ResetFlight();
                        ResetTrace();
                        coyoteStartY = player.transform.position.y;
                        stageStartTime = Elapsed;
                        return;
                    }

                    // Wait for one grounded physics step so lastGroundedTime is freshly stamped before
                    // the player leaves the platform - that is what the coyote window measures from.
                    if (Elapsed < stageStartTime + 0.06f)
                        return;

                    if (!coyotePressed)
                    {
                        // Controlled ledge departure in ONE move: place the character just past the
                        // platform's support edge (7.5..12.5 plus the 0.46u probe reach) and press Space
                        // immediately, so the press falls inside the configured coyote window.
                        PlacePlayerAt(13.6f, 4.5f);
                        coyotePressed = true;
                        coyoteLeaveTime = Elapsed;
                        coyoteEdgesAtPress = JumpEdgesSeen;
                        desiredKeys = new[] { Key.Space };
                        return;
                    }

                    // Hold Space only until the reader has observed the press edge (bounded), then release:
                    // the coyote window is measured from the last genuine ground contact, so a press that is
                    // dropped by throttled event delivery would look like a failed coyote jump.
                    desiredKeys = MomentaryPress(Key.Space, JumpEdgesSeen - coyoteEdgesAtPress, Elapsed - coyoteLeaveTime, 0.4f);

                    coyoteVyAfter = Mathf.Max(coyoteVyAfter, player.VerticalVelocity);

                    // Wait until THIS phase's launch event AND the physical takeoff counter have both been
                    // observed, or the bounded window ends. Exiting on a timer alone could stop before the
                    // arc the independent counter samples - which is how a recorded Coyote launch once
                    // coexisted with a sampled peak vy of 0.00.
                    MotionEvent coyoteLaunch = LaunchEvent();
                    bool haveLaunch = coyoteLaunch.Kind == MotionEventKind.JumpLaunch;
                    bool takeoffObserved = takeoffs > 0;

                    if ((!haveLaunch || !takeoffObserved) && Elapsed < coyoteLeaveTime + 3f)
                        return;

                    MotionEvent lost = LastEventOfKind(MotionEventKind.GroundLost);
                    MotionEvent press = PressForLaunch(coyoteLaunch);
                    MotionEvent expired = LastEventOfKind(MotionEventKind.JumpRequestExpired);
                    MotionEvent rejected = LastEventOfKind(MotionEventKind.JumpRequestRejected);

                    bool lostInPhase = lost.Kind == MotionEventKind.GroundLost;
                    bool launchInPhase = coyoteLaunch.Kind == MotionEventKind.JumpLaunch;
                    bool freshLaunch = launchInPhase && coyoteLaunch.LaunchId > launchIdAtArm;
                    bool pressInPhase = press.Kind == MotionEventKind.JumpPress;

                    float departureToLaunch = lostInPhase && freshLaunch ? coyoteLaunch.GameTime - lost.GameTime : -1f;
                    bool afterDeparture = departureToLaunch >= 0f;
                    bool withinCoyote = afterDeparture &&
                                        departureToLaunch <= player.CoyoteWindow + CoyoteStepTolerance;
                    // The launch's OWN recorded ground contact proves the coyote age without trusting the
                    // sampled reason, and it must line up with the departure this phase recorded.
                    float coyoteAge = freshLaunch ? coyoteLaunch.GameTime - coyoteLaunch.GroundContactTime : -1f;
                    bool coyoteAgeOk = coyoteAge >= 0f &&
                                       coyoteAge <= player.CoyoteWindow + CoyoteStepTolerance;
                    bool contactMatchesDeparture = freshLaunch && lostInPhase &&
                                                   Mathf.Abs(coyoteLaunch.GroundContactTime - lost.GameTime) <= 2f * Time.fixedDeltaTime;
                    bool velocityOk = launchInPhase && coyoteLaunch.VelocityY >= player.JumpLaunchVelocity * 0.9f;
                    bool reasonOk = launchInPhase && coyoteLaunch.Reason == JumpReason.Coyote;

                    Line($"JumpCoyote: departureY={coyoteStartY:F2} sampledPeakVy={coyoteVyAfter:F2} " +
                         $"takeoffs={takeoffs} coyoteWindow={player.CoyoteWindow:F2}s " +
                         $"departureToLaunch={(departureToLaunch >= 0f ? departureToLaunch.ToString("F3") : "n/a")} " +
                         $"coyoteAge={(coyoteAge >= 0f ? coyoteAge.ToString("F3") : "n/a")} " +
                         $"launchId={(launchInPhase ? coyoteLaunch.LaunchId : 0)}>{launchIdAtArm} " +
                         $"airSamplesAfterLaunch={airSamplesAfterLaunch} grounded={player.IsGrounded} state={player.State}");
                    LinePhaseEvents();

                    if (!freshLaunch && (expired.Kind == MotionEventKind.JumpRequestExpired ||
                                         rejected.Kind == MotionEventKind.JumpRequestRejected))
                    {
                        Line($"  [SKIP] the coyote press never produced a launch in this phase " +
                             $"(request rejected t={(rejected.Kind == MotionEventKind.JumpRequestRejected ? rejected.GameTime.ToString("F3") : "n/a")}, " +
                             $"expired t={(expired.Kind == MotionEventKind.JumpRequestExpired ? expired.GameTime.ToString("F3") : "n/a")}) - result is not a gameplay verdict");
                        SetPhase(focusedJumpVerification ? Phase.Done : Phase.MoveWhileJumping);
                        break;
                    }

                    Line(lostInPhase
                        ? $"  [PASS] a genuine ground departure was recorded in this phase (t={lost.GameTime:F3}, step={lost.PhysicsStep})"
                        : "  [FAIL] no genuine ground departure was recorded for this phase");
                    Line(afterDeparture
                        ? $"  [PASS] the coyote launch happened after the departure (departure t={lost.GameTime:F3}, launch t={coyoteLaunch.GameTime:F3})"
                        : "  [FAIL] the coyote launch did not follow a ground departure");
                    Line(withinCoyote
                        ? $"  [PASS] the launch fell inside the {player.CoyoteWindow:F2}s coyote window + {CoyoteStepTolerance:F2}s step tolerance (delay={departureToLaunch:F3}s)"
                        : $"  [FAIL] the launch was outside the coyote window (delay={(departureToLaunch >= 0f ? departureToLaunch.ToString("F3") : "n/a")}s, window={player.CoyoteWindow:F2}s + {CoyoteStepTolerance:F2}s)");
                    Line(coyoteAgeOk && contactMatchesDeparture
                        ? $"  [PASS] the launch's recorded ground contact supports the Coyote reason (coyoteAge={coyoteAge:F3}s, contact t={coyoteLaunch.GroundContactTime:F3} vs departure t={lost.GameTime:F3})"
                        : $"  [FAIL] the recorded ground contact does not support Coyote (coyoteAge={(coyoteAge >= 0f ? coyoteAge.ToString("F3") : "n/a")}s, contact t={coyoteLaunch.GroundContactTime:F3}, departure t={lost.GameTime:F3})");
                    Line(velocityOk
                        ? $"  [PASS] the coyote launch applied the full jump velocity at the launch event (vyAfterLaunch={coyoteLaunch.VelocityY:F2})"
                        : $"  [FAIL] the coyote launch velocity was wrong (vyAfterLaunch={coyoteLaunch.VelocityY:F2}, expected ~{player.JumpLaunchVelocity:F2})");
                    Line(reasonOk
                        ? "  [PASS] the launch was labelled Coyote, not a coincidental grounded jump"
                        : $"  [FAIL] the launch reason was {coyoteLaunch.Reason}, expected Coyote");
                    Line(pressInPhase
                        ? $"  [PASS] the launch traces back to a press recorded in this phase (press t={press.GameTime:F3})"
                        : "  [FAIL] no jump press was recorded in this phase, so the launch is unattributed");
                    Line(takeoffs >= 1
                        ? $"  [PASS] the physical takeoff counter independently observed the coyote launch (takeoffs={takeoffs}, airSamplesAfterLaunch={airSamplesAfterLaunch})"
                        : $"  [FAIL] a launch was recorded but the physical takeoff counter saw {takeoffs} - investigate sampling/ground detection, do not assume an artefact");
                    SetPhase(focusedJumpVerification ? Phase.Done : Phase.MoveWhileJumping);
                    break;
                }

                // 8) holding a direction while jumping stays responsive.
                case Phase.MoveWhileJumping:
                {
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        SuspendEnemies();
                        PlacePlayer(-4f);
                        desiredKeys = System.Array.Empty<Key>();
                        return;
                    }

                    if (Elapsed < 0.5f)
                        return;

                    if (!phaseArmed)
                    {
                        phaseArmed = true;
                        ResetFlight();
                        measureStartX = player.transform.position.x;
                        return;
                    }

                    // Toggle Space so every press is a FRESH edge. This does not depend on auto-jump
                    // having been armed by a landing, and Left/Right stays held throughout.
                    bool spaceDown = Mathf.Repeat(Elapsed - 0.5f, 0.16f) < 0.08f;
                    desiredKeys = spaceDown ? new[] { Key.D, Key.Space } : new[] { Key.D };

                    if (!player.IsGrounded)
                    {
                        airborneMarked = true;
                        maxAirSpeed = Mathf.Max(maxAirSpeed, player.HorizontalSpeed);
                    }

                    // Keep going until the airborne phase has been measured.
                    if (Elapsed < 1.4f)
                        return;

                    float airDx = player.transform.position.x - measureStartX;
                    Line($"MoveWhileJumping: airborne={airborneMarked} maxAirSpeed={maxAirSpeed:F2} dx={airDx:F2} state={player.State}");
                    Line(airborneMarked && maxAirSpeed > 3f
                        ? "  [PASS] holding a direction while jumping kept horizontal control responsive"
                        : $"  [FAIL] horizontal control was lost during a jump (airborne={airborneMarked}, airSpeed={maxAirSpeed:F2})");
                    SetPhase(Phase.AttackWhileMoving);
                    break;
                }

                // 9) movement + jump input while attack input is also requested.
                case Phase.AttackWhileMoving:
                {
                    if (enemy == null || enemyHealth == null)
                    {
                        Line("AttackWhileMoving: [SKIP] no enemy in scene");
                        SetPhase(Phase.AttackSpamRecover);
                        return;
                    }

                    foreach (var e in enemies)
                        if (e != null)
                            e.gameObject.SetActive(true);

                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;

                        if (enemyBody != null)
                            enemyBody.bodyType = RigidbodyType2D.Static;

                        enemyHealthBeforeAttack = enemyHealth.CurrentHealth;
                        // Enemy on the RIGHT of the player: holding D faces the target AND is a
                        // continuous movement request, so both inputs are live at the same time and
                        // the swing hitbox (offset 0.75, width 1.35) covers the target.
                        PlacePlayerAt(enemy.transform.position.x - 0.95f, enemy.transform.position.y + 0.05f);
                        sawAttack = false;
                        desiredKeys = new[] { Key.D };
                        return;
                    }

                    if (Elapsed < 0.18f)
                    {
                        desiredKeys = new[] { Key.D };
                        return;
                    }

                    // Attack while the movement key is still held down.
                    if (Elapsed < 0.36f)
                    {
                        desiredKeys = new[] { Key.D, Key.Enter };
                        return;
                    }

                    // Keep the movement key held through the swing, then measure the recovery.
                    desiredKeys = new[] { Key.D };

                    if (!jumpStarted)
                    {
                        // Wait for the swing to actually end before deciding if movement came back.
                        if (player.IsMovementLocked && Elapsed < 1.6f)
                            return;

                        jumpStarted = true;

                        // Clear the target's collider so the recovery walk is not blocked by it.
                        if (enemyBody != null)
                            enemyBody.bodyType = enemyOriginalBodyType;

                        SuspendEnemies();
                        measureStartX = player.transform.position.x;
                        return;
                    }

                    if (Elapsed < 2.1f)
                        return;

                    float enemyAfter = enemyHealth != null ? enemyHealth.CurrentHealth : 0f;
                    float movedAfter = player.transform.position.x - measureStartX;

                    Line($"AttackWhileMoving: attackSeen={sawAttack} enemyHealth {enemyHealthBeforeAttack:F0} -> {enemyAfter:F0}, walked after the swing={movedAfter:F2}, locked={player.IsMovementLocked} state={player.State}");
                    Line(sawAttack && enemyAfter < enemyHealthBeforeAttack
                        ? "  [PASS] an attack still lands while a movement key is held"
                        : $"  [FAIL] the attack did not land while a movement key was held (attackSeen={sawAttack}, health {enemyHealthBeforeAttack:F0} -> {enemyAfter:F0})");
                    Line(!player.IsMovementLocked && movedAfter > 0.3f
                        ? "  [PASS] movement resumed after the attack finished"
                        : $"  [FAIL] movement did not resume after the attack (locked={player.IsMovementLocked}, walked={movedAfter:F2})");
                    SetPhase(Phase.AttackSpamRecover);
                    break;
                }

                // 10) repeated attacks and interruptions never leave movement permanently locked.
                case Phase.AttackSpamRecover:
                {
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        SuspendEnemies();
                        PlacePlayer(-4f);
                        desiredKeys = System.Array.Empty<Key>();
                        return;
                    }

                    if (Elapsed < 0.4f)
                        return;

                    if (!phaseArmed)
                    {
                        phaseArmed = true;
                        measureStartX = player.transform.position.x;
                        return;
                    }

                    // Spam attack (no enemy nearby needed) for a couple of seconds.
                    if (Elapsed < 2.4f)
                    {
                        bool down = Mathf.Repeat(Elapsed - 0.4f, 0.14f) < 0.07f;
                        desiredKeys = down ? new[] { Key.Enter } : System.Array.Empty<Key>();
                        return;
                    }

                    // Then demand movement. If an interrupted swing left the lock held, this stalls.
                    desiredKeys = new[] { Key.D };

                    if (Elapsed < 3.0f)
                        return;

                    float moved = player.transform.position.x - measureStartX;
                    Line($"AttackSpamRecover: moved after attack spam={moved:F2}, locked={player.IsMovementLocked} locks={player.ActiveLocks} state={player.State} grounded={player.IsGrounded}");
                    Line(!player.IsMovementLocked && moved > 0.5f
                        ? "  [PASS] repeated attacks and interruptions left movement fully functional"
                        : $"  [FAIL] movement was suppressed after attack spam (locked={player.IsMovementLocked}, moved={moved:F2})");
                    SetPhase(Phase.PauseRecovery);
                    break;
                }

                // 11b) pause uses REAL time, because pausing freezes game time (the harness's clock).
                case Phase.PauseRecovery:
                {
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        SuspendEnemies();
                        PlacePlayer(0f);
                        desiredKeys = System.Array.Empty<Key>();
                        return;
                    }

                    if (Elapsed < 0.5f)
                        return;

                    if (!phaseArmed)
                    {
                        phaseArmed = true;
                        ResetFlight();

                        // Press Space and pause on the same tick: the press edge is queued and then the
                        // reader is disabled, so the request must not survive the pause.
                        desiredKeys = new[] { Key.Space };

                        // Pause exactly the way PauseMenu does: freeze time and disable the reader,
                        // with a live jump request outstanding.
                        if (GameManager.Instance != null)
                            GameManager.Instance.SetPaused(true);

                        if (inputReader != null)
                            inputReader.Enabled = false;

                        pauseRealStart = (float)(EditorApplication.timeSinceStartup - lastProgressRealtime);
                        return;
                    }

                    float realNow = (float)(EditorApplication.timeSinceStartup - lastProgressRealtime);

                    if (realNow - pauseRealStart < 0.8f)
                        return;

                    // Resume exactly like PauseMenu.Resume.
                    if (GameManager.Instance != null)
                        GameManager.Instance.SetPaused(false);

                    if (inputReader != null)
                        inputReader.Enabled = true;

                    if (!resumeMarked)
                    {
                        resumeMarked = true;
                        resumeRealTime = realNow;
                        takeoffsAtResume = takeoffs;
                        desiredKeys = System.Array.Empty<Key>();
                        return;
                    }

                    // Space is NOT held after resuming: nothing may jump.
                    desiredKeys = System.Array.Empty<Key>();

                    if (realNow - resumeRealTime < 0.7f)
                        return;

                    int staleJumps = takeoffs - takeoffsAtResume;
                    Line($"PauseRecovery: paused 0.8s of real time (game clock frozen), ground state={player.IsGrounded}, locked={player.IsMovementLocked}");
                    Line(staleJumps == 0
                        ? "  [PASS] a jump request outstanding before the pause did not fire after resuming"
                        : $"  [FAIL] {staleJumps} stale jump(s) fired across the pause");
                    Line(!player.IsMovementLocked && player.State != PlayerState.Dead
                        ? "  [PASS] the player is in a valid, unlocked state after a pause cycle"
                        : $"  [FAIL] invalid state after pause (locked={player.IsMovementLocked}, state={player.State})");
                    SetPhase(Phase.KnockbackRecover);
                    break;
                }

                // 11c) knockback, hurt and landing leave a valid, unlocked controller.
                case Phase.KnockbackRecover:
                {
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        SuspendEnemies();
                        PlacePlayer(-4f);
                        desiredKeys = System.Array.Empty<Key>();
                        return;
                    }

                    if (Elapsed < 0.5f)
                        return;

                    if (!phaseArmed)
                    {
                        phaseArmed = true;
                        ResetFlight();
                        knockbackStartX = player.transform.position.x;
                        return;
                    }

                    if (!knockbackApplied)
                    {
                        desiredKeys = System.Array.Empty<Key>();

                        // Wait out invulnerability so the scripted hit is actually accepted.
                        if (playerHealth.IsInvulnerable && Elapsed < 3f)
                            return;

                        var hit = new DamageInfo(1f, player.transform.position, null, 9f, Vector2.right, 0.2f);
                        playerHealth.TakeDamage(in hit);
                        knockbackApplied = true;
                        stageStartTime = Elapsed;
                        return;
                    }

                    // Hitstun runs for 0.28s; wait it out plus a margin before judging the recovery.
                    if (Elapsed < stageStartTime + 0.6f)
                        return;

                    if (!knockbackReported)
                    {
                        knockbackReported = true;
                        float pushed = player.transform.position.x - knockbackStartX;

                        Line($"KnockbackRecover: after hit, state={player.State} pushed={pushed:F2} locked={player.IsMovementLocked} locks={player.ActiveLocks} grounded={player.IsGrounded}");
                        Line(pushed > 0.15f
                            ? "  [PASS] the damage impulse actually moved the player (knockback was not cancelled by the motor)"
                            : $"  [FAIL] knockback moved the player only {pushed:F2}u");
                        Line(!player.IsMovementLocked && player.State != PlayerState.Hurt && player.State != PlayerState.Dead
                            ? "  [PASS] hitstun ended and the controller returned to a normal state"
                            : $"  [FAIL] controller stayed in {player.State} (locked={player.IsMovementLocked})");

                        // Measure the recovery walk from here so the knockback slide is not counted twice.
                        walkStartX = player.transform.position.x;
                        return;
                    }

                    // Movement must work again immediately after hitstun.
                    desiredKeys = new[] { Key.D };

                    if (Elapsed < stageStartTime + 1.2f)
                        return;

                    float walked = player.transform.position.x - walkStartX;
                    Line(!player.IsMovementLocked && walked > 0.4f
                        ? "  [PASS] movement works again after hitstun and knockback"
                        : $"  [FAIL] movement did not resume after hitstun (walked={walked:F2}, locked={player.IsMovementLocked})");
                    SetPhase(Phase.StaleAfterRespawn);
                    break;
                }

                // 12) no stale jump or auto-jump may survive a death/respawn cycle.
                case Phase.StaleAfterRespawn:
                {
                    if (!phaseInitialized)
                    {
                        phaseInitialized = true;
                        SuspendEnemies();
                        PlacePlayer(-4f);
                        desiredKeys = System.Array.Empty<Key>();
                        return;
                    }

                    if (Elapsed < 0.5f)
                        return;

                    // Stage 1: get airborne using toggled fresh presses, so this does not depend on
                    // auto-jump being armed. Bounded: report rather than stall.
                    if (!jumpStarted)
                    {
                        bool spaceDown = Mathf.Repeat(Elapsed - 0.5f, 0.16f) < 0.08f;
                        desiredKeys = spaceDown ? new[] { Key.Space } : System.Array.Empty<Key>();

                        if (!player.IsGrounded)
                            jumpStarted = true;

                        if (Elapsed > 4f)
                        {
                            Line($"StaleAfterRespawn: [FAIL] could not get the player airborne " +
                                 $"(grounded={player.IsGrounded} vy={player.VerticalVelocity:F2} state={player.State})");
                            SetPhase(Phase.Done);
                        }

                        return;
                    }

                    // Stage 2: die mid-air while Space is still held (so auto-jump intent is live too).
                    if (!respawnSeen)
                    {
                        desiredKeys = new[] { Key.Space };

                        var respawnLethal = new DamageInfo(9999f, player.transform.position, null);
                        playerHealth.TakeDamage(in respawnLethal);
                        respawnSeen = true;
                        return;
                    }

                    // Stage 3: Space is released for the whole death/respawn window.
                    desiredKeys = System.Array.Empty<Key>();

                    if (!playerHealth.IsAlive && Elapsed < 10f)
                        return;

                    // Stage 4: re-baseline the instant the player is alive again, then watch for stale input.
                    if (!respawnBaselineDone)
                    {
                        respawnBaselineDone = true;
                        takeoffsAtRespawn = takeoffs;
                        stageStartTime = Elapsed;
                        return;
                    }

                    if (Elapsed < stageStartTime + 0.8f)
                        return;

                    int stale = takeoffs - takeoffsAtRespawn;
                    Line($"StaleAfterRespawn: alive={playerHealth.IsAlive} health={playerHealth.CurrentHealth:F0} staleJumps={stale} grounded={player.IsGrounded} locked={player.IsMovementLocked} state={player.State}");
                    Line(stale == 0
                        ? "  [PASS] no stale jump or auto-jump fired after respawn"
                        : $"  [FAIL] {stale} stale jump(s) fired after respawn");
                    Line(playerHealth.IsAlive && !player.IsMovementLocked && player.State != PlayerState.Dead
                        ? "  [PASS] respawn left the controller alive, unlocked and in a valid state"
                        : $"  [FAIL] respawn left an invalid state (alive={playerHealth.IsAlive}, locked={player.IsMovementLocked}, state={player.State})");
                    SetPhase(Phase.Done);
                    break;
                }

                default:
                    Finish(focusedJumpVerification
                        ? "RESULT: focused jump verification completed"
                        : "RESULT: probe completed");
                    break;
            }
        }
    }
}

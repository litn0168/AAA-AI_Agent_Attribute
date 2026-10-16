using System.Collections.Generic;
using UnityEngine;
using DarkRealms.Core;

namespace DarkRealms.Player
{
    public enum PlayerState
    {
        Idle,
        Walk,
        Run,
        Jump,
        Fall,
        Land,
        Attack,
        Hurt,
        Dead
    }

    /// <summary>What a recorded motion event represents.</summary>
    public enum MotionEventKind
    {
        /// <summary>Never recorded. Keeps a default-constructed event unambiguously "no event".</summary>
        Unset = 0,
        /// <summary>A fresh jump press edge was latched into a request.</summary>
        JumpPress,
        /// <summary>A request survived its buffer window without ever becoming eligible to launch.</summary>
        JumpRequestExpired,
        /// <summary>A still-buffered request could not launch because no ground/coyote eligibility was available.</summary>
        JumpRequestRejected,
        /// <summary>A launch was applied, with its reason and the post-launch velocity.</summary>
        JumpLaunch,
        /// <summary>A new ground contact was accepted (impact velocity is the pre-contact fall speed).</summary>
        Land,
        /// <summary>An accepted ground contact ended.</summary>
        GroundLost,
        /// <summary>A jump press would be accepted right now.</summary>
        CoyoteOpened,
        /// <summary>The jump eligibility window closed.</summary>
        CoyoteExpired,
    }

    /// <summary>
    /// One recorded motion event. Every field needed to correlate a press with the launch it caused is
    /// captured in the same record - an event-specific snapshot cannot be reconstructed from a later
    /// sample, which is exactly how a launch reason and a sampled velocity can disagree.
    /// </summary>
    public struct MotionEvent
    {
        public MotionEventKind Kind;
        public int PhaseId;
        public int LaunchId;
        public JumpReason Reason;
        public float GameTime;
        public float FixedTime;
        public int PhysicsStep;
        public float VelocityY;
        public bool IsGrounded;
        public bool JumpHeld;
        public float JumpRequestTime;
        /// <summary>Game time of the last accepted ground contact when this event was recorded (-99 when none).</summary>
        public float GroundContactTime;
        public float ImpactVelocity;

        public string ToLine()
        {
            var text = new System.Text.StringBuilder(160);

            text.Append("t=").Append(GameTime.ToString("F3"))
                .Append(" ft=").Append(FixedTime.ToString("F3"))
                .Append(" step=").Append(PhysicsStep)
                .Append(" phase=").Append(PhaseId)
                .Append(' ').Append(Kind);

            if (Kind == MotionEventKind.JumpLaunch)
            {
                text.Append(" launchId=").Append(LaunchId)
                    .Append(" reason=").Append(Reason)
                    .Append(" vyAfterLaunch=").Append(VelocityY.ToString("F2"))
                    .Append(" grounded=").Append(IsGrounded)
                    .Append(" held=").Append(JumpHeld)
                    .Append(" requestAge=").Append(float.IsNaN(JumpRequestTime)
                        ? "n/a"
                        : (GameTime - JumpRequestTime).ToString("F3"))
                    .Append(" groundContactAge=").Append(GroundContactTime < 0f
                        ? "n/a"
                        : (GameTime - GroundContactTime).ToString("F3"));

                return text.ToString();
            }

            text.Append(" vy=").Append(VelocityY.ToString("F2"))
                .Append(" grounded=").Append(IsGrounded)
                .Append(" held=").Append(JumpHeld);

            if (Kind == MotionEventKind.Land)
                text.Append(" impactVy=").Append(ImpactVelocity.ToString("F2"));

            return text.ToString();
        }
    }

    /// <summary>
    /// Why a jump launched. Recorded on every launch so a verifier can tell an ability apart from a
    /// temporary upward velocity instead of guessing, and so an unintended relaunch is attributable.
    /// </summary>
    public enum JumpReason
    {
        None,
        /// <summary>A fresh press consumed while standing on the ground.</summary>
        Ground,
        /// <summary>A press latched while airborne and consumed on the ground contact.</summary>
        Buffered,
        /// <summary>Consumed after leaving the ground, inside the coyote window.</summary>
        Coyote,
        /// <summary>The hold-Space auto-jump fired from an armed ground contact.</summary>
        AutoHold
    }

    /// <summary>
    /// Rigidbody2D-based side-view character controller: run/walk, variable-height jump
    /// with coyote time and input buffering, landing recovery, and explicit facing.
    ///
    /// INPUT / PHYSICS CONTRACT
    /// ------------------------
    ///  * Input is SAMPLED in Update (PlayerInputReader runs at -200, so it is always sampled
    ///    earlier in the same frame) and latched into time-stamped requests here.
    ///  * Physics state - velocity, gravityScale, and state transitions caused by them - is
    ///    RESOLVED in FixedUpdate only. This single pipeline is the only place locomotion writes
    ///    body.linearVelocity, so a frame with zero, one or several FixedUpdate steps cannot lose
    ///    or double-apply a request: the request stays valid until it is consumed or expires.
    ///  * Different inputs have different consumption policies:
    ///      - Horizontal direction and JumpHeld are CONTINUOUS: read fresh every frame.
    ///      - Jump press is a ONE-SHOT: latched once, consumed at most once, expires after
    ///        jumpBufferTime.
    ///  * Requests are cleared on death, respawn, pause and reader-disable so input sampled
    ///    before a lock/pause can never fire an action afterwards.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    [DisallowMultipleComponent]
    public class PlayerController : MonoBehaviour, IFacingProvider
    {
        /// <summary>
        /// Why movement is currently suppressed. Each reason is owned by exactly one system and is
        /// cleared by that same system, so unrelated systems can no longer clear each other's lock.
        /// </summary>
        [System.Flags]
        private enum MotionLock
        {
            None = 0,
            /// <summary>Owned by PlayerCombat while a swing is in its startup/active/recovery window.</summary>
            Attack = 1 << 0,
            /// <summary>Owned by PlayerHealth hitstun. Cleared by the hurt timer, never by an animation.</summary>
            Hurt = 1 << 1,
            /// <summary>Owned by death. Cleared only by Respawn.</summary>
            Death = 1 << 2,
            /// <summary>Legacy external owner, kept so existing callers of SetMovementLocked keep working.</summary>
            External = 1 << 3
        }

        [Header("References")]
        [SerializeField] private PlayerInputReader input;
        [Tooltip("Debug anchor only. Ground detection derives its probes from this object's Collider2D, " +
                 "so a mis-authored marker offset can never break grounding (see FootLine).")]
        [SerializeField] private Transform groundCheck;
        [Tooltip("Layers treated as standable ground. Should contain Ground only.")]
        [SerializeField] private LayerMask groundMask = ~0;

        [Header("Movement")]
        [Tooltip("Top horizontal speed while walking, in world units per second.")]
        [SerializeField] private float walkSpeed = 6.5f;
        [Tooltip("Top horizontal speed while sprinting, in world units per second.")]
        [SerializeField] private float runSpeed = 10.5f;
        [Tooltip("Horizontal acceleration while a direction is held, in units/s per second.")]
        [SerializeField] private float acceleration = 70f;
        [Tooltip("Horizontal deceleration after releasing input, in units/s per second.")]
        [SerializeField] private float deceleration = 90f;
        [Tooltip("Multiplier on acceleration/deceleration while airborne. 1 = same handling as grounded.")]
        [SerializeField, Range(0f, 1f)] private float airControl = 0.65f;

        [Header("Jump")]
        [Tooltip("Instantaneous upward velocity applied on jump, in world units per second.")]
        [SerializeField] private float jumpVelocity = 15.5f;
        [Tooltip("Terminal downward speed the body is clamped to, in world units per second.")]
        [SerializeField] private float maxFallSpeed = 26f;
        [Tooltip("Grace period after leaving ground during which a jump is still allowed, in seconds. 0 disables.")]
        [SerializeField] private float coyoteTime = 0.12f;
        [Tooltip("How long a jump press stays queued while airborne or locked, in seconds.")]
        [SerializeField] private float jumpBufferTime = 0.12f;
        [Tooltip("Gravity multiplier while rising with the jump button released (variable jump height).")]
        [SerializeField] private float lowJumpGravityMultiplier = 2.6f;
        [Tooltip("Gravity multiplier while falling, for a snappier descent.")]
        [SerializeField] private float fallGravityMultiplier = 1.35f;

        [Header("Auto Jump (hold Space)")]
        [Tooltip("When enabled, holding Space jumps again automatically after every GENUINE landing, " +
                 "without releasing the button. Requires an actual landing, so it can never fire twice " +
                 "in one airborne phase. Independent of jump buffering and coyote time.")]
        [SerializeField] private bool autoJumpWhileSpaceHeld = true;

        [Header("Action Locks")]
        [Tooltip("Safety cap in seconds on the attack movement lock. If a swing is interrupted without " +
                 "ending cleanly (component disabled, state changed mid-swing) the lock self-releases " +
                 "after this instead of leaving the player permanently stuck.")]
        [SerializeField] private float attackLockSafetyTimeout = 1f;
        [Tooltip("Seconds after taking a hit during which horizontal velocity is owned by the damage " +
                 "impulse rather than the walk motor, so knockback is not cancelled instantly.")]
        [SerializeField] private float knockbackControlLoss = 0.18f;
        [Tooltip("Minimum window after a jump launches during which a grounded probe sample is not " +
                 "trusted, in seconds. The foot probes keep overlapping the platform for a step or two " +
                 "after takeoff; trusting that sample would re-arm coyote time and auto-jump in mid-air. " +
                 "Trust is restored by the end of the rise or the probes physically reporting clear, so " +
                 "this can never latch on and block jumping.")]
        [SerializeField] private float staleGroundGuardTime = 0.08f;

        [Header("Ground Detection")]
        [Tooltip("Radius in world units of each foot probe circle.")]
        [SerializeField] private float groundCheckRadius = 0.14f;
        [Tooltip("Number of probes spread along the foot line. 3+ avoids false ground on ledges and narrow gaps.")]
        [SerializeField, Range(1, 9)] private int footProbeCount = 3;
        [Tooltip("World units the probe centres sit below the collider's bottom edge. Keep <= groundCheckRadius.")]
        [SerializeField] private float groundProbeDepth = 0.06f;
        [Tooltip("Fraction of the collider half-width the outermost probes reach. 1 = exactly at the collider edges.")]
        [SerializeField, Range(0.1f, 1f)] private float footProbeWidthScale = 0.8f;

        [Header("Landing")]
        [Tooltip("Duration of the Land state / movement lock after a hard landing, in seconds.")]
        [SerializeField] private float landingRecovery = 0.08f;
        [Tooltip("Downward impact speed, in units/s, above which a landing counts as 'hard' and triggers landingRecovery.")]
        [SerializeField] private float hardLandingSpeed = 9f;

        private Rigidbody2D body;
        private Collider2D footCollider;
        private float moveInput;

        // --- input requests (latched in Update, consumed in FixedUpdate) ---
        private float lastGroundedTime = -99f;
        private float jumpRequestTime = -99f;

        /// <summary>True when the currently pending jump request was latched while airborne. Lets the
        /// consumer label the launch Buffered instead of Ground without a second buffer.</summary>
        private bool jumpRequestWasAirborne;

        // --- motion locks ---
        private MotionLock locks;
        private float hurtTimer;
        private float attackLockTimer;
        private float knockbackTimer;

        // --- ground-contact bookkeeping ---
        /// <summary>
        /// Time of the last launch. For staleGroundGuardTime afterwards a grounded probe sample is
        /// treated as stale: the foot probes can still overlap the platform for a step or two after
        /// takeoff, and trusting that sample would re-arm coyote time (allowing a double jump) and
        /// re-arm auto-jump.
        ///
        /// This is deliberately a TIMER rather than a latch. A latch cleared only by observing an
        /// airborne sample can get stuck (a teleport, a blocked jump or one missed sample would
        /// leave it set) and then suppress every future jump - a far worse failure than the
        /// double-jump it was guarding against.
        /// </summary>
        private float lastLaunchTime = -99f;

        /// <summary>
        /// Set by every jump launch and cleared once the ground sample can be believed again. While it
        /// is set, a grounded sample is NOT trusted, and it is released by either of two conditions that
        /// are both guaranteed to occur during a jump:
        ///   1. the probes actually report clear (a genuine departure), or
        ///   2. the rise has ended (vertical speed &lt;= 0) and the minimum guard window has elapsed.
        /// Physics guarantees condition 2 within one apex, so the flag cannot latch on; and because a
        /// body that is still rising is never reported grounded, the still-overlapping probes right
        /// after takeoff can no longer re-arm coyote time or the auto-jump in mid-air.
        /// </summary>
        private bool launchGroundGuardActive;

        /// <summary>
        /// True when auto-jump may fire from the current ground contact. Armed by a genuine landing
        /// (or the first grounded step), disarmed the instant any jump launches, so holding Space can
        /// never produce a second jump without a real landing in between.
        /// </summary>
        private bool autoJumpArmed;

        /// <summary>
        /// Time at which the current ground contact began, or -99 when airborne. Used to defer the
        /// hold-Space auto-jump by exactly one physics step after the landing that armed it: the landing
        /// step stays fully grounded (so state, animation and any observer see the contact), and only the
        /// following step launches. Without the deferral the launch would clear the grounded flag in the
        /// same step the contact was made, so a bouncing auto-jump would never report a landing at all.
        /// </summary>
        private float groundContactStartTime = -99f;

        private float landTimer;
        private float baseGravityScale = 1f;
        private float previousVerticalSpeed;

        public PlayerState State { get; private set; } = PlayerState.Idle;
        public bool IsGrounded { get; private set; }
        public int Facing { get; private set; } = 1;
        public bool IsMovementLocked => locks != MotionLock.None;
        public bool IsDead => State == PlayerState.Dead;
        public float HorizontalSpeed => body != null ? Mathf.Abs(body.linearVelocity.x) : 0f;
        public float VerticalVelocity => body != null ? body.linearVelocity.y : 0f;
        public bool IsRunning => input != null && input.SprintHeld && HorizontalSpeed > walkSpeed * 1.05f;
        public Rigidbody2D Body => body;
        /// <summary>The launch speed a real jump leaves the ground with, in units/s.</summary>
        public float JumpLaunchVelocity => jumpVelocity;

        /// <summary>Why the most recent jump launched. Diagnostics only.</summary>
        public JumpReason LastJumpReason { get; private set; } = JumpReason.None;
        /// <summary>Total jump launches since this component was enabled. Diagnostics only.</summary>
        public int JumpLaunchCount { get; private set; }
        /// <summary>Game time of the most recent jump launch, in seconds. Diagnostics only.</summary>
        public float LastJumpTime { get; private set; } = -99f;
        /// <summary>Monotonic identifier of the most recent jump launch. Diagnostics only.</summary>
        public int LastLaunchId { get; private set; }
        /// <summary>True while a grounded sample is being ignored because the body is still rising out of a launch.</summary>
        public bool LaunchGroundGuardActive => launchGroundGuardActive;
        /// <summary>True when a jump press would be accepted in the next physics step (grounded or inside coyote time).</summary>
        public bool IsJumpEligible => !IsMovementLocked && Time.time - lastGroundedTime <= coyoteTime;

        /// <summary>Configured jump buffer window in seconds, so a verifier asserts the real policy.</summary>
        public float JumpBufferWindow => jumpBufferTime;

        /// <summary>Configured coyote window in seconds, so a verifier asserts the real policy.</summary>
        public float CoyoteWindow => coyoteTime;

        /// <summary>Game time of the last accepted ground contact, in seconds. Diagnostics only.</summary>
        public float LastGroundedTime => lastGroundedTime;

        // ---- Bounded motion trace ----------------------------------------------------------------
        // Off by default, so normal play allocates and logs nothing. When the verification harness turns
        // it on, every input edge, request, launch (with its reason), landing and ground transition that
        // affects jumping is recorded, which is what makes a phantom relaunch attributable instead of
        // guesswork.
        private const int MotionTraceCapacity = 800;
        private readonly List<MotionEvent> motionTrace = new();
        private bool coyoteEligible;
        private int physicsStep;
        private int nextLaunchId = 1;
        private float lastRejectedRequestTime = -99f;

        /// <summary>Turns the bounded motion trace on/off. Intended for automated verification only.</summary>
        public bool TraceEnabled { get; set; }

        /// <summary>
        /// Identifier of the test phase currently driving the player. Set by the harness so a recorded
        /// event can never be attributed to the wrong phase, which is the difference between "this test
        /// failed" and "a leftover from the previous test was still in flight".
        /// </summary>
        public int TracePhaseId { get; set; }

        /// <summary>Drops everything recorded so far, so each test starts from a clean trace.</summary>
        public void ClearTrace()
        {
            motionTrace.Clear();
            coyoteEligible = false;
        }

        /// <summary>The recorded trace, oldest first. Empty unless <see cref="TraceEnabled"/> is set.</summary>
        public string DumpTrace()
        {
            var text = new System.Text.StringBuilder();

            foreach (var e in motionTrace)
                text.Append(e.ToLine()).Append('\n');

            return text.ToString();
        }

        /// <summary>The recorded events, oldest first. Empty unless <see cref="TraceEnabled"/> is set.</summary>
        public IReadOnlyList<MotionEvent> TraceEvents => motionTrace;

        private void Trace(
            MotionEventKind kind,
            JumpReason reason = JumpReason.None,
            float impactVelocity = 0f,
            int launchId = 0,
            float requestTime = float.NaN,
            float groundContactTime = -99f)
        {
            if (!TraceEnabled)
                return;

            if (motionTrace.Count >= MotionTraceCapacity)
                motionTrace.RemoveAt(0);

            motionTrace.Add(new MotionEvent
            {
                Kind = kind,
                PhaseId = TracePhaseId,
                LaunchId = launchId,
                Reason = reason,
                GameTime = Time.time,
                FixedTime = Time.fixedTime,
                PhysicsStep = physicsStep,
                VelocityY = body != null ? body.linearVelocity.y : 0f,
                IsGrounded = IsGrounded,
                JumpHeld = input != null && input.JumpHeld,
                JumpRequestTime = requestTime,
                GroundContactTime = groundContactTime,
                ImpactVelocity = impactVelocity,
            });
        }

        /// <summary>Diagnostics only. Which systems currently own a motion lock.</summary>
        public string ActiveLocks => locks.ToString();

        private void Awake()
        {
            body = GetComponent<Rigidbody2D>();
            footCollider = GetComponent<Collider2D>();

            if (footCollider == null)
                Debug.LogError($"[{nameof(PlayerController)}] No Collider2D found. Ground detection falls back to the transform origin, which is unreliable.", this);

            body.freezeRotation = true;
            baseGravityScale = body.gravityScale > 0f ? body.gravityScale : 1f;
            body.gravityScale = baseGravityScale;

            if (input == null)
                input = GetComponent<PlayerInputReader>();

            if (groundCheck == null)
                groundCheck = transform;
        }

        private void OnDisable()
        {
            // A disabled controller must not resume with requests or locks left over from before.
            ClearInputRequests();
            locks = MotionLock.None;
            hurtTimer = attackLockTimer = knockbackTimer = 0f;
            launchGroundGuardActive = false;
            groundContactStartTime = -99f;
        }

        private void Update()
        {
            if (CanReadInput)
            {
                moveInput = Mathf.Clamp(input.MoveX, -1f, 1f);

                // Record a fresh press even while a lock is active. The buffering policy decides
                // whether it is consumed; a locked player must not have the press silently discarded.
                if (input.JumpPressed)
                {
                    jumpRequestTime = Time.time;
                    jumpRequestWasAirborne = !IsGrounded;
                    Trace(MotionEventKind.JumpPress, requestTime: Time.time);
                }
            }
            else
            {
                // Paused, dead or reader disabled: discard everything so input sampled before the
                // pause cannot fire an action after it ends.
                ClearInputRequests();
                moveInput = 0f;
            }

            UpdateFacing();
            UpdateState();
        }

        private void FixedUpdate()
        {
            physicsStep++;
            TickLockTimers();

            if (State == PlayerState.Dead)
                return;

            if (landTimer > 0f)
                landTimer -= Time.fixedDeltaTime;

            UpdateGroundState();
            ApplyGravityProfile();
            ApplyHorizontalMovement();

            // Priority: an explicit one-shot buffered press wins over the auto-jump hold, so the two
            // mechanisms can never both fire in the same step.
            TryConsumeJump();
            TryAutoJump();
        }

        /// <summary>True only when the reader is live and the game is not paused.</summary>
        private bool CanReadInput =>
            input != null &&
            input.Enabled &&
            !IsDead &&
            (GameManager.Instance == null || !GameManager.Instance.IsPaused);

        /// <summary>Drops every pending request and re-arms nothing. Used on pause, death, respawn and disable.</summary>
        private void ClearInputRequests()
        {
            jumpRequestTime = -99f;
            autoJumpArmed = false;
            knockbackTimer = 0f;
        }

        /// <summary>Advances and expires the lock timers. Locks are time-bounded, never animation-bounded.</summary>
        private void TickLockTimers()
        {
            if (hurtTimer > 0f)
            {
                hurtTimer -= Time.fixedDeltaTime;
                if (hurtTimer <= 0f)
                {
                    hurtTimer = 0f;
                    ReleaseLock(MotionLock.Hurt);
                    if (State == PlayerState.Hurt)
                        State = PlayerState.Idle;
                }
            }

            if (knockbackTimer > 0f)
                knockbackTimer = Mathf.Max(0f, knockbackTimer - Time.fixedDeltaTime);

            if (attackLockTimer > 0f)
            {
                attackLockTimer -= Time.fixedDeltaTime;
                if (attackLockTimer <= 0f && HasLock(MotionLock.Attack))
                {
                    HardReleaseAttackLock($"attack lock exceeded its {attackLockSafetyTimeout:F2}s safety cap");
                }
            }
        }

        private bool HasLock(MotionLock mask) => (locks & mask) != 0;

        private void ReleaseLock(MotionLock mask) => locks &= ~mask;

        private void HardReleaseAttackLock(string reason)
        {
            attackLockTimer = 0f;
            ReleaseLock(MotionLock.Attack);
            if (State == PlayerState.Attack)
                State = PlayerState.Idle;

            Debug.LogWarning($"[{nameof(PlayerController)}] Force-released the attack movement lock ({reason}). " +
                             "Movement was restored instead of leaving the player stuck.", this);
        }

        private void UpdateGroundState()
        {
            bool wasGrounded = IsGrounded;
            bool grounded = SampleGround();

            // Release the post-launch guard. Both releasers are guaranteed to occur during a jump, so
            // this always clears and can never latch on and block jumping:
            //   1. the probes report clear   - a genuine departure, or
            //   2. the rise has ended        - the body is no longer travelling upward.
            // While the guard is set a grounded sample is ignored, which is what stops the still
            // overlapping foot probes right after takeoff from re-arming coyote time and the auto-jump
            // in mid-air (a rising body is never grounded).
            if (launchGroundGuardActive)
            {
                bool probesClear = !grounded;
                bool riseEnded = body.linearVelocity.y <= 0.01f && Time.time - lastLaunchTime > staleGroundGuardTime;

                if (probesClear || riseEnded)
                    launchGroundGuardActive = false;
            }

            IsGrounded = grounded && !launchGroundGuardActive;

            if (IsGrounded)
            {
                lastGroundedTime = Time.time;

                if (!wasGrounded)
                {
                    // A genuine new ground contact is the only thing that re-arms auto-jump.
                    autoJumpArmed = true;
                    groundContactStartTime = Time.time;

                    if (previousVerticalSpeed <= -hardLandingSpeed)
                        landTimer = landingRecovery;

                    Trace(MotionEventKind.Land, impactVelocity: previousVerticalSpeed);
                }
            }
            else
            {
                groundContactStartTime = -99f;

                if (wasGrounded)
                    Trace(MotionEventKind.GroundLost);
            }

            // Coyote eligibility is logged on change only, so the trace stays bounded.
            bool eligibleNow = Time.time - lastGroundedTime <= coyoteTime;
            if (eligibleNow != coyoteEligible)
            {
                coyoteEligible = eligibleNow;
                Trace(eligibleNow ? MotionEventKind.CoyoteOpened : MotionEventKind.CoyoteExpired);
            }

            previousVerticalSpeed = body.linearVelocity.y;
        }

        /// <summary>
        /// True if any foot probe overlaps ground. Samples a row of circles along the bottom edge of
        /// this object's own collider, so the result is independent of facing, parenting and any
        /// authored child offset. Allocation-free (the singular OverlapCircle overload does not allocate).
        /// </summary>
        private bool SampleGround()
        {
            FootLine(out float probeY, out float firstX, out float stepX);

            for (int i = 0; i < footProbeCount; i++)
            {
                var point = new Vector2(firstX + stepX * i, probeY);
                if (Physics2D.OverlapCircle(point, groundCheckRadius, groundMask) != null)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Foot-line geometry in world space, derived from the collider so it tracks size/offset changes
        /// automatically. Also used by the gizmo so what you see matches what is tested at runtime.
        /// </summary>
        private void FootLine(out float probeY, out float firstX, out float stepX)
        {
            if (body == null)
                body = GetComponent<Rigidbody2D>();
            if (footCollider == null)
                footCollider = GetComponent<Collider2D>();

            Vector2 origin = body != null ? body.position : (Vector2)transform.position;
            float halfWidth = footCollider != null ? footCollider.bounds.extents.x : 0.4f;
            float bottom = footCollider != null ? footCollider.bounds.min.y : origin.y;

            probeY = bottom - groundProbeDepth;
            float span = halfWidth * footProbeWidthScale;

            if (footProbeCount <= 1)
            {
                firstX = origin.x;
                stepX = 0f;
                return;
            }

            stepX = span * 2f / (footProbeCount - 1);
            firstX = origin.x - span;
        }

        private void ApplyHorizontalMovement()
        {
            // Explicit priority rule between combat and locomotion: for a short window after a hit the
            // damage impulse owns horizontal velocity. Skipping the write here (instead of decelerating
            // towards zero) is what lets knockback actually push the player. Only one of the two ever
            // writes velocity.x in a given step.
            if (knockbackTimer > 0f)
                return;

            float targetSpeed = 0f;
            if (!IsMovementLocked && Mathf.Abs(moveInput) > 0.01f)
            {
                bool running = input != null && input.SprintHeld;
                targetSpeed = moveInput * (running ? runSpeed : walkSpeed);
            }

            float rate = Mathf.Abs(targetSpeed) > 0.01f ? acceleration : deceleration;
            if (!IsGrounded)
                rate *= airControl;

            Vector2 velocity = body.linearVelocity;
            velocity.x = Mathf.MoveTowards(velocity.x, targetSpeed, rate * Time.fixedDeltaTime);
            body.linearVelocity = velocity;
        }

        /// <summary>
        /// Consumes a buffered jump press. Requires BOTH a live request within jumpBufferTime and a
        /// ground contact (or coyote grace) within coyoteTime, so an old press can never surprise-jump.
        /// The launch is labelled with where the eligibility came from.
        /// </summary>
        private void TryConsumeJump()
        {
            // The sentinel is negative, so a request that was never latched - or one that was already
            // consumed or expired - can never be mistaken for a live request.
            if (jumpRequestTime < 0f)
                return;

            if (IsMovementLocked)
                return;

            if (Time.time - jumpRequestTime > jumpBufferTime)
            {
                // The buffer GENUINELY expired: the press can no longer produce a jump. This is the only
                // place expiry is recorded, and dropping the dead request here means it can never be
                // re-evaluated (so it cannot be double-logged or silently resurrected later).
                Trace(MotionEventKind.JumpRequestExpired, requestTime: jumpRequestTime);
                jumpRequestTime = -99f;
                lastRejectedRequestTime = -99f;
                return;
            }

            if (Time.time - lastGroundedTime > coyoteTime)
            {
                // The request is still INSIDE its buffer, but there is no ground contact or coyote grace
                // to launch from. This is a rejection, not an expiry: the request stays valid so a landing
                // inside the remaining buffer window can still consume it. Recorded once per request.
                if (lastRejectedRequestTime != jumpRequestTime)
                {
                    lastRejectedRequestTime = jumpRequestTime;
                    Trace(MotionEventKind.JumpRequestRejected, requestTime: jumpRequestTime);
                }

                return;
            }

            JumpReason reason = IsGrounded
                ? (jumpRequestWasAirborne ? JumpReason.Buffered : JumpReason.Ground)
                : JumpReason.Coyote;

            LaunchJump(reason);
        }

        /// <summary>
        /// Hold-Space auto-jump. Fires only from an armed, grounded, unlocked and non-rising state, so it
        /// is bounded by the ground contact and can never queue a permanent action or relaunch in mid-air.
        /// </summary>
        private void TryAutoJump()
        {
            if (!autoJumpWhileSpaceHeld || input == null || !input.JumpHeld)
                return;

            if (!autoJumpArmed || !IsGrounded || IsMovementLocked)
                return;

            // A held button may only ever produce a GROUND launch. Refusing to fire while the body is
            // still moving upward keeps this a hold-to-bounce feature even if ground detection were to
            // misfire somewhere else.
            if (body.linearVelocity.y > 0.5f)
                return;

            // Deferred a couple of physics steps from the landing that armed it (see
            // groundContactStartTime), so the landing itself is always observable as a grounded state by
            // the state machine, by animation and by any observer before the next launch clears it. This
            // is still the first valid opportunity after landing - it costs ~40 ms.
            if (Time.time - groundContactStartTime < Time.fixedDeltaTime * 1.5f)
                return;

            LaunchJump(JumpReason.AutoHold);
        }

        /// <summary>The single place a jump is applied. Keeps buffering, coyote and auto-jump in sync.</summary>
        private void LaunchJump(JumpReason reason)
        {
            // Captured before the launch invalidates both: the request it consumed, and the ground contact
            // the coyote window is measured from. Capturing them here (rather than re-sampling later) is
            // what lets a verifier prove the launch used THIS request and THIS departure.
            float requestTime = jumpRequestTime;
            float groundContactTime = lastGroundedTime;

            body.linearVelocity = new Vector2(body.linearVelocity.x, jumpVelocity);

            jumpRequestTime = -99f;           // consumed exactly once
            lastGroundedTime = -99f;          // no coyote jump after launching
            lastLaunchTime = Time.time;       // guard the still-overlapping probe sample after takeoff
            launchGroundGuardActive = true;   // a rising body is not grounded until the rise ends
            IsGrounded = false;               // the launch establishes the airborne state immediately
            autoJumpArmed = false;            // needs a fresh landing before auto-jump may fire again
            knockbackTimer = 0f;              // the motor takes ownership back from any damage impulse
            State = PlayerState.Jump;         // launch establishes the rising state immediately

            LastJumpReason = reason;
            LastJumpTime = Time.time;
            LastLaunchId = nextLaunchId++;
            JumpLaunchCount++;

            // Recorded AFTER the velocity write, so the snapshot holds the velocity the launch actually
            // left with rather than a value re-sampled later.
            Trace(
                MotionEventKind.JumpLaunch,
                reason,
                launchId: LastLaunchId,
                // An Auto-hold launch consumes no discrete request, so its age is recorded as
                // not-applicable instead of a stale timestamp that would look like a long-buffered press.
                requestTime: reason == JumpReason.AutoHold ? float.NaN : requestTime,
                groundContactTime: groundContactTime);
        }

        /// <summary>
        /// Shorter hops when the jump button is released early; snappier falls, plus the terminal
        /// fall clamp. Runs in FixedUpdate so this controller has exactly one place that writes velocity.
        /// </summary>
        private void ApplyGravityProfile()
        {
            float vertical = body.linearVelocity.y;

            if (vertical > 0.01f && input != null && !input.JumpHeld)
                body.gravityScale = baseGravityScale * lowJumpGravityMultiplier;
            else if (vertical < -0.01f)
                body.gravityScale = baseGravityScale * fallGravityMultiplier;
            else
                body.gravityScale = baseGravityScale;

            if (vertical < -maxFallSpeed)
                body.linearVelocity = new Vector2(body.linearVelocity.x, -maxFallSpeed);
        }

        private void UpdateFacing()
        {
            // Facing must not flip while a lock is held. It would swing an in-progress attack's hitbox
            // (and the sprite) to the other side mid-swing, and it would invert knockback on a hit.
            if (IsMovementLocked)
                return;

            if (Mathf.Abs(moveInput) > 0.05f)
                Facing = moveInput > 0f ? 1 : -1;
        }

        private void UpdateState()
        {
            // Attack/Hurt/Dead are owned externally so they are not overwritten here.
            if (State == PlayerState.Attack || State == PlayerState.Hurt || State == PlayerState.Dead)
                return;

            if (!IsGrounded)
            {
                State = body.linearVelocity.y > 0.1f ? PlayerState.Jump : PlayerState.Fall;
            }
            else if (landTimer > 0f)
            {
                State = PlayerState.Land;
            }
            else if (HorizontalSpeed > walkSpeed * 1.05f)
            {
                State = PlayerState.Run;
            }
            else if (HorizontalSpeed > 0.2f)
            {
                State = PlayerState.Walk;
            }
            else
            {
                State = PlayerState.Idle;
            }
        }

        // ---- External control surface -------------------------------------------------

        /// <summary>Legacy generic lock, kept so existing callers keep working. Ownership is explicit.</summary>
        public void SetMovementLocked(bool locked)
        {
            if (locked)
                locks |= MotionLock.External;
            else
                ReleaseLock(MotionLock.External);
        }

        /// <summary>
        /// Enters hitstun for the given duration. Time-bounded, not Invoke-based, so disabling the
        /// component or dying mid-stun cannot leave the player locked.
        /// </summary>
        public void EnterHurt(float duration)
        {
            if (State == PlayerState.Dead)
                return;

            State = PlayerState.Hurt;
            locks |= MotionLock.Hurt;
            hurtTimer = Mathf.Max(hurtTimer, Mathf.Max(0f, duration));

            // Hand horizontal velocity to the damage impulse for a short window (see ApplyHorizontalMovement).
            knockbackTimer = knockbackControlLoss;

            // Stale airborne intent must not carry into hitstun.
            jumpRequestTime = -99f;
            autoJumpArmed = false;
        }

        /// <summary>Owned by PlayerCombat. Release is never blocked, so a swing that ends during
        /// hitstun or death still clears its own lock.</summary>
        public void SetAttackState(bool attacking)
        {
            if (!attacking)
            {
                attackLockTimer = 0f;
                ReleaseLock(MotionLock.Attack);

                if (State == PlayerState.Attack)
                    State = PlayerState.Idle;

                return;
            }

            if (State == PlayerState.Dead || State == PlayerState.Hurt)
                return;

            State = PlayerState.Attack;
            locks |= MotionLock.Attack;
            attackLockTimer = attackLockSafetyTimeout;
        }

        public void Kill()
        {
            State = PlayerState.Dead;
            locks = MotionLock.Death;
            hurtTimer = attackLockTimer = knockbackTimer = 0f;
            ClearInputRequests();
            body.linearVelocity = new Vector2(0f, body.linearVelocity.y);
        }

        public void Respawn(Vector2 position)
        {
            transform.position = position;
            body.linearVelocity = Vector2.zero;

            locks = MotionLock.None;
            hurtTimer = attackLockTimer = knockbackTimer = 0f;
            landTimer = 0f;
            lastGroundedTime = -99f;
            lastLaunchTime = -99f;
            launchGroundGuardActive = false;
            groundContactStartTime = -99f;
            previousVerticalSpeed = 0f;

            ClearInputRequests();

            // Auto-jump must re-earn a fresh landing after respawn.
            autoJumpArmed = false;
            State = PlayerState.Idle;
        }

        private void OnDrawGizmosSelected()
        {
            // Mirrors the runtime probe exactly: collider-derived, not marker-derived.
            FootLine(out float probeY, out float firstX, out float stepX);
            Gizmos.color = IsGrounded ? Color.green : Color.red;

            for (int i = 0; i < footProbeCount; i++)
                Gizmos.DrawWireSphere(new Vector2(firstX + stepX * i, probeY), groundCheckRadius);

            if (groundCheck != null)
            {
                // The authored marker: shown so any offset drift is visible, but not used for detection.
                Gizmos.color = new Color(1f, 0.5f, 0f, 0.7f);
                Gizmos.DrawWireSphere(groundCheck.position, 0.05f);
            }
        }
    }
}

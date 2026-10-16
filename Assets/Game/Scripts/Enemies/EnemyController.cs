using UnityEngine;
using DarkRealms.Core;

namespace DarkRealms.Enemies
{
    public enum EnemyState
    {
        Patrol,
        Chase,
        Attack,
        Hurt,
        Dead
    }

    /// <summary>
    /// Simple, readable melee enemy used to validate the combat loop: it patrols between
    /// waypoints, chases the player within an aggro radius, and attacks in melee range.
    /// Kept deliberately small so it doubles as a test target.
    /// </summary>
    [RequireComponent(typeof(Rigidbody2D))]
    [RequireComponent(typeof(Health))]
    [DisallowMultipleComponent]
    public class EnemyController : MonoBehaviour
    {
        [Header("Targeting")]
        [SerializeField] private Transform target;
        [SerializeField] private float aggroRadius = 8f;
        [SerializeField] private float attackRange = 1.35f;
        [SerializeField] private float verticalTolerance = 2.2f;
        [SerializeField] private LayerMask targetMask = ~0;

        [Header("Movement")]
        [SerializeField] private float patrolSpeed = 1.8f;
        [SerializeField] private float chaseSpeed = 3.6f;
        [SerializeField] private float patrolRadius = 3.5f;
        [SerializeField] private float acceleration = 30f;

        [Header("Attack")]
        [SerializeField] private Vector2 hitboxSize = new(1.25f, 1f);
        [SerializeField] private Vector2 hitboxOffset = new(0.7f, 0f);
        [SerializeField] private float damage = 12f;
        [SerializeField] private float attackCooldown = 1.1f;
        [SerializeField] private float windup = 0.35f;
        [SerializeField] private float knockbackForce = 5f;

        [Header("Ground")]
        [SerializeField] private Transform groundCheck;
        [SerializeField] private LayerMask groundMask = ~0;
        [SerializeField] private float groundCheckRadius = 0.14f;
        [SerializeField] private float ledgeCheckDistance = 0.6f;
        [SerializeField] private Vector2 ledgeCheckOffset = new(0.35f, -0.1f);

        [Header("Death")]
        [Tooltip("Seconds before this enemy revives at its home position. Keeps the test target reusable.")]
        [SerializeField] private float reviveDelay = 3f;

        private Rigidbody2D body;
        private Health health;
        private SpriteRenderer spriteRenderer;
        private Vector2 homePosition;
        private float patrolDirection = 1f;
        private float attackTimer;
        private float windupTimer;
        private int facing = 1;
        private float hurtTimer;
        private bool attackInProgress;

        public EnemyState State { get; private set; } = EnemyState.Patrol;
        public int Facing => facing;
        public bool IsGrounded { get; private set; }

        private void Awake()
        {
            body = GetComponent<Rigidbody2D>();
            health = GetComponent<Health>();
            spriteRenderer = GetComponentInChildren<SpriteRenderer>();
            body.freezeRotation = true;

            if (groundCheck == null)
                groundCheck = transform;

            homePosition = transform.position;
        }

        private void OnEnable()
        {
            health.Damaged += HandleDamaged;
            health.Died += HandleDied;
        }

        private void OnDisable()
        {
            health.Damaged -= HandleDamaged;
            health.Died -= HandleDied;
        }

        private void Start()
        {
            if (target == null)
            {
                var player = GameObject.FindGameObjectWithTag("Player");
                if (player != null)
                    target = player.transform;
            }
        }

        private void Update()
        {
            if (State == EnemyState.Dead)
                return;

            if (hurtTimer > 0f)
                hurtTimer -= Time.deltaTime;

            if (attackTimer > 0f)
                attackTimer -= Time.deltaTime;

            IsGrounded = Physics2D.OverlapCircle(groundCheck.position, groundCheckRadius, groundMask) != null;

            EvaluateBrain();
            UpdateFacing();
        }

        private void FixedUpdate()
        {
            // Velocity can only be driven on a dynamic body; static/kinematic bodies (e.g. while
            // frozen by an external system) are left alone.
            if (State == EnemyState.Dead || body.bodyType != RigidbodyType2D.Dynamic)
                return;

            Vector2 velocity = body.linearVelocity;

            switch (State)
            {
                case EnemyState.Patrol:
                    velocity.x = MoveTowards(velocity.x, patrolDirection * patrolSpeed, acceleration);
                    break;

                case EnemyState.Chase:
                    velocity.x = MoveTowards(velocity.x, Mathf.Sign(DirectionToTarget().x) * chaseSpeed, acceleration);
                    break;

                case EnemyState.Attack:
                    velocity.x = MoveTowards(velocity.x, 0f, acceleration * 1.5f);
                    TryAttack();
                    break;

                default:
                    velocity.x = MoveTowards(velocity.x, 0f, acceleration);
                    break;
            }

            body.linearVelocity = velocity;
        }

        private void EvaluateBrain()
        {
            if (hurtTimer > 0f)
            {
                State = EnemyState.Hurt;
                return;
            }

            // An attack already in progress owns the state until it resolves, otherwise the
            // brain would immediately override it and the swing would never connect.
            if (attackInProgress)
            {
                State = EnemyState.Attack;
                return;
            }

            if (target == null)
            {
                State = EnemyState.Patrol;
                return;
            }

            Vector2 toTarget = DirectionToTarget();
            float distance = Mathf.Abs(toTarget.x);
            bool sameHeight = Mathf.Abs(toTarget.y) <= verticalTolerance;

            if (distance <= attackRange && sameHeight && attackTimer <= 0f)
            {
                State = EnemyState.Attack;
                attackInProgress = true;
                windupTimer = windup;
                attackTimer = attackCooldown;
            }
            else if (distance <= aggroRadius && sameHeight)
            {
                State = EnemyState.Chase;
            }
            else
            {
                State = EnemyState.Patrol;
                UpdatePatrolDirection();
            }
        }

        private void UpdatePatrolDirection()
        {
            float distanceFromHome = transform.position.x - homePosition.x;

            if (distanceFromHome > patrolRadius)
                patrolDirection = -1f;
            else if (distanceFromHome < -patrolRadius)
                patrolDirection = 1f;

            // Turn around at ledges or walls so patrols never walk off the world.
            bool groundAhead = Physics2D.Raycast(
                (Vector2)transform.position + new Vector2(ledgeCheckOffset.x * patrolDirection, ledgeCheckOffset.y),
                Vector2.down,
                ledgeCheckDistance,
                groundMask);

            bool wallAhead = Physics2D.Raycast(
                (Vector2)transform.position + new Vector2(0f, 0.2f),
                new Vector2(patrolDirection, 0f),
                0.45f,
                groundMask);

            if (!groundAhead || wallAhead)
                patrolDirection = -patrolDirection;
        }

        private void TryAttack()
        {
            if (windupTimer > 0f)
            {
                windupTimer -= Time.fixedDeltaTime;
                return;
            }

            if (target == null)
                return;

            Vector2 center = (Vector2)transform.position + new Vector2(hitboxOffset.x * facing, hitboxOffset.y);
            Collider2D[] hits = Physics2D.OverlapBoxAll(center, hitboxSize, 0f, targetMask);

            foreach (Collider2D hit in hits)
            {
                if (hit.isTrigger || hit.transform.IsChildOf(transform))
                    continue;

                var damageable = hit.GetComponentInParent<Health>();
                if (damageable == null || !damageable.IsAlive)
                    continue;

                var info = new DamageInfo(
                    damage,
                    hit.ClosestPoint(center),
                    gameObject,
                    knockbackForce,
                    new Vector2(facing, 0.4f));

                damageable.TakeDamage(in info);
                break; // single target per swing
            }

            attackInProgress = false;
            windupTimer = 0f;
            State = EnemyState.Chase;
        }

        private Vector2 DirectionToTarget()
        {
            if (target == null)
                return Vector2.right;

            return target.position - transform.position;
        }

        private void UpdateFacing()
        {
            if (target != null && (State == EnemyState.Chase || State == EnemyState.Attack) && Mathf.Abs(DirectionToTarget().x) > 0.05f)
                facing = DirectionToTarget().x > 0f ? 1 : -1;
            else if (State == EnemyState.Patrol)
                facing = patrolDirection > 0f ? 1 : -1;

            if (spriteRenderer != null)
                spriteRenderer.flipX = facing < 0;
        }

        private static float MoveTowards(float current, float targetValue, float rate)
        {
            return Mathf.MoveTowards(current, targetValue, rate * Time.fixedDeltaTime);
        }

        private void HandleDamaged(DamageInfo info)
        {
            hurtTimer = 0.18f;
            attackInProgress = false;

            if (target == null && info.Source != null)
                target = info.Source.transform;
        }

        private void HandleDied(Health _)
        {
            State = EnemyState.Dead;
            body.linearVelocity = new Vector2(0f, body.linearVelocity.y);
            body.simulated = false;

            foreach (var collider in GetComponentsInChildren<Collider2D>())
                collider.enabled = false;

            StartCoroutine(ReviveAfterDelay());
        }

        private System.Collections.IEnumerator ReviveAfterDelay()
        {
            yield return new WaitForSeconds(reviveDelay);

            transform.position = homePosition;
            body.simulated = true;
            body.linearVelocity = Vector2.zero;

            foreach (var collider in GetComponentsInChildren<Collider2D>())
                collider.enabled = true;

            health.ResetHealth();
            State = EnemyState.Patrol;
            patrolDirection = 1f;
            attackInProgress = false;
            attackTimer = 0.5f;
        }

        private void OnDrawGizmosSelected()
        {
            Gizmos.color = new Color(1f, 0.6f, 0.2f, 0.6f);
            Gizmos.DrawWireSphere(transform.position, aggroRadius);
            Gizmos.color = new Color(1f, 0.2f, 0.2f, 0.8f);
            Gizmos.DrawWireSphere(transform.position, attackRange);
        }
    }
}

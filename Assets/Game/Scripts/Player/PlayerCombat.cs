using DarkRealms.Combat;
using UnityEngine;

namespace DarkRealms.Player
{
    /// <summary>
    /// Attack coordinator. It owns the parts that belong to the actor rather than to a weapon - input
    /// latching, the attack buffer, the swing/cooldown state machine and PlayerController's attack lock -
    /// and delegates the actual damage delivery to a <see cref="WeaponAttackHandler"/> chosen by the equipped
    /// weapon's <see cref="WeaponKind"/>.
    ///
    /// The numbers used by a swing come from the equipped weapon's profile, or from the serialized fields
    /// below when nothing is equipped. Those fields are the unarmed fallback and hold the exact values this
    /// component shipped with, so an unarmed player attacks precisely as before.
    ///
    /// Weapon selection comes from the inventory through InventoryCombatBridge; this component never reads the
    /// UI and has no per-weapon branches keyed on a name or id.
    /// </summary>
    [RequireComponent(typeof(PlayerController))]
    [DisallowMultipleComponent]
    public class PlayerCombat : MonoBehaviour
    {
        [Header("Input")]
        [SerializeField] private PlayerInputReader input;

        [Header("Hitbox (unarmed fallback)")]
        [SerializeField] private Vector2 hitboxSize = new(1.35f, 1.05f);
        [SerializeField] private Vector2 hitboxOffset = new(0.75f, 0.05f);
        [SerializeField] private LayerMask targetMask = ~0;

        [Header("Damage (unarmed fallback)")]
        [SerializeField] private float damage = 25f;
        [SerializeField] private float knockbackForce = 6f;
        [SerializeField] private float stunTime = 0.18f;

        [Header("Timing (unarmed fallback)")]
        [SerializeField] private float attackDuration = 0.34f;
        [SerializeField] private float activeWindowStart = 0.08f;
        [SerializeField] private float activeWindowEnd = 0.2f;
        [SerializeField] private float cooldown = 0.12f;

        [Header("Input Buffering")]
        [Tooltip("Seconds an attack press stays queued while a swing or the cooldown is still running, " +
                 "in real seconds. Lets a press made slightly early come out at the first valid " +
                 "opportunity instead of being silently dropped. 0 disables buffering.")]
        [SerializeField] private float attackBufferTime = 0.15f;

        [Header("Feedback (unarmed fallback)")]
        [SerializeField] private GameObject hitEffectPrefab;

        private PlayerController controller;
        private float attackTimer;
        private float cooldownTimer;
        private float attackRequestTime = -99f;

        private WeaponAttackProfile fallbackProfile;
        private MeleeAttackHandler meleeHandler;

        // Frozen for the duration of one swing so a weapon change mid-swing cannot swap hitboxes underneath it.
        private WeaponAttackProfile swingProfile;
        private WeaponAttackHandler swingHandler;

        private WeaponDefinition equippedWeapon;

        public bool IsAttacking { get; private set; }

        public float NormalizedAttackTime =>
            IsAttacking && swingProfile != null && swingProfile.attackDuration > 0f
                ? Mathf.Clamp01(attackTimer / swingProfile.attackDuration)
                : 0f;

        /// <summary>Weapon currently driving attacks, or null for the unarmed fallback.</summary>
        public WeaponDefinition ActiveWeapon => equippedWeapon;

        /// <summary>
        /// The numbers the next attack will use: the equipped weapon's profile, or the unarmed fallback.
        /// Read-only view for diagnostics and verification - edit the WeaponDefinition asset instead of
        /// mutating this, otherwise authored data changes only until the next domain reload.
        /// </summary>
        public WeaponAttackProfile ActiveProfile => equippedWeapon != null ? equippedWeapon.attack : fallbackProfile;

        private void Awake()
        {
            controller = GetComponent<PlayerController>();
            if (input == null)
                input = GetComponent<PlayerInputReader>();

            fallbackProfile = new WeaponAttackProfile
            {
                attackDuration = attackDuration,
                activeWindowStart = activeWindowStart,
                activeWindowEnd = activeWindowEnd,
                cooldown = cooldown,
                damage = damage,
                knockbackForce = knockbackForce,
                stunTime = stunTime,
                hitboxSize = hitboxSize,
                hitboxOffset = hitboxOffset,
                targetMask = targetMask,
                hitEffectPrefab = hitEffectPrefab
            };

            meleeHandler = new MeleeAttackHandler();
        }

        private void Update()
        {
            if (cooldownTimer > 0f)
                cooldownTimer -= Time.deltaTime;

            // A disabled reader (pause/menus) or a dead player must never keep a queued attack, and a
            // swing in flight must not stay open forever.
            if (input == null || !input.Enabled || controller == null || controller.IsDead)
            {
                attackRequestTime = -99f;

                if (IsAttacking && (controller == null || controller.IsDead))
                    EndAttack();

                return;
            }

            if (IsAttacking)
            {
                attackTimer += Time.deltaTime;

                WeaponAttackProfile profile = swingProfile;
                if (profile != null &&
                    attackTimer >= profile.activeWindowStart &&
                    attackTimer <= profile.activeWindowEnd)
                {
                    swingHandler?.OnActiveWindowTick(BuildContext(profile), attackTimer);
                }

                if (profile == null || attackTimer >= profile.attackDuration)
                    EndAttack();
                return;
            }

            // Latch a fresh press before the cooldown test. Buffering is deliberate: a press made
            // during a swing or the cooldown is honoured at the first valid opportunity rather than
            // dropped, which is what made rapid attacks feel unresponsive.
            if (input.AttackPressed)
                RequestAttack();

            bool requestValid = Time.time - attackRequestTime <= attackBufferTime;

            if (requestValid && cooldownTimer <= 0f)
            {
                attackRequestTime = -99f;   // consumed exactly once
                BeginAttack();
            }
        }

        private void OnDisable()
        {
            // Switching this component off mid-swing must not leave PlayerController's attack lock
            // held, which would permanently suppress movement.
            attackRequestTime = -99f;

            if (IsAttacking)
                EndAttack();
        }

        /// <summary>
        /// Queues an attack request, latching it exactly like a pressed attack button so the buffer window,
        /// cooldown and swing state machine all behave the same. This is the single entry point for a request,
        /// which keeps non-keyboard sources (AI, replay, Play Mode verification) on the real buffered path
        /// instead of bypassing it. Call it only when no swing is running, which is where Update calls it -
        /// presses during a swing are intentionally dropped by the state machine above.
        /// </summary>
        public void RequestAttack()
        {
            attackRequestTime = Time.time;
        }

        /// <summary>
        /// Applies the weapon equipped in the inventory. Pass null for the unarmed fallback.
        ///
        /// Safe mid-swing and mid-cooldown: the swing in flight keeps the profile and handler it started with,
        /// so a weapon change can never open a second hitbox or cut a hit short. The new weapon takes effect
        /// on the next swing, and the running cooldown is left alone.
        /// </summary>
        public void SetWeapon(WeaponDefinition definition)
        {
            if (equippedWeapon == definition)
                return;

            equippedWeapon = definition;

            if (equippedWeapon != null && !equippedWeapon.IsDeliveryImplemented)
            {
                Debug.LogWarning(
                    $"[PlayerCombat] Weapon '{equippedWeapon.weaponId}' has kind {equippedWeapon.kind}, which has no " +
                    "delivery handler yet (no projectile/spell infrastructure in this project). Falling back to the " +
                    "melee handler so the player can still attack.", equippedWeapon);
            }
        }

        private void BeginAttack()
        {
            IsAttacking = true;
            attackTimer = 0f;

            swingProfile = ActiveProfile;
            swingHandler = ResolveHandler(equippedWeapon != null ? equippedWeapon.kind : WeaponKind.Melee);

            if (controller != null)
                controller.SetAttackState(true);

            swingHandler?.OnAttackBegin(BuildContext(swingProfile));
        }

        private void EndAttack()
        {
            IsAttacking = false;
            attackTimer = 0f;
            cooldownTimer = swingProfile != null ? swingProfile.cooldown : cooldown;

            swingHandler?.OnAttackEnd(BuildContext(swingProfile));
            swingHandler = null;
            swingProfile = null;

            // Release unconditionally: if the swing ended during hitstun or after death, the lock
            // still has to be cleared or movement would never come back.
            if (controller != null)
                controller.SetAttackState(false);
        }

        /// <summary>Single registration point for attack delivery. Add a case here to support a new kind.</summary>
        private WeaponAttackHandler ResolveHandler(WeaponKind kind)
        {
            // Ranged and Magic are authored extension points only: nothing in this project fires projectiles
            // or casts spells yet. Returning the melee handler keeps the player able to attack instead of
            // silently doing nothing; SetWeapon already warned about the missing handler.
            return meleeHandler;
        }

        private AttackContext BuildContext(WeaponAttackProfile profile)
        {
            return new AttackContext(
                gameObject,
                transform,
                controller != null ? controller.Facing : 1,
                profile);
        }

        private void OnDrawGizmosSelected()
        {
            var owner = controller != null ? controller : GetComponent<PlayerController>();
            int facing = owner != null ? owner.Facing : 1;

            // Preview the equipped weapon when one is set, otherwise the authored unarmed hitbox. Read straight
            // from the serialized fields so the gizmo also works in edit mode, where Awake has not run yet.
            Vector2 size = hitboxSize;
            Vector2 offset = hitboxOffset;
            if (equippedWeapon != null)
            {
                size = equippedWeapon.attack.hitboxSize;
                offset = equippedWeapon.attack.hitboxOffset;
            }

            Vector2 center = (Vector2)transform.position + new Vector2(offset.x * facing, offset.y);

            Gizmos.color = IsAttacking ? Color.red : new Color(1f, 0.4f, 0.4f, 0.4f);
            Gizmos.DrawWireCube(center, size);
        }
    }
}

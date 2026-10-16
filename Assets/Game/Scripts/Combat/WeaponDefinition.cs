using UnityEngine;

namespace DarkRealms.Combat
{
    /// <summary>
    /// How a weapon delivers its damage. Only <see cref="Melee"/> has a delivery handler implemented in this
    /// project; the other entries exist so a definition can be authored honestly without pretending the
    /// delivery exists yet (see <see cref="WeaponDefinition.IsDeliveryImplemented"/>).
    /// </summary>
    public enum WeaponKind
    {
        Melee = 0,
        Ranged = 1,
        Magic = 2
    }

    /// <summary>
    /// The gameplay numbers of a single attack. One field layout is shared by every place that needs those
    /// numbers - a <see cref="WeaponDefinition"/> asset owns one, and PlayerCombat owns one as the unarmed
    /// fallback - so a given weapon's values are never duplicated between two places that can drift apart.
    /// </summary>
    [System.Serializable]
    public sealed class WeaponAttackProfile
    {
        [Header("Timing (seconds)")]
        [Min(0f)] public float attackDuration = 0.34f;
        [Min(0f)] public float activeWindowStart = 0.08f;
        [Min(0f)] public float activeWindowEnd = 0.22f;
        [Min(0f)] public float cooldown = 0.12f;

        [Header("Damage")]
        [Min(0f)] public float damage = 25f;
        [Min(0f)] public float knockbackForce = 6f;
        [Min(0f)] public float stunTime = 0.18f;

        [Header("Hitbox (melee handler)")]
        public Vector2 hitboxSize = new Vector2(1.4f, 1.1f);
        public Vector2 hitboxOffset = new Vector2(0.8f, 0.05f);
        public LayerMask targetMask = ~0;

        [Header("Feedback")]
        public GameObject hitEffectPrefab;

        /// <summary>Independent copy, so handing the numbers to a verifier cannot let it edit live combat state.</summary>
        public WeaponAttackProfile Clone()
        {
            return new WeaponAttackProfile
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
        }
    }

    /// <summary>
    /// Data-driven description of one weapon. Inventory items of category Weapon point at one of these, and
    /// PlayerCombat applies it while that weapon is equipped. Actor behaviour lives in the handlers
    /// (<see cref="WeaponAttackHandler"/>), not in per-weapon branches keyed on a name or id.
    /// </summary>
    [CreateAssetMenu(menuName = "Dark Realms/Combat/Weapon Definition", fileName = "Weapon_")]
    public sealed class WeaponDefinition : ScriptableObject
    {
        [Header("Identity")]
        [Tooltip("Stable id linking this weapon to its inventory item and to saved data. Do not localise or reuse it.")]
        public string weaponId = "new_weapon";

        public string displayName = "New Weapon";

        [Tooltip("Optional. The inventory item normally supplies the icon shown in the bag.")]
        public Sprite icon;

        [Header("Attack")]
        public WeaponKind kind = WeaponKind.Melee;

        public WeaponAttackProfile attack = new WeaponAttackProfile();

        /// <summary>
        /// True when a delivery handler exists for <see cref="kind"/>. PlayerCombat falls back to the melee
        /// handler and warns instead of silently behaving as if the weapon were implemented.
        /// </summary>
        public bool IsDeliveryImplemented => kind == WeaponKind.Melee;

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(weaponId))
                weaponId = name.Trim().ToLowerInvariant().Replace(' ', '_');

            if (string.IsNullOrWhiteSpace(displayName))
                displayName = name;

            // Keep the authoring window coherent so a weapon can never have its active window past its own end.
            if (attack.activeWindowEnd < attack.activeWindowStart)
                attack.activeWindowEnd = attack.activeWindowStart;

            if (attack.attackDuration < attack.activeWindowEnd)
                attack.attackDuration = attack.activeWindowEnd;
        }
#endif
    }
}

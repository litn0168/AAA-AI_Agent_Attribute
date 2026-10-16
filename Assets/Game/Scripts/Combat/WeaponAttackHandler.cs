using UnityEngine;

namespace DarkRealms.Combat
{
    /// <summary>
    /// Everything a handler needs for one swing, passed by value so a handler cannot keep a reference to
    /// mutable combat state between attacks.
    /// </summary>
    public readonly struct AttackContext
    {
        public readonly GameObject Owner;
        public readonly Transform Origin;
        public readonly int Facing;
        public readonly WeaponAttackProfile Profile;

        public AttackContext(GameObject owner, Transform origin, int facing, WeaponAttackProfile profile)
        {
            Owner = owner;
            Origin = origin;
            Facing = facing;
            Profile = profile;
        }
    }

    /// <summary>
    /// Extension point for attack delivery: melee today, a projectile or spell handler later. PlayerCombat owns
    /// one stateless instance per <see cref="WeaponKind"/> and reuses it for every swing, so switching weapons
    /// mid-fight cannot leave an orphaned hitbox behind - a swing's handler and profile are frozen when the
    /// swing starts and released when it ends.
    ///
    /// To support a new kind: write a subclass and add one case to PlayerCombat.ResolveHandler.
    /// </summary>
    public abstract class WeaponAttackHandler
    {
        /// <summary>Called once when the swing starts. Per-swing state (hit bookkeeping) is reset here.</summary>
        public abstract void OnAttackBegin(in AttackContext context);

        /// <summary>Called every frame while attackTimer is inside the profile's active window.</summary>
        public abstract void OnActiveWindowTick(in AttackContext context, float attackTimer);

        /// <summary>Called once when the swing ends, including when it ends early through death or disable.</summary>
        public abstract void OnAttackEnd(in AttackContext context);
    }
}

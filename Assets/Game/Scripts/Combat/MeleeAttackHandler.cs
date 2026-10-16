using System.Collections.Generic;
using DarkRealms.Core;
using UnityEngine;

namespace DarkRealms.Combat
{
    /// <summary>
    /// Default melee delivery: an overlap box that is only resolved during the active window of the swing,
    /// with each target hit at most once per swing. This is the behaviour the project shipped with, moved
    /// behind the handler seam unchanged - same overlap query, same self-hit filtering, same damage payload,
    /// same knockback direction and the same hit effect.
    /// </summary>
    public sealed class MeleeAttackHandler : WeaponAttackHandler
    {
        private readonly HashSet<Health> hitThisSwing = new HashSet<Health>();

        public override void OnAttackBegin(in AttackContext context)
        {
            hitThisSwing.Clear();
        }

        public override void OnActiveWindowTick(in AttackContext context, float attackTimer)
        {
            WeaponAttackProfile profile = context.Profile;
            if (profile == null || context.Origin == null || context.Owner == null)
                return;

            // OverlapBoxAll is kept deliberately (rather than the NonAlloc variant) so the set of targets and
            // their order stay identical to the behaviour this handler replaced; a fixed buffer could silently
            // drop targets in a crowded hitbox.
            Vector2 center = (Vector2)context.Origin.position +
                             new Vector2(profile.hitboxOffset.x * context.Facing, profile.hitboxOffset.y);
            Collider2D[] hits = Physics2D.OverlapBoxAll(center, profile.hitboxSize, 0f, profile.targetMask);

            foreach (Collider2D hit in hits)
            {
                if (hit == null || hit.isTrigger || hit.transform.IsChildOf(context.Owner.transform))
                    continue;

                Health health = hit.GetComponentInParent<Health>();
                if (health == null || !health.IsAlive || hitThisSwing.Contains(health))
                    continue;

                hitThisSwing.Add(health);

                Vector2 direction = new Vector2(context.Facing, 0.35f);
                var info = new DamageInfo(
                    profile.damage,
                    hit.ClosestPoint(center),
                    context.Owner,
                    profile.knockbackForce,
                    direction,
                    profile.stunTime);

                health.TakeDamage(in info);

                if (profile.hitEffectPrefab != null)
                    Object.Instantiate(profile.hitEffectPrefab, hit.ClosestPoint(center), Quaternion.identity);
            }
        }

        public override void OnAttackEnd(in AttackContext context)
        {
            hitThisSwing.Clear();
        }
    }
}

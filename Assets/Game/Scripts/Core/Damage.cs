using UnityEngine;

namespace DarkRealms.Core
{
    /// <summary>Immutable payload describing a single hit.</summary>
    public struct DamageInfo
    {
        public float Amount;
        public float KnockbackForce;
        public Vector2 KnockbackDirection;
        public Vector2 HitPoint;
        public GameObject Source;
        public float StunTime;

        public DamageInfo(
            float amount,
            Vector2 hitPoint,
            GameObject source = null,
            float knockbackForce = 0f,
            Vector2 knockbackDirection = default,
            float stunTime = 0f)
        {
            Amount = amount;
            HitPoint = hitPoint;
            Source = source;
            KnockbackForce = knockbackForce;
            KnockbackDirection = knockbackDirection.sqrMagnitude < 0.0001f
                ? Vector2.right
                : knockbackDirection.normalized;
            StunTime = stunTime;
        }
    }

    /// <summary>Anything that can absorb damage.</summary>
    public interface IDamageable
    {
        bool IsAlive { get; }
        void TakeDamage(in DamageInfo info);
    }

    /// <summary>Exposes a horizontal facing direction (-1 / +1) without implying a concrete actor type.</summary>
    public interface IFacingProvider
    {
        int Facing { get; }
    }
}

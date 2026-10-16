using System;
using UnityEngine;

namespace DarkRealms.Core
{
    /// <summary>
    /// Generic, reusable health/damage component. Drives UI through events so no
    /// concrete HUD reference is required, and applies optional knockback.
    /// </summary>
    [DisallowMultipleComponent]
    public class Health : MonoBehaviour, IDamageable
    {
        [Header("Health")]
        [SerializeField] protected float maxHealth = 100f;
        [Tooltip("Seconds of invulnerability granted after a hit. 0 disables i-frames.")]
        [SerializeField] protected float invulnerabilityTime = 0f;
        [SerializeField] protected bool destroyOnDeath;

        [Header("Reaction")]
        [SerializeField] protected bool applyKnockback = true;

        protected float currentHealth;
        protected float invulnerableUntil;
        protected Rigidbody2D body;

        /// <summary>Raised for every accepted hit.</summary>
        public event Action<DamageInfo> Damaged;
        /// <summary>Raised once when health reaches zero.</summary>
        public event Action<Health> Died;
        /// <summary>Raised as (current, max) whenever health changes.</summary>
        public event Action<float, float> HealthChanged;

        public float MaxHealth => maxHealth;
        public float CurrentHealth => currentHealth;
        public float NormalizedHealth => maxHealth <= 0f ? 0f : Mathf.Clamp01(currentHealth / maxHealth);
        public bool IsAlive => currentHealth > 0f;
        public bool IsInvulnerable => Time.time < invulnerableUntil;

        protected virtual void Awake()
        {
            currentHealth = maxHealth;
            body = GetComponent<Rigidbody2D>();
        }

        protected virtual void Start()
        {
            HealthChanged?.Invoke(currentHealth, maxHealth);
        }

        /// <summary>Restores full health and clears invulnerability. Used on respawn.</summary>
        public virtual void ResetHealth()
        {
            currentHealth = maxHealth;
            invulnerableUntil = 0f;
            HealthChanged?.Invoke(currentHealth, maxHealth);
        }

        public virtual void TakeDamage(in DamageInfo info)
        {
            if (!IsAlive || IsInvulnerable || info.Amount <= 0f)
                return;

            currentHealth = Mathf.Max(0f, currentHealth - info.Amount);

            if (invulnerabilityTime > 0f)
                invulnerableUntil = Time.time + invulnerabilityTime;

            if (applyKnockback && body != null && info.KnockbackForce > 0f)
                body.AddForce(info.KnockbackDirection * info.KnockbackForce, ForceMode2D.Impulse);

            HealthChanged?.Invoke(currentHealth, maxHealth);
            Damaged?.Invoke(info);
            OnDamaged(info);

            if (currentHealth <= 0f)
                Die(info);
        }

        public virtual void Heal(float amount)
        {
            if (!IsAlive || amount <= 0f)
                return;

            currentHealth = Mathf.Min(maxHealth, currentHealth + amount);
            HealthChanged?.Invoke(currentHealth, maxHealth);
        }

        protected virtual void OnDamaged(in DamageInfo info) { }

        protected virtual void Die(in DamageInfo info)
        {
            Died?.Invoke(this);
            OnDied();
            if (destroyOnDeath)
                Destroy(gameObject);
        }

        protected virtual void OnDied() { }
    }
}

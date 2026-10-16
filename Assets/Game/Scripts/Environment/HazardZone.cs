using UnityEngine;
using DarkRealms.Core;

namespace DarkRealms.Environment
{
    /// <summary>
    /// Trigger volume that damages anything entering it. Used for the pit/kill plane so
    /// falling out of the level triggers the death and respawn loop.
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    [DisallowMultipleComponent]
    public class HazardZone : MonoBehaviour
    {
        [SerializeField] private float damage = 999f;
        [SerializeField] private float knockbackForce;

        private void Reset()
        {
            var collider = GetComponent<Collider2D>();
            if (collider != null)
                collider.isTrigger = true;
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            var health = other.GetComponentInParent<Health>();
            if (health == null || !health.IsAlive)
                return;

            var info = new DamageInfo(
                damage,
                other.ClosestPoint(transform.position),
                gameObject,
                knockbackForce,
                Vector2.up);

            health.TakeDamage(in info);
        }
    }
}

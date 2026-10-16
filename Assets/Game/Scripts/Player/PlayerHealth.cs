using System.Collections;
using UnityEngine;
using DarkRealms.Core;

namespace DarkRealms.Player
{
    /// <summary>
    /// Player-specific health: invulnerability frames, hurt reaction, death and respawn
    /// at the GameManager's active spawn point. Extends the generic Health component.
    /// </summary>
    [DisallowMultipleComponent]
    public class PlayerHealth : Health
    {
        [Header("Player Reaction")]
        [SerializeField] private PlayerController controller;
        [SerializeField] private float respawnDelay = 1.6f;
        [SerializeField] private float hurtDuration = 0.28f;

        protected override void Awake()
        {
            base.Awake();
            if (controller == null)
                controller = GetComponent<PlayerController>();
        }

        protected override void OnDamaged(in DamageInfo info)
        {
            if (controller == null)
                return;

            controller.EnterHurt(Mathf.Max(hurtDuration, info.StunTime));
        }

        protected override void OnDied()
        {
            if (controller != null)
                controller.Kill();

            StartCoroutine(RespawnAfterDelay());
        }

        private IEnumerator RespawnAfterDelay()
        {
            yield return new WaitForSeconds(respawnDelay);

            ResetHealth();

            Vector2 spawnPosition = transform.position;
            if (GameManager.Instance != null && GameManager.Instance.ActiveSpawn != null)
                spawnPosition = GameManager.Instance.ActiveSpawn.position;

            if (controller != null)
                controller.Respawn(spawnPosition);

            // Re-sync the resolve target after the antialiased death pose.
            transform.rotation = Quaternion.identity;
        }
    }
}

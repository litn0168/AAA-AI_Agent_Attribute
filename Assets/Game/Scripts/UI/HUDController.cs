using UnityEngine;
using UnityEngine.UI;
using DarkRealms.Core;
using DarkRealms.Player;

namespace DarkRealms.UI
{
    /// <summary>
    /// Minimal gameplay HUD: health bar, health text and a player-state readout.
    /// Subscribes to Health events rather than polling so it stays decoupled.
    /// </summary>
    [DisallowMultipleComponent]
    public class HUDController : MonoBehaviour
    {
        [SerializeField] private Health playerHealth;
        [SerializeField] private PlayerController playerController;
        [SerializeField] private Image healthFill;
        [SerializeField] private Text healthText;
        [SerializeField] private Text stateText;
        [SerializeField] private Image damageVignette;
        [SerializeField] private float vignetteFadeSpeed = 4f;

        private float vignetteAlpha;

        private void OnEnable()
        {
            if (playerHealth != null)
            {
                playerHealth.HealthChanged += HandleHealthChanged;
                playerHealth.Damaged += HandleDamaged;
                HandleHealthChanged(playerHealth.CurrentHealth, playerHealth.MaxHealth);
            }
        }

        private void OnDisable()
        {
            if (playerHealth != null)
            {
                playerHealth.HealthChanged -= HandleHealthChanged;
                playerHealth.Damaged -= HandleDamaged;
            }
        }

        private void Update()
        {
            if (stateText != null && playerController != null)
            {
                string lockText = playerController.IsMovementLocked ? " (locked)" : string.Empty;
                stateText.text = $"State: {playerController.State}{lockText}";
            }

            if (damageVignette != null)
            {
                vignetteAlpha = Mathf.MoveTowards(vignetteAlpha, 0f, vignetteFadeSpeed * Time.unscaledDeltaTime);
                Color color = damageVignette.color;
                color.a = vignetteAlpha;
                damageVignette.color = color;
            }
        }

        private void HandleHealthChanged(float current, float max)
        {
            if (healthFill != null)
                healthFill.fillAmount = max <= 0f ? 0f : Mathf.Clamp01(current / max);

            if (healthText != null)
                healthText.text = $"{Mathf.CeilToInt(current)} / {Mathf.CeilToInt(max)}";
        }

        private void HandleDamaged(DamageInfo info)
        {
            vignetteAlpha = Mathf.Clamp01(0.35f + info.Amount / 100f);
        }
    }
}

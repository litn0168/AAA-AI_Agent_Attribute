using DarkRealms.Core;
using DarkRealms.Inventory;
using DarkRealms.Player;
using UnityEngine;

namespace DarkRealms.Integration
{
    /// <summary>
    /// Gate between the inventory panel and gameplay input. While the bag is open the player cannot move, jump
    /// or attack behind it; when it closes the state that was in force beforehand is restored rather than being
    /// forced on.
    ///
    /// Pause is deliberately not this component's business: GameManager remains the only owner of Time.timeScale,
    /// so opening the bag never pauses the world and never competes with PauseMenu. The bag only borrows the
    /// existing <see cref="PlayerInputReader.Enabled"/> gate that PauseMenu already uses.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InventoryInputLock : MonoBehaviour
    {
        [SerializeField] private InventoryUIController inventory;
        [SerializeField] private PlayerInputReader playerInput;

        private bool lockedByInventory;
        private bool enabledBeforeLock;

        private void Awake()
        {
            if (inventory == null)
                inventory = FindAnyObjectByType<InventoryUIController>();

            if (playerInput == null)
                playerInput = FindAnyObjectByType<PlayerInputReader>();
        }

        private void OnEnable()
        {
            if (inventory == null)
                return;

            inventory.onVisibilityChanged.AddListener(HandleVisibilityChanged);

            // Cover the case where the panel is already open by the time this component comes up.
            if (inventory.IsOpen)
                HandleVisibilityChanged(true);
        }

        private void OnDisable()
        {
            if (inventory != null)
                inventory.onVisibilityChanged.RemoveListener(HandleVisibilityChanged);
        }

        private void HandleVisibilityChanged(bool open)
        {
            if (playerInput == null)
                return;

            if (open)
            {
                if (lockedByInventory)
                    return;

                enabledBeforeLock = playerInput.Enabled;
                lockedByInventory = true;
                playerInput.Enabled = false;
                return;
            }

            if (!lockedByInventory)
                return;

            lockedByInventory = false;

            // Restore what was true before the bag opened, except never re-enable input for a paused game: that
            // would hand control back while the world is frozen, and would also disagree with PauseMenu.
            bool paused = GameManager.Instance != null && GameManager.Instance.IsPaused;
            playerInput.Enabled = enabledBeforeLock && !paused;
        }
    }
}

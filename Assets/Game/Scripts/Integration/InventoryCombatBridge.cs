using DarkRealms.Combat;
using DarkRealms.Inventory;
using DarkRealms.Player;
using UnityEngine;

namespace DarkRealms.Integration
{
    /// <summary>
    /// The single place that translates inventory state into combat state: it watches the equipped weapon
    /// slots and pushes the matching <see cref="WeaponDefinition"/> into PlayerCombat.
    ///
    /// Responsibility is deliberately narrow. The inventory never reaches into PlayerCombat's internals, and
    /// PlayerCombat never queries the UI - both only meet here. Every outcome (slot selected, weapon equipped,
    /// unequipped, swapped, slot emptied) funnels through <see cref="PushActiveWeapon"/>, which re-resolves from
    /// the model instead of trusting event payloads, so there is exactly one code path to reason about.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InventoryCombatBridge : MonoBehaviour
    {
        [SerializeField] private InventoryModel inventory;
        [SerializeField] private PlayerCombat combat;

        [Tooltip("Log weapon-to-combat pushes to the Console. Useful while integrating; can be switched off later.")]
        [SerializeField] private bool logChanges = true;

        private WeaponDefinition lastResolved;
        private bool needsInitialPush = true;

        public WeaponDefinition CurrentWeapon => lastResolved;

        private void Awake()
        {
            if (inventory == null)
                inventory = FindAnyObjectByType<InventoryModel>();

            if (combat == null)
                combat = FindAnyObjectByType<PlayerCombat>();
        }

        private void OnEnable()
        {
            if (inventory != null)
            {
                inventory.ActiveWeaponChanged += HandleActiveWeaponChanged;
                inventory.EquipmentChanged += HandleEquipmentChanged;
            }

            PushActiveWeapon();
        }

        private void Start()
        {
            // InventoryModel.Awake normalises its arrays, and the UI's Start may still be settling on the first
            // frame, so resolve once more after both have come up.
            if (needsInitialPush)
                PushActiveWeapon();
        }

        private void OnDisable()
        {
            if (inventory == null)
                return;

            inventory.ActiveWeaponChanged -= HandleActiveWeaponChanged;
            inventory.EquipmentChanged -= HandleEquipmentChanged;
        }

        // The payload is intentionally ignored: a stale or empty argument must never be able to leave combat on
        // the wrong weapon, so the active slot is always re-read from the model instead.
        private void HandleActiveWeaponChanged(int weaponIndex, InventoryItemDefinition item) => PushActiveWeapon();

        private void HandleEquipmentChanged() => PushActiveWeapon();

        /// <summary>
        /// Resolves the weapon that should drive combat and applies it. Only an item of category Weapon with a
        /// WeaponDefinition assigned yields a weapon; an empty slot, a potion or a bare weapon item all resolve
        /// to null, which restores PlayerCombat's unarmed fallback rather than inventing a weapon.
        /// </summary>
        public void PushActiveWeapon()
        {
            needsInitialPush = false;

            if (inventory == null || combat == null)
                return;

            InventoryItemDefinition item = inventory.ActiveWeapon;

            WeaponDefinition resolved = null;
            if (item != null && item.category == InventoryItemCategory.Weapon)
            {
                resolved = item.weapon;

                if (resolved == null)
                {
                    Debug.LogWarning(
                        $"[InventoryCombatBridge] '{item.itemId}' is a Weapon item but has no WeaponDefinition " +
                        "assigned, so combat keeps the unarmed fallback. Assign one on the item asset to bind it.",
                        item);
                }
            }

            bool changed = lastResolved != resolved;
            lastResolved = resolved;
            combat.SetWeapon(resolved);

            if (logChanges && changed)
            {
                Debug.Log($"[InventoryCombatBridge] weapon slot {inventory.ActiveWeaponIndex} -> " +
                          (resolved != null
                              ? $"{resolved.weaponId} (damage {resolved.attack.damage}, kind {resolved.kind})"
                              : "(none: unarmed fallback)"));
            }
        }
    }
}

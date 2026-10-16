using DarkRealms.Combat;
using UnityEngine;

namespace DarkRealms.Inventory
{
    public enum InventoryItemCategory
    {
        Weapon,
        Helmet,
        Chestplate,
        Legging,
        Boots,
        Earring,
        Necklace,
        Ring,
        Bracelet,
        Amulet,
        Accessory,
        Consumable,
        Material,
        Quest
    }

    /// <summary>Data asset describing an inventory item. Use one asset per distinct item type.</summary>
    [CreateAssetMenu(menuName = "Dark Realms/Inventory/Item Definition", fileName = "Item_")]
    public sealed class InventoryItemDefinition : ScriptableObject
    {
        [Header("Identity")]
        public string itemId = "new_item";
        public string displayName = "New Item";
        [TextArea(2, 5)] public string description;
        public Sprite icon;

        [Header("Rules")]
        public InventoryItemCategory category = InventoryItemCategory.Material;
        [Min(1)] public int maxStack = 100;

        [Header("Combat")]
        [Tooltip("Combat profile applied when this item is equipped in a weapon slot. Only read for items whose " +
                 "category is Weapon; leave it empty on every other item. A Weapon item without a profile keeps " +
                 "PlayerCombat on its unarmed fallback instead of inventing one.")]
        public WeaponDefinition weapon;

        /// <summary>True when this item is a weapon with a combat profile bound to it.</summary>
        public bool HasWeaponDefinition => category == InventoryItemCategory.Weapon && weapon != null;

        public int MaxStack => Mathf.Clamp(maxStack, 1, 100);
        public bool IsEquipment => category == InventoryItemCategory.Weapon ||
                                   category == InventoryItemCategory.Helmet ||
                                   category == InventoryItemCategory.Chestplate ||
                                   category == InventoryItemCategory.Legging ||
                                   category == InventoryItemCategory.Boots ||
                                   category == InventoryItemCategory.Earring ||
                                   category == InventoryItemCategory.Necklace ||
                                   category == InventoryItemCategory.Ring ||
                                   category == InventoryItemCategory.Bracelet ||
                                   category == InventoryItemCategory.Amulet ||
                                   category == InventoryItemCategory.Accessory;

#if UNITY_EDITOR
        private void OnValidate()
        {
            maxStack = Mathf.Clamp(maxStack, 1, 100);
            if (string.IsNullOrWhiteSpace(itemId))
                itemId = name.Trim().ToLowerInvariant().Replace(' ', '_');
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = name;
        }
#endif
    }
}

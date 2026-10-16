using System;
using System.Collections.Generic;
using UnityEngine;

namespace DarkRealms.Inventory
{
    public enum EquipmentSlotId
    {
        Weapon1 = 0,
        Weapon2,
        Weapon3,
        Helmet,
        Chestplate,
        Legging,
        Boots,
        EarringLeft,
        EarringRight,
        Necklace,
        RingLeft,
        RingRight,
        Bracelet,
        Amulet,
        Accessory1,
        Accessory2,
        Accessory3,
        Accessory4,
        Accessory5
    }

    /// <summary>
    /// Inventory data model. The UI is a view of this component; gameplay and loot code should call its API,
    /// rather than editing UI labels or slot objects directly.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InventoryModel : MonoBehaviour
    {
        public const int Columns = 6;
        public const int Rows = 9;
        public const int InventorySlotCount = Columns * Rows;
        public const int AbsoluteMaxStack = 100;
        public const int WeaponSlotCount = 3;

        [SerializeField] private InventoryStack[] inventorySlots = new InventoryStack[InventorySlotCount];
        [SerializeField] private InventoryStack[] equipmentSlots = new InventoryStack[Enum.GetValues(typeof(EquipmentSlotId)).Length];
        [SerializeField, Range(0, WeaponSlotCount - 1)] private int activeWeaponIndex;

        public event Action InventoryChanged;
        public event Action EquipmentChanged;
        public event Action<int, InventoryItemDefinition> ActiveWeaponChanged;

        public int ActiveWeaponIndex => Mathf.Clamp(activeWeaponIndex, 0, WeaponSlotCount - 1);
        public InventoryItemDefinition ActiveWeapon => GetEquipmentSlot((EquipmentSlotId)ActiveWeaponIndex)?.item;

        private void Awake() => EnsureArrays();
        private void OnValidate() => EnsureArrays();

        public InventoryStack GetInventorySlot(int index)
        {
            EnsureArrays();
            return index >= 0 && index < inventorySlots.Length ? inventorySlots[index] : null;
        }

        public InventoryStack GetEquipmentSlot(EquipmentSlotId slot)
        {
            EnsureArrays();
            int index = (int)slot;
            return index >= 0 && index < equipmentSlots.Length ? equipmentSlots[index] : null;
        }

        /// <summary>Add up to amount items. Returns the number accepted; amount - return value remains with the caller.</summary>
        public int AddItem(InventoryItemDefinition item, int amount)
        {
            EnsureArrays();
            if (item == null || amount <= 0) return 0;
            int added = AddItemInternal(item, amount);
            if (added > 0) InventoryChanged?.Invoke();
            return added;
        }

        public bool RemoveFromInventory(int index, int amount)
        {
            EnsureArrays();
            InventoryStack stack = GetInventorySlot(index);
            if (stack == null || stack.IsEmpty || amount <= 0 || stack.count < amount) return false;
            stack.count -= amount;
            if (stack.count <= 0) inventorySlots[index] = null;
            InventoryChanged?.Invoke();
            return true;
        }

        public bool MoveInventorySlots(int fromIndex, int toIndex)
        {
            EnsureArrays();
            if (!ValidInventoryIndex(fromIndex) || !ValidInventoryIndex(toIndex) || fromIndex == toIndex)
                return false;

            InventoryStack source = inventorySlots[fromIndex];
            if (source == null || source.IsEmpty) return false;
            InventoryStack target = inventorySlots[toIndex];

            if (target == null || target.IsEmpty)
            {
                inventorySlots[toIndex] = source;
                inventorySlots[fromIndex] = null;
            }
            else if (target.item == source.item)
            {
                int capacity = target.item.MaxStack - target.count;
                if (capacity <= 0) return false;
                int moved = Math.Min(capacity, source.count);
                target.count += moved;
                source.count -= moved;
                if (source.count <= 0) inventorySlots[fromIndex] = null;
            }
            else
            {
                inventorySlots[fromIndex] = target;
                inventorySlots[toIndex] = source;
            }

            InventoryChanged?.Invoke();
            return true;
        }

        /// <summary>
        /// Equip one item from an inventory stack. If the destination already holds equipment, that item is
        /// returned to inventory first. The operation rolls back if the displaced item cannot fit.
        /// </summary>
        public bool TryEquipInventoryItem(int inventoryIndex, EquipmentSlotId targetSlot)
        {
            EnsureArrays();
            if (!ValidInventoryIndex(inventoryIndex) || !Enum.IsDefined(typeof(EquipmentSlotId), targetSlot)) return false;
            InventoryStack incoming = inventorySlots[inventoryIndex];
            if (incoming == null || incoming.IsEmpty || !CanEquip(incoming.item, targetSlot)) return false;

            int equipmentIndex = (int)targetSlot;
            InventoryStack[] inventoryBackup = CopyArray(inventorySlots);
            InventoryStack[] equipmentBackup = CopyArray(equipmentSlots);
            int previousActiveWeapon = activeWeaponIndex;

            InventoryItemDefinition incomingItem = incoming.item;
            incoming.count--;
            if (incoming.count <= 0) inventorySlots[inventoryIndex] = null;

            InventoryStack displaced = equipmentSlots[equipmentIndex];
            equipmentSlots[equipmentIndex] = new InventoryStack(incomingItem, 1);

            if (displaced != null && !displaced.IsEmpty)
            {
                int returned = AddItemInternal(displaced.item, displaced.count);
                if (returned != displaced.count)
                {
                    inventorySlots = inventoryBackup;
                    equipmentSlots = equipmentBackup;
                    activeWeaponIndex = previousActiveWeapon;
                    return false;
                }
            }

            InventoryChanged?.Invoke();
            EquipmentChanged?.Invoke();
            NotifyActiveWeaponIfChanged(targetSlot);
            return true;
        }

        public bool TryUnequipToInventory(EquipmentSlotId sourceSlot, int inventoryIndex)
        {
            EnsureArrays();
            if (!ValidInventoryIndex(inventoryIndex) || !Enum.IsDefined(typeof(EquipmentSlotId), sourceSlot)) return false;
            int sourceIndex = (int)sourceSlot;
            InventoryStack equipped = equipmentSlots[sourceIndex];
            if (equipped == null || equipped.IsEmpty) return false;

            InventoryStack target = inventorySlots[inventoryIndex];
            if (target == null || target.IsEmpty)
            {
                inventorySlots[inventoryIndex] = equipped;
                equipmentSlots[sourceIndex] = null;
            }
            else if (target.item == equipped.item && target.count < target.item.MaxStack)
            {
                target.count++;
                equipmentSlots[sourceIndex] = null;
            }
            else
            {
                // No item is removed from equipment if the destination has no room.
                return false;
            }

            InventoryChanged?.Invoke();
            EquipmentChanged?.Invoke();
            NotifyActiveWeaponIfChanged(sourceSlot);
            return true;
        }

        public bool TryMoveEquipment(EquipmentSlotId fromSlot, EquipmentSlotId toSlot)
        {
            EnsureArrays();
            if (!Enum.IsDefined(typeof(EquipmentSlotId), fromSlot) || !Enum.IsDefined(typeof(EquipmentSlotId), toSlot) || fromSlot == toSlot) return false;
            int from = (int)fromSlot;
            int to = (int)toSlot;
            InventoryStack source = equipmentSlots[from];
            InventoryStack target = equipmentSlots[to];
            if (source == null || source.IsEmpty) return false;
            if (!CanEquip(source.item, toSlot)) return false;
            if (target != null && !target.IsEmpty && !CanEquip(target.item, fromSlot)) return false;

            equipmentSlots[from] = target;
            equipmentSlots[to] = source;
            EquipmentChanged?.Invoke();
            if (IsWeaponSlot(fromSlot)) NotifyActiveWeaponChanged();
            if (IsWeaponSlot(toSlot)) NotifyActiveWeaponChanged();
            return true;
        }

        public void SelectWeaponSlot(int weaponIndex)
        {
            if (weaponIndex < 0 || weaponIndex >= WeaponSlotCount || activeWeaponIndex == weaponIndex)
                return;
            activeWeaponIndex = weaponIndex;
            NotifyActiveWeaponChanged();
        }

        /// <summary>
        /// Short form of <see cref="TryQuickEquip"/> used by the UI's double-click, so both entry points always
        /// follow the same empty-slot-first rule instead of drifting apart.
        /// </summary>
        public bool TryAutoEquip(int inventoryIndex)
        {
            return TryQuickEquip(inventoryIndex, out _, out _);
        }

        /// <summary>
        /// Equips one item from a bag stack into the first EMPTY compatible slot, so a second ring never silently
        /// replaces the ring already worn while the other ring slot is free. Only when every compatible slot is
        /// occupied does it fall back to a swap, which returns the replaced item to the bag and rolls back if it
        /// does not fit. Returns false - changing nothing - for items that are not equipment or fit no slot.
        /// </summary>
        public bool TryQuickEquip(int inventoryIndex, out EquipmentSlotId usedSlot, out bool replacedEquipped)
        {
            EnsureArrays();
            usedSlot = default;
            replacedEquipped = false;

            InventoryStack stack = GetInventorySlot(inventoryIndex);
            if (stack == null || stack.IsEmpty || !stack.item.IsEquipment)
                return false;

            EquipmentSlotId? firstOccupied = null;
            foreach (EquipmentSlotId slot in Enum.GetValues(typeof(EquipmentSlotId)))
            {
                if (!CanEquip(stack.item, slot))
                    continue;

                InventoryStack occupied = equipmentSlots[(int)slot];
                if (occupied == null || occupied.IsEmpty)
                {
                    if (!TryEquipInventoryItem(inventoryIndex, slot))
                        return false;
                    usedSlot = slot;
                    return true;
                }

                if (firstOccupied == null)
                    firstOccupied = slot;
            }

            if (firstOccupied == null)
                return false;

            if (!TryEquipInventoryItem(inventoryIndex, firstOccupied.Value))
                return false;

            usedSlot = firstOccupied.Value;
            replacedEquipped = true;
            return true;
        }

        /// <summary>
        /// Moves an equipped item back into the bag: into a partial stack of the same item when one has room,
        /// otherwise into the first empty slot. Returns false when there is nowhere for it to go, leaving the
        /// item equipped rather than destroying it.
        /// </summary>
        public bool TryQuickUnequip(EquipmentSlotId slot, out int inventoryIndex)
        {
            EnsureArrays();
            inventoryIndex = -1;
            if (!Enum.IsDefined(typeof(EquipmentSlotId), slot))
                return false;

            InventoryStack equipped = equipmentSlots[(int)slot];
            if (equipped == null || equipped.IsEmpty)
                return false;

            int capacity = Mathf.Min(AbsoluteMaxStack, equipped.item.MaxStack);
            int destination = -1;
            for (int i = 0; i < inventorySlots.Length; i++)
            {
                InventoryStack candidate = inventorySlots[i];
                if (candidate == null || candidate.IsEmpty || candidate.item != equipped.item)
                    continue;
                if (candidate.count < capacity)
                {
                    destination = i;
                    break;
                }
            }

            if (destination < 0)
                destination = FirstEmptyInventoryIndex();

            if (destination < 0)
                return false;

            if (!TryUnequipToInventory(slot, destination))
                return false;

            inventoryIndex = destination;
            return true;
        }

        /// <summary>
        /// Splits <paramref name="amount"/> items off an existing bag stack into the first empty bag slot.
        /// Refuses a split that would empty the source (that is a move, not a split) and refuses when the bag has
        /// no free slot, so a rejected split never leaves two half-written stacks behind.
        /// </summary>
        public bool TrySplitStack(int index, int amount, out int newIndex)
        {
            EnsureArrays();
            newIndex = -1;
            if (!ValidInventoryIndex(index) || amount <= 0)
                return false;

            InventoryStack source = inventorySlots[index];
            if (source == null || source.IsEmpty || amount >= source.count)
                return false;

            int destination = FirstEmptyInventoryIndex();
            if (destination < 0)
                return false;

            inventorySlots[destination] = new InventoryStack(source.item, amount);
            source.count -= amount;
            newIndex = destination;
            InventoryChanged?.Invoke();
            return true;
        }

        /// <summary>Largest legal split amount for a bag stack, or 0 when it cannot be split at all.</summary>
        public int MaxSplitAmount(int index)
        {
            EnsureArrays();
            if (!ValidInventoryIndex(index))
                return 0;
            InventoryStack stack = inventorySlots[index];
            if (stack == null || stack.IsEmpty || FirstEmptyInventoryIndex() < 0)
                return 0;
            return stack.count - 1;
        }

        /// <summary>
        /// Merges partial stacks of the same item into the earliest one with room. Totals are preserved: every
        /// item is subtracted from one stack and added to another, and the result is independent of the order
        /// the stacks happened to be in. Returns the number of merges performed.
        /// </summary>
        public int AutoStackInventory()
        {
            EnsureArrays();
            int merges = 0;

            for (int i = 0; i < inventorySlots.Length; i++)
            {
                InventoryStack target = inventorySlots[i];
                if (target == null || target.IsEmpty)
                    continue;

                int capacity = Mathf.Min(AbsoluteMaxStack, target.item.MaxStack);
                for (int j = i + 1; j < inventorySlots.Length && target.count < capacity; j++)
                {
                    InventoryStack donor = inventorySlots[j];
                    if (donor == null || donor.IsEmpty || donor.item != target.item)
                        continue;

                    int moved = Math.Min(capacity - target.count, donor.count);
                    if (moved <= 0)
                        continue;

                    target.count += moved;
                    donor.count -= moved;
                    if (donor.count <= 0)
                        inventorySlots[j] = null;
                    merges++;
                }
            }

            if (merges > 0)
                InventoryChanged?.Invoke();
            return merges;
        }

        /// <summary>
        /// Deterministic bag sort: item category, then display name, then item id. Stacks of one item that fit
        /// together are merged as part of the pass, which can only ever reduce the number of slots used, so the
        /// result always fits. Item totals are unchanged. Returns true when the layout actually changed.
        /// </summary>
        public bool SortInventory()
        {
            EnsureArrays();

            var entries = new List<InventoryStack>();
            for (int i = 0; i < inventorySlots.Length; i++)
            {
                InventoryStack stack = inventorySlots[i];
                if (stack != null && !stack.IsEmpty)
                    entries.Add(new InventoryStack(stack.item, stack.count));
            }

            if (entries.Count == 0)
                return false;

            entries.Sort(CompareStacks);

            var sorted = new List<InventoryStack>(entries.Count);
            foreach (InventoryStack entry in entries)
            {
                int capacity = Mathf.Min(AbsoluteMaxStack, entry.item.MaxStack);
                int remaining = entry.count;

                for (int i = 0; i < sorted.Count && remaining > 0; i++)
                {
                    InventoryStack existing = sorted[i];
                    if (existing.item != entry.item || existing.count >= capacity)
                        continue;
                    int moved = Math.Min(capacity - existing.count, remaining);
                    existing.count += moved;
                    remaining -= moved;
                }

                if (remaining > 0)
                    sorted.Add(new InventoryStack(entry.item, remaining));
            }

            bool changed = false;
            for (int i = 0; i < inventorySlots.Length; i++)
            {
                InventoryStack next = i < sorted.Count ? sorted[i] : null;
                if (!SameStack(inventorySlots[i], next))
                    changed = true;
                inventorySlots[i] = next;
            }

            if (changed)
                InventoryChanged?.Invoke();
            return changed;
        }

        /// <summary>
        /// Pure feasibility check for a drag from one slot onto another, used to colour the target while dragging.
        /// It mirrors the rules of the mutating operations and never changes state: bag to bag swaps unlike items,
        /// dropping onto equipment must satisfy that slot's category rule, and equipment can only be unequipped
        /// onto an empty bag slot or a stack of the same item with room.
        /// </summary>
        public bool CanPlace(int fromIndex, bool fromIsEquipment, int toIndex, bool toIsEquipment)
        {
            EnsureArrays();
            if (fromIsEquipment == toIsEquipment && fromIndex == toIndex)
                return false;

            InventoryStack source = fromIsEquipment
                ? GetEquipmentSlot((EquipmentSlotId)fromIndex)
                : GetInventorySlot(fromIndex);
            if (source == null || source.IsEmpty)
                return false;

            if (!toIsEquipment)
            {
                if (!ValidInventoryIndex(toIndex))
                    return false;

                InventoryStack target = inventorySlots[toIndex];
                if (target == null || target.IsEmpty)
                    return true;

                if (target.item == source.item)
                    return target.count < Mathf.Min(AbsoluteMaxStack, target.item.MaxStack);

                // A bag-to-bag drag swaps; an unequip onto a different item has nowhere to put the swap.
                return !fromIsEquipment;
            }

            var targetSlot = (EquipmentSlotId)toIndex;
            if (!Enum.IsDefined(typeof(EquipmentSlotId), targetSlot) || !CanEquip(source.item, targetSlot))
                return false;

            if (!fromIsEquipment)
                return true;

            InventoryStack occupant = equipmentSlots[toIndex];
            return occupant == null || occupant.IsEmpty || CanEquip(occupant.item, (EquipmentSlotId)fromIndex);
        }

        /// <summary>Bag item total.</summary>
        public int CountInInventory(InventoryItemDefinition item)
        {
            EnsureArrays();
            if (item == null)
                return 0;

            int total = 0;
            foreach (InventoryStack stack in inventorySlots)
                if (stack != null && !stack.IsEmpty && stack.item == item)
                    total += stack.count;
            return total;
        }

        /// <summary>Equipped item total.</summary>
        public int CountEquipped(InventoryItemDefinition item)
        {
            EnsureArrays();
            if (item == null)
                return 0;

            int total = 0;
            foreach (InventoryStack stack in equipmentSlots)
                if (stack != null && !stack.IsEmpty && stack.item == item)
                    total += stack.count;
            return total;
        }

        /// <summary>
        /// Bag plus equipment item total. Verification reads this before and after a move to prove that nothing
        /// was lost or duplicated.
        /// </summary>
        public int TotalItemCount
        {
            get
            {
                EnsureArrays();
                int total = 0;
                foreach (InventoryStack stack in inventorySlots)
                    if (stack != null && !stack.IsEmpty)
                        total += stack.count;
                foreach (InventoryStack stack in equipmentSlots)
                    if (stack != null && !stack.IsEmpty)
                        total += stack.count;
                return total;
            }
        }

        /// <summary>Number of occupied bag slots.</summary>
        public int UsedInventorySlots
        {
            get
            {
                EnsureArrays();
                int used = 0;
                foreach (InventoryStack stack in inventorySlots)
                    if (stack != null && !stack.IsEmpty)
                        used++;
                return used;
            }
        }

        /// <summary>Index of the first empty bag slot, or -1 when the bag is full.</summary>
        public int FirstEmptyInventoryIndex()
        {
            EnsureArrays();
            for (int i = 0; i < inventorySlots.Length; i++)
                if (inventorySlots[i] == null || inventorySlots[i].IsEmpty)
                    return i;
            return -1;
        }

        private static bool SameStack(InventoryStack a, InventoryStack b)
        {
            bool aEmpty = a == null || a.IsEmpty;
            bool bEmpty = b == null || b.IsEmpty;
            if (aEmpty || bEmpty)
                return aEmpty == bEmpty;
            return a.item == b.item && a.count == b.count;
        }

        /// <summary>
        /// Total order used by the bag sort. Two entries of the same item compare equal, and they are
        /// indistinguishable once merged, so the merged layout does not depend on the sort's tie order.
        /// </summary>
        private static int CompareStacks(InventoryStack a, InventoryStack b)
        {
            int byCategory = ((int)a.item.category).CompareTo((int)b.item.category);
            if (byCategory != 0)
                return byCategory;

            int byName = string.CompareOrdinal(a.item.displayName, b.item.displayName);
            if (byName != 0)
                return byName;

            return string.CompareOrdinal(a.item.itemId, b.item.itemId);
        }

        public static bool CanEquip(InventoryItemDefinition item, EquipmentSlotId slot)
        {
            if (item == null) return false;
            switch (slot)
            {
                case EquipmentSlotId.Weapon1:
                case EquipmentSlotId.Weapon2:
                case EquipmentSlotId.Weapon3:
                    return item.category == InventoryItemCategory.Weapon;
                case EquipmentSlotId.Helmet: return item.category == InventoryItemCategory.Helmet;
                case EquipmentSlotId.Chestplate: return item.category == InventoryItemCategory.Chestplate;
                case EquipmentSlotId.Legging: return item.category == InventoryItemCategory.Legging;
                case EquipmentSlotId.Boots: return item.category == InventoryItemCategory.Boots;
                case EquipmentSlotId.EarringLeft:
                case EquipmentSlotId.EarringRight: return item.category == InventoryItemCategory.Earring;
                case EquipmentSlotId.Necklace: return item.category == InventoryItemCategory.Necklace;
                case EquipmentSlotId.RingLeft:
                case EquipmentSlotId.RingRight: return item.category == InventoryItemCategory.Ring;
                case EquipmentSlotId.Bracelet: return item.category == InventoryItemCategory.Bracelet;
                case EquipmentSlotId.Amulet: return item.category == InventoryItemCategory.Amulet;
                case EquipmentSlotId.Accessory1:
                case EquipmentSlotId.Accessory2:
                case EquipmentSlotId.Accessory3:
                case EquipmentSlotId.Accessory4:
                case EquipmentSlotId.Accessory5: return item.category == InventoryItemCategory.Accessory;
                default: return false;
            }
        }

        public static bool IsWeaponSlot(EquipmentSlotId slot)
        {
            return slot == EquipmentSlotId.Weapon1 || slot == EquipmentSlotId.Weapon2 || slot == EquipmentSlotId.Weapon3;
        }

        public static string GetSlotLabel(EquipmentSlotId slot)
        {
            switch (slot)
            {
                case EquipmentSlotId.Weapon1: return "Weapon 1";
                case EquipmentSlotId.Weapon2: return "Weapon 2";
                case EquipmentSlotId.Weapon3: return "Weapon 3";
                case EquipmentSlotId.Helmet: return "Helmet";
                case EquipmentSlotId.Chestplate: return "Chestplate";
                case EquipmentSlotId.Legging: return "Legging";
                case EquipmentSlotId.Boots: return "Boots";
                case EquipmentSlotId.EarringLeft: return "Earring L";
                case EquipmentSlotId.EarringRight: return "Earring R";
                case EquipmentSlotId.Necklace: return "Necklace";
                case EquipmentSlotId.RingLeft: return "Ring L";
                case EquipmentSlotId.RingRight: return "Ring R";
                case EquipmentSlotId.Bracelet: return "Bracelet";
                case EquipmentSlotId.Amulet: return "Amulet";
                case EquipmentSlotId.Accessory1: return "Accessory 1";
                case EquipmentSlotId.Accessory2: return "Accessory 2";
                case EquipmentSlotId.Accessory3: return "Accessory 3";
                case EquipmentSlotId.Accessory4: return "Accessory 4";
                case EquipmentSlotId.Accessory5: return "Accessory 5";
                default: return slot.ToString();
            }
        }

        private int AddItemInternal(InventoryItemDefinition item, int amount)
        {
            if (item == null || amount <= 0) return 0;
            int remaining = amount;
            int cap = Mathf.Min(AbsoluteMaxStack, item.MaxStack);

            for (int i = 0; i < inventorySlots.Length && remaining > 0; i++)
            {
                InventoryStack stack = inventorySlots[i];
                if (stack == null || stack.IsEmpty || stack.item != item || stack.count >= cap) continue;
                int moved = Math.Min(cap - stack.count, remaining);
                stack.count += moved;
                remaining -= moved;
            }

            for (int i = 0; i < inventorySlots.Length && remaining > 0; i++)
            {
                if (inventorySlots[i] != null && !inventorySlots[i].IsEmpty) continue;
                int moved = Math.Min(cap, remaining);
                inventorySlots[i] = new InventoryStack(item, moved);
                remaining -= moved;
            }
            return amount - remaining;
        }

        private void EnsureArrays()
        {
            int equipmentCount = Enum.GetValues(typeof(EquipmentSlotId)).Length;
            if (inventorySlots == null) inventorySlots = new InventoryStack[InventorySlotCount];
            if (inventorySlots.Length != InventorySlotCount)
            {
                InventoryStack[] resized = new InventoryStack[InventorySlotCount];
                Array.Copy(inventorySlots, resized, Math.Min(inventorySlots.Length, resized.Length));
                inventorySlots = resized;
            }
            if (equipmentSlots == null) equipmentSlots = new InventoryStack[equipmentCount];
            if (equipmentSlots.Length != equipmentCount)
            {
                InventoryStack[] resized = new InventoryStack[equipmentCount];
                Array.Copy(equipmentSlots, resized, Math.Min(equipmentSlots.Length, resized.Length));
                equipmentSlots = resized;
            }
            activeWeaponIndex = Mathf.Clamp(activeWeaponIndex, 0, WeaponSlotCount - 1);

            for (int i = 0; i < inventorySlots.Length; i++) NormalizeStack(ref inventorySlots[i]);
            for (int i = 0; i < equipmentSlots.Length; i++) NormalizeStack(ref equipmentSlots[i]);
        }

        private static void NormalizeStack(ref InventoryStack stack)
        {
            if (stack == null || stack.item == null || stack.count <= 0)
            {
                stack = null;
                return;
            }
            stack.count = Mathf.Clamp(stack.count, 1, Mathf.Min(AbsoluteMaxStack, stack.item.MaxStack));
        }

        private static InventoryStack[] CopyArray(InventoryStack[] source)
        {
            InventoryStack[] copy = new InventoryStack[source.Length];
            for (int i = 0; i < source.Length; i++) copy[i] = source[i] == null ? null : source[i].Copy();
            return copy;
        }

        private static bool ValidInventoryIndex(int index) => index >= 0 && index < InventorySlotCount;

        private void NotifyActiveWeaponIfChanged(EquipmentSlotId changedSlot)
        {
            if (IsWeaponSlot(changedSlot) && (int)changedSlot == activeWeaponIndex)
                NotifyActiveWeaponChanged();
        }

        private void NotifyActiveWeaponChanged()
        {
            ActiveWeaponChanged?.Invoke(activeWeaponIndex, ActiveWeapon);
        }
    }
}

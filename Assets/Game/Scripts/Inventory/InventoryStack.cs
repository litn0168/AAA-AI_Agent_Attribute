using System;

namespace DarkRealms.Inventory
{
    [Serializable]
    public sealed class InventoryStack
    {
        public InventoryItemDefinition item;
        public int count;

        public InventoryStack() { }

        public InventoryStack(InventoryItemDefinition item, int count)
        {
            this.item = item;
            this.count = count;
        }

        public bool IsEmpty => item == null || count <= 0;

        public InventoryStack Copy()
        {
            return IsEmpty ? null : new InventoryStack(item, count);
        }
    }
}

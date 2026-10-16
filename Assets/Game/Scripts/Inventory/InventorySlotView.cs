using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace DarkRealms.Inventory
{
    /// <summary>Visual state the controller or a drag can push onto one slot.</summary>
    public enum SlotHighlight
    {
        None,
        ValidTarget,
        InvalidTarget
    }

    /// <summary>
    /// One inventory cell or one equipment slot: renders the stack the model holds and turns pointer input into
    /// model calls.
    ///
    /// The view never edits a stack itself. Every interaction is translated into an
    /// <see cref="InventoryModel"/> call and the slot is redrawn from the model afterwards, so a refusal from the
    /// model leaves both the data and the picture untouched.
    ///
    /// A drag records the item it started with. If the source stack no longer holds that item when the drop
    /// lands - because a sort, an auto-stack or another transfer changed it mid-drag - the drop is refused instead
    /// of trusting an index that has moved on.
    /// </summary>
    public sealed class InventorySlotView : MonoBehaviour, IBeginDragHandler, IDragHandler, IEndDragHandler,
        IDropHandler, IPointerClickHandler, IPointerEnterHandler, IPointerExitHandler
    {
        private static readonly List<RaycastResult> RaycastHits = new List<RaycastResult>();

        private static InventorySlotView dragSource;
        private static InventoryItemDefinition dragItem;
        private static InventorySlotView dragHoverTarget;
        private static bool dropConsumed;

        /// <summary>Delay before a hover turns into a tooltip, so sweeping the mouse over the grid stays quiet.</summary>
        public const float TooltipHoverDelay = 0.4f;

        /// <summary>Window in which a second left click counts as a double click.</summary>
        public const float DoubleClickWindow = 0.32f;

        private static readonly Color EmptyColor = new Color(0.085f, 0.095f, 0.11f, 0.92f);
        private static readonly Color FilledColor = new Color(0.18f, 0.20f, 0.23f, 0.98f);
        private static readonly Color HoverColor = new Color(0.25f, 0.28f, 0.33f, 1f);
        private static readonly Color SelectedColor = new Color(0.34f, 0.30f, 0.18f, 1f);
        private static readonly Color ActiveWeaponColor = new Color(0.53f, 0.36f, 0.14f, 1f);
        private static readonly Color ValidTargetColor = new Color(0.16f, 0.36f, 0.20f, 1f);
        private static readonly Color InvalidTargetColor = new Color(0.42f, 0.15f, 0.15f, 1f);
        private static readonly Color DragSourceColor = new Color(0.12f, 0.13f, 0.15f, 0.55f);

        [SerializeField] private Image background;
        [SerializeField] private Image iconImage;
        [SerializeField] private Text itemLabel;
        [SerializeField] private Text countLabel;
        [SerializeField] private Button button;

        private InventoryUIController owner;
        private bool isEquipment;
        private int inventoryIndex;
        private EquipmentSlotId equipmentSlot;
        private string emptyLabel;
        private Image dragImage;
        private CanvasGroup canvasGroup;

        private SlotHighlight highlight = SlotHighlight.None;
        private bool hovered;
        private bool selected;
        private bool filteredOut;
        private float hoverStartedAt = -1f;
        private bool tooltipRequested;
        private float lastLeftClickTime = -10f;
        private Vector2 lastPointerPosition;

        public bool IsEquipment => isEquipment;
        public int InventoryIndex => inventoryIndex;
        public EquipmentSlotId EquipmentSlot => equipmentSlot;
        public SlotHighlight Highlight => highlight;
        public bool IsHovered => hovered;
        public bool IsSelected => selected;
        public bool IsFilteredOut => filteredOut;
        public Vector2 LastPointerPosition => lastPointerPosition;

        /// <summary>Item currently shown in this slot, or null when empty.</summary>
        public InventoryItemDefinition Item
        {
            get
            {
                InventoryStack stack = ReadStack();
                return stack != null && !stack.IsEmpty ? stack.item : null;
            }
        }

        /// <summary>Item the slot showed when the active drag started, or null when no drag started here.</summary>
        public InventoryItemDefinition DragItem => dragSource == this ? dragItem : null;

        public static bool IsDragging => dragSource != null;

        /// <summary>
        /// Ends any drag in flight and clears its highlight. Called when the bag closes, when the layout changes
        /// under a drag, or when a drop is refused, so no half-finished drag can leave an item stuck to the cursor
        /// or hold on to an index that other code has since rewritten.
        /// </summary>
        public static void CancelActiveDrag()
        {
            if (dragHoverTarget != null)
            {
                dragHoverTarget.highlight = SlotHighlight.None;
                dragHoverTarget.ApplyColors();
                dragHoverTarget = null;
            }

            if (dragSource != null)
            {
                if (dragSource.dragImage != null)
                    Destroy(dragSource.dragImage.gameObject);
                dragSource.dragImage = null;
                InventorySlotView previous = dragSource;
                dragSource = null;
                dragItem = null;
                previous.ApplyColors();
            }

            dropConsumed = false;
        }

        public void ConfigureInventory(InventoryUIController controller, int index)
        {
            owner = controller;
            isEquipment = false;
            inventoryIndex = index;
            equipmentSlot = default;
            emptyLabel = string.Empty;
            HookButton();
            Refresh();
        }

        public void ConfigureEquipment(InventoryUIController controller, EquipmentSlotId slot)
        {
            owner = controller;
            isEquipment = true;
            equipmentSlot = slot;
            inventoryIndex = -1;
            emptyLabel = InventoryModel.GetSlotLabel(slot);
            HookButton();
            Refresh();
        }

        private void HookButton()
        {
            if (button == null)
                return;

            button.onClick.RemoveListener(HandleClick);
            button.onClick.AddListener(HandleClick);
        }

        public void SetReferences(Image slotBackground, Image icon, Text label, Text count, Button slotButton)
        {
            background = slotBackground;
            iconImage = icon;
            itemLabel = label;
            countLabel = count;
            button = slotButton;
        }

        public void SetSelected(bool value)
        {
            if (selected == value)
                return;

            selected = value;
            ApplyColors();
        }

        /// <summary>
        /// Hides a slot from the current search/filter without moving it. The slot keeps its index, so the view
        /// grid and the model grid stay the same 6x9 layout and a drag can never act on a stale view index.
        /// </summary>
        public void SetFiltered(bool value)
        {
            if (filteredOut == value)
                return;

            filteredOut = value;
            if (canvasGroup == null)
            {
                canvasGroup = GetComponent<CanvasGroup>();
                if (canvasGroup == null)
                    canvasGroup = gameObject.AddComponent<CanvasGroup>();
            }

            canvasGroup.alpha = value ? 0.30f : 1f;
            canvasGroup.blocksRaycasts = !value;
            ApplyColors();
        }

        public void SetHighlight(SlotHighlight value)
        {
            if (highlight == value)
                return;

            highlight = value;
            ApplyColors();
        }

        public void Refresh()
        {
            if (owner == null || owner.Model == null)
                return;

            InventoryStack stack = ReadStack();
            bool hasItem = stack != null && !stack.IsEmpty;

            if (iconImage != null)
            {
                iconImage.sprite = hasItem ? stack.item.icon : null;
                iconImage.enabled = hasItem && stack.item.icon != null;
                iconImage.color = Color.white;
            }

            if (itemLabel != null)
            {
                itemLabel.text = hasItem ? stack.item.displayName : emptyLabel;
                itemLabel.color = hasItem ? new Color(0.94f, 0.91f, 0.82f, 1f) : new Color(0.57f, 0.59f, 0.62f, 1f);
            }

            if (countLabel != null)
                countLabel.text = hasItem && stack.count > 1 ? stack.count.ToString() : string.Empty;

            ApplyColors();
        }

        private InventoryStack ReadStack()
        {
            if (owner == null || owner.Model == null)
                return null;

            return isEquipment
                ? owner.Model.GetEquipmentSlot(equipmentSlot)
                : owner.Model.GetInventorySlot(inventoryIndex);
        }

        private void ApplyColors()
        {
            if (background == null)
                return;

            bool hasItem = Item != null;
            bool activeWeapon = isEquipment && InventoryModel.IsWeaponSlot(equipmentSlot) &&
                                owner != null && owner.Model != null &&
                                (int)equipmentSlot == owner.Model.ActiveWeaponIndex;

            Color color;
            if (dragSource == this)
                color = DragSourceColor;
            else if (highlight == SlotHighlight.ValidTarget)
                color = ValidTargetColor;
            else if (highlight == SlotHighlight.InvalidTarget)
                color = InvalidTargetColor;
            else if (activeWeapon)
                color = ActiveWeaponColor;
            else if (selected && hasItem)
                color = SelectedColor;
            else if (hovered && hasItem)
                color = HoverColor;
            else
                color = hasItem ? FilledColor : EmptyColor;

            background.color = color;
        }

        private void Update()
        {
            if (!hovered || tooltipRequested || owner == null || dragSource != null)
                return;

            if (hoverStartedAt < 0f)
                return;

            if (Time.unscaledTime - hoverStartedAt < TooltipHoverDelay)
                return;

            tooltipRequested = true;
            owner.RequestTooltip(this, lastPointerPosition);
        }

        private void OnDisable()
        {
            // Closing the window (or destroying the canvas) reaches every slot through OnDisable, which is the one
            // place guaranteed to run while a drag might still be in flight.
            if (dragSource == this)
                CancelActiveDrag();

            hovered = false;
            tooltipRequested = false;
            hoverStartedAt = -1f;

            if (dragImage != null)
            {
                Destroy(dragImage.gameObject);
                dragImage = null;
            }
        }

        // ------------------------------------------------------------------ pointer hover

        public void OnPointerEnter(PointerEventData eventData)
        {
            hovered = true;
            hoverStartedAt = Time.unscaledTime;
            tooltipRequested = false;
            lastPointerPosition = eventData.position;
            ApplyColors();
        }

        public void OnPointerExit(PointerEventData eventData)
        {
            hovered = false;
            hoverStartedAt = -1f;
            tooltipRequested = false;
            ApplyColors();

            if (owner != null)
                owner.HideTooltip(this);
        }

        // ------------------------------------------------------------------ drag and drop

        public void OnBeginDrag(PointerEventData eventData)
        {
            InventoryStack stack = ReadStack();
            if (stack == null || stack.IsEmpty || owner == null || owner.Model == null)
                return;

            dragSource = this;
            dragItem = stack.item;
            dropConsumed = false;
            dragHoverTarget = null;

            dragImage = owner.CreateDragVisual(stack.item.icon, stack.item.displayName);
            MoveDragVisual(eventData);

            // A tooltip following a dragged item is noise, and the hover that opened it has already ended.
            owner.HideTooltip(this);
            owner.ShowStatus(string.Empty);
            ApplyColors();
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (dragSource != this)
                return;

            lastPointerPosition = eventData.position;
            MoveDragVisual(eventData);
            UpdateDropTarget(eventData);
        }

        public void OnEndDrag(PointerEventData eventData)
        {
            if (dragSource != this)
                return;

            bool wasConsumed = dropConsumed;

            if (dragHoverTarget != null)
            {
                dragHoverTarget.highlight = SlotHighlight.None;
                dragHoverTarget.ApplyColors();
                dragHoverTarget = null;
            }

            if (dragImage != null)
                Destroy(dragImage.gameObject);
            dragImage = null;
            dragSource = null;
            dragItem = null;
            dropConsumed = false;
            ApplyColors();

            if (!wasConsumed && owner != null)
                owner.ShowStatus("Đã huỷ thao tác kéo: thả vào ô trống trong túi hoặc ô trang bị hợp lệ.");
        }

        private void UpdateDropTarget(PointerEventData eventData)
        {
            var target = FindSlotUnderPointer(eventData);
            if (target != dragHoverTarget)
            {
                if (dragHoverTarget != null)
                {
                    dragHoverTarget.highlight = SlotHighlight.None;
                    dragHoverTarget.ApplyColors();
                }

                dragHoverTarget = target;
            }

            if (target == null)
                return;

            if (target == this)
            {
                target.SetHighlight(SlotHighlight.None);
                return;
            }

            // Equipment cells carry -1 as their bag index, so the slot index handed to the model has to be the
            // equipment slot's own enum value; passing -1 made every equipment target look invalid.
            int sourceIndex = isEquipment ? (int)equipmentSlot : inventoryIndex;
            int targetIndex = target.isEquipment ? (int)target.equipmentSlot : target.inventoryIndex;

            bool valid = owner != null && owner.Model != null &&
                         owner.Model.CanPlace(sourceIndex, isEquipment, targetIndex, target.isEquipment);
            target.SetHighlight(valid ? SlotHighlight.ValidTarget : SlotHighlight.InvalidTarget);
        }

        public void OnDrop(PointerEventData eventData)
        {
            if (owner == null || owner.Model == null || dragSource == null || dragSource == this)
                return;

            dropConsumed = true;
            InventorySlotView source = dragSource;
            InventoryModel model = owner.Model;

            // The drag is only valid for the stack it started on. Anything that changed that stack in the meantime
            // (sort, auto-stack, another transfer, a split) invalidates the drop rather than moving a surprise item.
            if (source.Item == null || source.Item != dragItem)
            {
                owner.ShowStatus("Vật phẩm nguồn đã thay đổi trong lúc kéo — thao tác đã huỷ.");
                CancelActiveDrag();
                return;
            }

            bool changed;
            if (!source.isEquipment && !isEquipment)
                changed = model.MoveInventorySlots(source.inventoryIndex, inventoryIndex);
            else if (!source.isEquipment && isEquipment)
                changed = model.TryEquipInventoryItem(source.inventoryIndex, equipmentSlot);
            else if (source.isEquipment && !isEquipment)
                changed = model.TryUnequipToInventory(source.equipmentSlot, inventoryIndex);
            else
                changed = model.TryMoveEquipment(source.equipmentSlot, equipmentSlot);

            owner.ShowStatus(changed ? owner.DescribeTransfer(source, this) : "Không thể đặt vật phẩm vào ô này.");
        }

        private static InventorySlotView FindSlotUnderPointer(PointerEventData eventData)
        {
            if (EventSystem.current == null)
                return null;

            RaycastHits.Clear();
            EventSystem.current.RaycastAll(eventData, RaycastHits);
            foreach (RaycastResult hit in RaycastHits)
            {
                if (hit.gameObject == null)
                    continue;

                var slot = hit.gameObject.GetComponentInParent<InventorySlotView>();
                if (slot != null)
                    return slot;
            }

            return null;
        }

        private void MoveDragVisual(PointerEventData eventData)
        {
            if (dragImage == null || owner == null)
                return;

            RectTransform canvasRect = owner.CanvasRect;
            if (canvasRect != null && RectTransformUtility.ScreenPointToLocalPointInRectangle(
                    canvasRect, eventData.position, eventData.pressEventCamera, out Vector2 localPoint))
                dragImage.rectTransform.anchoredPosition = localPoint;
        }

        // ------------------------------------------------------------------ clicks

        public void OnPointerClick(PointerEventData eventData)
        {
            if (owner == null || owner.Model == null)
                return;

            lastPointerPosition = eventData.position;

            if (eventData.button == PointerEventData.InputButton.Right)
            {
                owner.OpenContextMenu(this, eventData.position);
                return;
            }

            if (eventData.button != PointerEventData.InputButton.Left)
                return;

            // Shift-click is the one-step quick move: into the bag's matching equipment slot, or back out of it.
            if (IsShiftHeld())
            {
                owner.QuickMove(this);
                lastLeftClickTime = -10f;
                return;
            }

            bool doubleClick = Time.unscaledTime - lastLeftClickTime < DoubleClickWindow;
            lastLeftClickTime = Time.unscaledTime;

            if (doubleClick)
            {
                owner.QuickMove(this);
                lastLeftClickTime = -10f;
                return;
            }

            owner.SelectSlot(this);
            if (isEquipment && InventoryModel.IsWeaponSlot(equipmentSlot))
                owner.Model.SelectWeaponSlot((int)equipmentSlot);
        }

        private static bool IsShiftHeld()
        {
            Keyboard keyboard = Keyboard.current;
            return keyboard != null && (keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed);
        }

        private void HandleClick()
        {
            // The Button component consumes the click before OnPointerClick for some pointer sources, so weapon
            // slots keep their selection behaviour here as well.
            if (isEquipment && InventoryModel.IsWeaponSlot(equipmentSlot) && owner != null && owner.Model != null)
                owner.Model.SelectWeaponSlot((int)equipmentSlot);
        }
    }
}

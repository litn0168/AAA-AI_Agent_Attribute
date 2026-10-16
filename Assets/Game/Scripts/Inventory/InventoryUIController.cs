using System;
using System.Collections.Generic;
using DarkRealms.UI;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace DarkRealms.Inventory
{
    [Serializable] public sealed class InventoryVisibilityEvent : UnityEvent<bool> { }
    [Serializable] public sealed class ActiveWeaponUnityEvent : UnityEvent<InventoryItemDefinition> { }

    /// <summary>
    /// Builds a responsive runtime inventory/equipment UI. The data stays in InventoryModel and survives
    /// closing/reopening this panel. Install the module with the Dark Realms editor menu.
    ///
    /// Every mutation here goes through the model and the panel is then redrawn from the model, so a refused
    /// operation cannot leave the picture disagreeing with the data. Escape is NOT handled by this component's own
    /// bindings: PauseMenu owns the key and asks <see cref="HandleEscapePress"/> first, so one press performs
    /// exactly one action.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InventoryUIController : MonoBehaviour
    {
        private const float WindowWidth = 1200f;
        private const float WindowHeight = 950f;
        private const float PanelRowY = 20f;
        private const float ToolbarY = -368f;
        private const float StatusY = -424f;
        private const float ControlsY = -456f;

        [Header("References")]
        [SerializeField] private InventoryModel model;

        [Header("Input bindings (inspect existing project bindings before shipping)")]
        [SerializeField] private string primaryToggleBinding = "<Keyboard>/i";
        [SerializeField] private string alternateToggleBinding = "<Keyboard>/tab";
        [SerializeField] private string closeBinding = "<Keyboard>/escape";
        [Tooltip("Off by default: PauseMenu already owns Escape and routes it here through HandleEscapePress, " +
                 "so binding it twice would close the bag AND pause on one press. Switch this on only for a scene " +
                 "that has no PauseMenu and therefore no other owner of Escape.")]
        [SerializeField] private bool bindEscapeLocally;
        [SerializeField] private string[] weaponBindings = { "<Keyboard>/1", "<Keyboard>/2", "<Keyboard>/3" };
        [SerializeField] private bool pauseGameplayWhileOpen = false;

        [Header("Events")]
        public InventoryVisibilityEvent onVisibilityChanged = new InventoryVisibilityEvent();
        public ActiveWeaponUnityEvent onActiveWeaponChanged = new ActiveWeaponUnityEvent();

        private Canvas canvas;
        private GameObject window;
        private Text statusText;
        private Text filterLabel;
        private InputField searchField;
        private InventorySlotView[] inventoryViews;
        private readonly InventorySlotView[] equipmentViews = new InventorySlotView[Enum.GetValues(typeof(EquipmentSlotId)).Length];
        private InputAction toggleAction;
        private InputAction closeAction;
        private InputAction[] weaponActions;
        private bool isOpen;
        private float previousTimeScale = 1f;

        private InventoryItemTooltip tooltip;
        private InventorySlotView tooltipSlot;

        private GameObject contextMenu;
        private InventorySlotView contextSlot;
        private Button contextEquipButton;
        private Button contextSplitButton;
        private Button contextWeaponButton;
        private Text contextHint;

        private GameObject splitPrompt;
        private InputField splitField;
        private Text splitSummary;
        private int splitIndex = -1;

        private InventorySlotView selectedSlot;
        private string searchQuery = string.Empty;
        private int filterIndex;
        private readonly List<InventoryItemCategory?> filterCycle = BuildFilterCycle();

        public InventoryModel Model => model;
        public RectTransform CanvasRect => canvas != null ? canvas.transform as RectTransform : null;
        public Transform CanvasTransform => canvas != null ? canvas.transform : null;
        public bool IsOpen => isOpen;
        public bool IsSplitPromptOpen => splitPrompt != null && splitPrompt.activeSelf;
        public bool IsContextMenuOpen => contextMenu != null && contextMenu.activeSelf;
        public bool IsTooltipVisible => tooltip != null && tooltip.IsVisible;
        public string StatusMessage => statusText != null ? statusText.text : string.Empty;
        public string SearchQuery => searchQuery;
        public string FilterDescription => DescribeFilter();
        public InventoryItemCategory? ActiveCategoryFilter => filterCycle[filterIndex];
        public int SelectedSlotIndex => selectedSlot != null && !selectedSlot.IsEquipment ? selectedSlot.InventoryIndex : -1;
        public InventoryItemTooltip Tooltip => tooltip;
        public Transform WindowTransform => window != null ? window.transform : null;

        private void Awake()
        {
            if (model == null) model = GetComponent<InventoryModel>();
            if (model == null) model = FindAnyObjectByType<InventoryModel>();
            BuildInputActions();
            BuildInterface();
        }

        private void OnEnable()
        {
            EnableActions(true);
            if (model != null)
            {
                model.InventoryChanged += RefreshAll;
                model.EquipmentChanged += RefreshAll;
                model.ActiveWeaponChanged += HandleActiveWeaponChanged;
            }
        }

        private void Start()
        {
            SetOpen(false, false);
            RefreshAll();
        }

        private void OnDisable()
        {
            if (model != null)
            {
                model.InventoryChanged -= RefreshAll;
                model.EquipmentChanged -= RefreshAll;
                model.ActiveWeaponChanged -= HandleActiveWeaponChanged;
            }
            EnableActions(false);
            if (isOpen && pauseGameplayWhileOpen) Time.timeScale = previousTimeScale;
            isOpen = false;
        }

        private void OnDestroy()
        {
            toggleAction?.Dispose();
            closeAction?.Dispose();
            if (weaponActions != null)
                foreach (InputAction action in weaponActions) action?.Dispose();
        }

        private void BuildInputActions()
        {
            toggleAction = new InputAction("Toggle Inventory", InputActionType.Button);
            if (!string.IsNullOrWhiteSpace(primaryToggleBinding)) toggleAction.AddBinding(primaryToggleBinding);
            if (!string.IsNullOrWhiteSpace(alternateToggleBinding)) toggleAction.AddBinding(alternateToggleBinding);
            toggleAction.performed += _ => Toggle();

            closeAction = new InputAction("Close Inventory", InputActionType.Button, closeBinding);
            closeAction.performed += _ => { if (isOpen) SetOpen(false); };

            weaponActions = new InputAction[InventoryModel.WeaponSlotCount];
            for (int i = 0; i < weaponActions.Length; i++)
            {
                int captured = i;
                string binding = weaponBindings != null && i < weaponBindings.Length
                    ? weaponBindings[i]
                    : "<Keyboard>/" + (i + 1);
                weaponActions[i] = new InputAction("Select Weapon " + (i + 1), InputActionType.Button, binding);
                weaponActions[i].performed += _ =>
                {
                    if (TypingInField()) return;
                    if (model != null) model.SelectWeaponSlot(captured);
                };
            }
        }

        private void EnableActions(bool enable)
        {
            if (toggleAction == null) return;
            if (enable)
            {
                toggleAction.Enable();
                // Escape stays with PauseMenu unless this scene has no other owner of it.
                if (bindEscapeLocally) closeAction.Enable();
                if (weaponActions != null) foreach (InputAction action in weaponActions) action.Enable();
            }
            else
            {
                toggleAction.Disable();
                closeAction.Disable();
                if (weaponActions != null) foreach (InputAction action in weaponActions) action.Disable();
            }
        }

        /// <summary>
        /// True while a text field is taking keyboard input. Without this, typing "i" in the search box would close
        /// the bag and typing "1" while splitting a stack would also switch weapons.
        /// </summary>
        private bool TypingInField()
        {
            if (IsSplitPromptOpen)
                return true;

            EventSystem events = EventSystem.current;
            GameObject selected = events != null ? events.currentSelectedGameObject : null;
            return selected != null && selected.GetComponent<InputField>() != null;
        }

        /// <summary>
        /// Drops keyboard focus from this panel's own text fields. A field keeps the Event System's selection after
        /// its panel closes, and while that selection lasts the I/TAB toggle is treated as typing, which would leave
        /// the player unable to reopen the bag until they clicked somewhere else.
        /// </summary>
        private void ReleaseFieldFocus()
        {
            EventSystem events = EventSystem.current;
            GameObject selected = events != null ? events.currentSelectedGameObject : null;
            if (selected == null)
                return;

            bool isOurField = (searchField != null && selected == searchField.gameObject) ||
                              (splitField != null && selected == splitField.gameObject);
            if (isOurField)
                events.SetSelectedGameObject(null);
        }

        private void BuildInterface()
        {
            GameObject canvasObject = new GameObject("DarkRealms Inventory Canvas", typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);
            canvas = canvasObject.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            if (FindAnyObjectByType<EventSystem>() == null)
            {
                GameObject eventSystemObject = new GameObject("DarkRealms Inventory EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
                eventSystemObject.transform.SetParent(transform, false);
            }

            window = MakePanel("Inventory Window", canvasObject.transform, new Vector2(WindowWidth, WindowHeight), Vector2.zero,
                new Color(0.045f, 0.052f, 0.063f, 0.97f));
            MakeText("Title", window.transform, "INVENTORY & EQUIPMENT", 30, TextAnchor.MiddleLeft,
                new Vector2(-180f, 432f), new Vector2(720f, 48f), new Color(0.91f, 0.82f, 0.61f, 1f));

            Button close = MakeButton("Close", window.transform, "X", 24, new Vector2(548f, 432f), new Vector2(48f, 44f));
            close.onClick.AddListener(() => SetOpen(false));

            BuildEquipmentPanel(window.transform);
            BuildInventoryPanel(window.transform);
            BuildToolbar(window.transform);
            BuildSplitPrompt();
            BuildContextMenu();

            tooltip = gameObject.AddComponent<InventoryItemTooltip>();
            tooltip.Initialize(canvas, model);

            statusText = MakeText("Status", window.transform, string.Empty, 16, TextAnchor.MiddleCenter,
                new Vector2(0f, StatusY), new Vector2(1120f, 28f), new Color(0.88f, 0.69f, 0.42f, 1f));
            MakeText("Controls", window.transform,
                "I / TAB: mở-đóng    1-3: chọn vũ khí    Kéo-thả: di chuyển / trang bị    Chuột phải: menu    Double-click hoặc Shift-click: trang bị nhanh    ESC: đóng túi",
                16, TextAnchor.MiddleCenter, new Vector2(0f, ControlsY), new Vector2(1120f, 26f), new Color(0.63f, 0.66f, 0.70f, 1f));
        }

        private void BuildEquipmentPanel(Transform parent)
        {
            GameObject panel = MakePanel("Equipment Panel", parent, new Vector2(410f, 710f), new Vector2(-365f, PanelRowY),
                new Color(0.075f, 0.083f, 0.095f, 0.97f));
            MakeText("Equipment Header", panel.transform, "EQUIPMENT", 23, TextAnchor.MiddleCenter,
                new Vector2(0f, 322f), new Vector2(360f, 36f), new Color(0.84f, 0.76f, 0.60f, 1f));

            AddEquipmentSlot(panel.transform, EquipmentSlotId.Weapon1, new Vector2(-112f, 225f), new Vector2(78f, 82f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Weapon2, new Vector2(0f, 225f), new Vector2(78f, 82f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Weapon3, new Vector2(112f, 225f), new Vector2(78f, 82f));

            MakeText("Armor Label", panel.transform, "ARMOR", 16, TextAnchor.MiddleCenter,
                new Vector2(0f, 158f), new Vector2(340f, 25f), new Color(0.70f, 0.73f, 0.77f, 1f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Helmet, new Vector2(-85f, 98f), new Vector2(78f, 78f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Chestplate, new Vector2(85f, 98f), new Vector2(78f, 78f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Legging, new Vector2(-85f, 8f), new Vector2(78f, 78f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Boots, new Vector2(85f, 8f), new Vector2(78f, 78f));

            MakeText("Jewelry Label", panel.transform, "JEWELRY", 16, TextAnchor.MiddleCenter,
                new Vector2(0f, -48f), new Vector2(340f, 24f), new Color(0.70f, 0.73f, 0.77f, 1f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.EarringLeft, new Vector2(-126f, -101f), new Vector2(66f, 68f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.EarringRight, new Vector2(-42f, -101f), new Vector2(66f, 68f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Necklace, new Vector2(42f, -101f), new Vector2(66f, 68f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.RingLeft, new Vector2(126f, -101f), new Vector2(66f, 68f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.RingRight, new Vector2(-84f, -183f), new Vector2(66f, 68f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Bracelet, new Vector2(0f, -183f), new Vector2(66f, 68f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Amulet, new Vector2(84f, -183f), new Vector2(66f, 68f));

            MakeText("Accessory Label", panel.transform, "ACCESSORIES (5)", 16, TextAnchor.MiddleCenter,
                new Vector2(0f, -235f), new Vector2(340f, 24f), new Color(0.70f, 0.73f, 0.77f, 1f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Accessory1, new Vector2(-144f, -290f), new Vector2(54f, 60f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Accessory2, new Vector2(-72f, -290f), new Vector2(54f, 60f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Accessory3, new Vector2(0f, -290f), new Vector2(54f, 60f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Accessory4, new Vector2(72f, -290f), new Vector2(54f, 60f));
            AddEquipmentSlot(panel.transform, EquipmentSlotId.Accessory5, new Vector2(144f, -290f), new Vector2(54f, 60f));
        }

        private void BuildInventoryPanel(Transform parent)
        {
            GameObject panel = MakePanel("Inventory Grid Panel", parent, new Vector2(735f, 710f), new Vector2(230f, PanelRowY),
                new Color(0.075f, 0.083f, 0.095f, 0.97f));
            MakeText("Bag Header", panel.transform, "BAG  ·  6 × 9", 23, TextAnchor.MiddleCenter,
                new Vector2(0f, 322f), new Vector2(640f, 36f), new Color(0.84f, 0.76f, 0.60f, 1f));

            GameObject gridObject = new GameObject("Inventory Slots 6x9", typeof(RectTransform), typeof(GridLayoutGroup));
            gridObject.transform.SetParent(panel.transform, false);
            RectTransform gridRect = gridObject.GetComponent<RectTransform>();
            gridRect.anchorMin = new Vector2(0.5f, 0.5f);
            gridRect.anchorMax = new Vector2(0.5f, 0.5f);
            gridRect.pivot = new Vector2(0.5f, 0.5f);
            gridRect.anchoredPosition = new Vector2(0f, -22f);
            gridRect.sizeDelta = new Vector2(442f, 658f);

            // Square cells in the 1920x1080 reference space; the CanvasScaler keeps them square at every
            // resolution, so the layout never needs a second set of hard-coded numbers.
            GridLayoutGroup grid = gridObject.GetComponent<GridLayoutGroup>();
            grid.cellSize = new Vector2(66f, 66f);
            grid.spacing = new Vector2(6f, 6f);
            grid.padding = new RectOffset(2, 2, 2, 2);
            grid.startCorner = GridLayoutGroup.Corner.UpperLeft;
            grid.startAxis = GridLayoutGroup.Axis.Horizontal;
            grid.childAlignment = TextAnchor.UpperLeft;
            grid.constraint = GridLayoutGroup.Constraint.FixedColumnCount;
            grid.constraintCount = InventoryModel.Columns;

            inventoryViews = new InventorySlotView[InventoryModel.InventorySlotCount];
            for (int i = 0; i < inventoryViews.Length; i++)
                inventoryViews[i] = MakeSlot("Inventory Slot " + (i + 1).ToString("00"), gridObject.transform, new Vector2(66f, 66f), i, null);
        }

        private void BuildToolbar(Transform parent)
        {
            GameObject toolbar = MakePanel("Inventory Toolbar", parent, new Vector2(1120f, 56f), new Vector2(0f, ToolbarY),
                new Color(0.06f, 0.067f, 0.078f, 0.97f));

            searchField = RuntimeUiFactory.InputField("Search Field", toolbar.transform, "Tìm theo tên hoặc ID…", 17,
                new Vector2(320f, 44f), new Vector2(-390f, 0f), UnityEngine.UI.InputField.ContentType.Standard, false);
            searchField.onValueChanged.AddListener(SetSearch);

            Button filter = RuntimeUiFactory.Button("Filter Button", toolbar.transform, "Lọc: Tất cả", 17,
                new Vector2(200f, 44f), new Vector2(-120f, 0f), new Color(0.16f, 0.18f, 0.22f, 1f), Color.white);
            filter.onClick.AddListener(CycleFilter);
            filterLabel = filter.GetComponentInChildren<Text>();

            Button autoStack = RuntimeUiFactory.Button("Auto Stack Button", toolbar.transform, "Gộp stack", 17,
                new Vector2(210f, 44f), new Vector2(115f, 0f), new Color(0.16f, 0.18f, 0.22f, 1f), Color.white);
            autoStack.onClick.AddListener(() => AutoStack());

            Button sort = RuntimeUiFactory.Button("Sort Button", toolbar.transform, "Sắp xếp", 17,
                new Vector2(170f, 44f), new Vector2(310f, 0f), new Color(0.16f, 0.18f, 0.22f, 1f), Color.white);
            sort.onClick.AddListener(() => SortBag());
        }

        private void BuildSplitPrompt()
        {
            splitPrompt = MakePanel("Split Stack Prompt", canvas.transform, new Vector2(1920f, 1080f), Vector2.zero,
                new Color(0f, 0f, 0f, 0.55f));
            RuntimeUiFactory.StretchFull(splitPrompt.GetComponent<RectTransform>());
            splitPrompt.GetComponent<Image>().raycastTarget = true;

            // The card is centred inside a full-screen dimmer, which also keeps stray clicks off the bag behind it.
            GameObject card = RuntimeUiFactory.Panel("Split Card", splitPrompt.transform, new Vector2(520f, 320f), Vector2.zero,
                new Color(0.055f, 0.062f, 0.075f, 0.99f));

            RuntimeUiFactory.Text("Split Title", card.transform, "TÁCH STACK", 28, TextAnchor.MiddleCenter,
                new Vector2(460f, 40f), new Vector2(0f, 118f), new Color(0.91f, 0.82f, 0.61f, 1f));
            splitSummary = RuntimeUiFactory.Text("Split Summary", card.transform, string.Empty, 18, TextAnchor.MiddleCenter,
                new Vector2(460f, 46f), new Vector2(0f, 70f), new Color(0.72f, 0.75f, 0.80f, 1f));

            splitField = RuntimeUiFactory.InputField("Split Amount", card.transform, "1", 22,
                new Vector2(160f, 52f), new Vector2(0f, 12f), UnityEngine.UI.InputField.ContentType.IntegerNumber, true);
            splitField.characterLimit = 3;

            Button confirm = RuntimeUiFactory.Button("Split Confirm", card.transform, "Tách", 20,
                new Vector2(180f, 50f), new Vector2(-100f, -78f), new Color(0.24f, 0.15f, 0.13f, 1f), Color.white);
            confirm.onClick.AddListener(() => ConfirmSplit());

            Button cancel = RuntimeUiFactory.Button("Split Cancel", card.transform, "Huỷ  (Esc)", 20,
                new Vector2(180f, 50f), new Vector2(100f, -78f), new Color(0.16f, 0.18f, 0.22f, 1f), Color.white);
            cancel.onClick.AddListener(CancelSplit);

            splitPrompt.SetActive(false);
        }

        private void BuildContextMenu()
        {
            contextMenu = MakePanel("Inventory Context Menu", canvas.transform, new Vector2(1920f, 1080f), Vector2.zero,
                new Color(0f, 0f, 0f, 0f));
            RuntimeUiFactory.StretchFull(contextMenu.GetComponent<RectTransform>());

            // A click anywhere outside the menu closes it; the invisible blocker is that click target.
            GameObject blocker = MakePanel("Blocker", contextMenu.transform, new Vector2(1920f, 1080f), Vector2.zero,
                new Color(0f, 0f, 0f, 0f));
            RuntimeUiFactory.StretchFull(blocker.GetComponent<RectTransform>());
            Button blockerButton = blocker.AddComponent<Button>();
            blockerButton.transition = Selectable.Transition.None;
            blockerButton.onClick.AddListener(CloseContextMenu);

            GameObject card = RuntimeUiFactory.Panel("Menu Card", contextMenu.transform, new Vector2(340f, 260f), Vector2.zero,
                new Color(0.055f, 0.062f, 0.075f, 0.99f));

            contextEquipButton = RuntimeUiFactory.Button("Menu Equip", card.transform, "Trang bị nhanh", 19,
                new Vector2(300f, 46f), new Vector2(0f, 84f), new Color(0.16f, 0.18f, 0.22f, 1f), Color.white);
            contextEquipButton.onClick.AddListener(() => { InventorySlotView slot = contextSlot; CloseContextMenu(); QuickMove(slot); });

            contextSplitButton = RuntimeUiFactory.Button("Menu Split", card.transform, "Tách stack…", 19,
                new Vector2(300f, 46f), new Vector2(0f, 30f), new Color(0.16f, 0.18f, 0.22f, 1f), Color.white);
            contextSplitButton.onClick.AddListener(() => { InventorySlotView slot = contextSlot; CloseContextMenu(); OpenSplitPrompt(slot); });

            contextWeaponButton = RuntimeUiFactory.Button("Menu Weapon", card.transform, "Chọn làm vũ khí đang dùng", 17,
                new Vector2(300f, 46f), new Vector2(0f, -24f), new Color(0.16f, 0.18f, 0.22f, 1f), Color.white);
            contextWeaponButton.onClick.AddListener(() =>
            {
                InventorySlotView slot = contextSlot;
                CloseContextMenu();
                if (slot != null && slot.IsEquipment && model != null)
                    model.SelectWeaponSlot((int)slot.EquipmentSlot);
            });

            contextHint = RuntimeUiFactory.Text("Menu Hint", card.transform, string.Empty, 17, TextAnchor.MiddleCenter,
                new Vector2(300f, 46f), new Vector2(0f, 20f), new Color(0.66f, 0.69f, 0.74f, 1f));

            Button close = RuntimeUiFactory.Button("Menu Close", card.transform, "Đóng", 19,
                new Vector2(300f, 46f), new Vector2(0f, -84f), new Color(0.20f, 0.13f, 0.12f, 1f), Color.white);
            close.onClick.AddListener(CloseContextMenu);

            contextMenu.SetActive(false);
        }

        private void AddEquipmentSlot(Transform parent, EquipmentSlotId slot, Vector2 position, Vector2 size)
        {
            InventorySlotView view = MakeSlot(InventoryModel.GetSlotLabel(slot), parent, size, -1, slot);
            RectTransform rect = view.transform as RectTransform;
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            equipmentViews[(int)slot] = view;
        }

        private InventorySlotView MakeSlot(string objectName, Transform parent, Vector2 size, int index, EquipmentSlotId? equipmentSlot)
        {
            GameObject slotObject = new GameObject(objectName, typeof(RectTransform), typeof(Image), typeof(Button), typeof(InventorySlotView));
            slotObject.transform.SetParent(parent, false);
            RectTransform rect = slotObject.GetComponent<RectTransform>();
            rect.sizeDelta = size;

            Image background = slotObject.GetComponent<Image>();
            background.color = new Color(0.085f, 0.095f, 0.11f, 0.92f);
            Button button = slotObject.GetComponent<Button>();
            button.targetGraphic = background;
            button.transition = Selectable.Transition.None;

            Image icon = MakeImage("Icon", slotObject.transform, null, new Vector2(0f, 6f), new Vector2(size.x - 12f, size.y - 20f));
            Text label = MakeText("Item Name", slotObject.transform, string.Empty, size.x < 60f ? 9 : 11,
                TextAnchor.LowerCenter, new Vector2(0f, 3f), new Vector2(size.x - 4f, 23f), new Color(0.94f, 0.91f, 0.82f, 1f));
            Text count = MakeText("Stack Count", slotObject.transform, string.Empty, 14,
                TextAnchor.LowerRight, new Vector2(-5f, 4f), new Vector2(size.x - 10f, 19f), Color.white);

            InventorySlotView view = slotObject.GetComponent<InventorySlotView>();
            view.SetReferences(background, icon, label, count, button);
            if (equipmentSlot.HasValue) view.ConfigureEquipment(this, equipmentSlot.Value);
            else view.ConfigureInventory(this, index);
            return view;
        }

        // ------------------------------------------------------------------ open / close

        public void Toggle()
        {
            if (TypingInField())
                return;

            SetOpen(!isOpen);
        }

        public void SetOpen(bool open, bool invokeEvent = true)
        {
            if (isOpen == open && window != null && window.activeSelf == open) return;
            isOpen = open;
            if (window != null) window.SetActive(open);

            if (!open)
            {
                // Nothing half-finished may survive the window: no drag, no tooltip, no popup, no stale selection.
                InventorySlotView.CancelActiveDrag();
                HideTooltip();
                CloseContextMenu();
                CloseSplitPrompt();
                SelectSlot(null);
                ReleaseFieldFocus();
            }

            if (pauseGameplayWhileOpen)
            {
                if (open)
                {
                    previousTimeScale = Time.timeScale;
                    Time.timeScale = 0f;
                }
                else
                {
                    Time.timeScale = previousTimeScale;
                }
            }

            if (open) RefreshAll();
            if (invokeEvent) onVisibilityChanged?.Invoke(open);
        }

        /// <summary>
        /// Handles one Escape press while this panel is open and reports whether it consumed the press. Called by
        /// <see cref="PauseMenu"/>, the single owner of Escape, so that one press performs exactly one action: a
        /// popup closes, or the bag closes, and the pause menu does not also toggle in the same press.
        /// </summary>
        public bool HandleEscapePress()
        {
            if (IsSplitPromptOpen)
            {
                CancelSplit();
                return true;
            }

            if (IsContextMenuOpen)
            {
                CloseContextMenu();
                return true;
            }

            if (!isOpen)
                return false;

            SetOpen(false);
            return true;
        }

        public void ShowStatus(string message)
        {
            if (statusText != null)
                statusText.text = message ?? string.Empty;
        }

        public Image CreateDragVisual(Sprite sprite, string label)
        {
            if (canvas == null) return null;
            GameObject visual = new GameObject("Dragged Item", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            visual.transform.SetParent(canvas.transform, false);
            visual.transform.SetAsLastSibling();
            RectTransform rect = visual.GetComponent<RectTransform>();
            rect.sizeDelta = new Vector2(60f, 60f);
            Image image = visual.GetComponent<Image>();
            image.sprite = sprite;
            image.color = sprite != null ? Color.white : new Color(0.88f, 0.72f, 0.42f, 0.75f);
            image.raycastTarget = false;
            return image;
        }

        public void RefreshAll()
        {
            if (model == null) return;
            if (inventoryViews != null)
                foreach (InventorySlotView view in inventoryViews) view?.Refresh();
            foreach (InventorySlotView view in equipmentViews) view?.Refresh();

            RefreshSelectionHighlight();
            RefreshOpenTooltip();
        }

        public InventorySlotView GetInventorySlotView(int index)
        {
            return inventoryViews != null && index >= 0 && index < inventoryViews.Length ? inventoryViews[index] : null;
        }

        public InventorySlotView GetEquipmentSlotView(EquipmentSlotId slot)
        {
            int index = (int)slot;
            return index >= 0 && index < equipmentViews.Length ? equipmentViews[index] : null;
        }

        private void RefreshSelectionHighlight()
        {
            if (selectedSlot == null || model == null)
                return;

            bool stillValid = selectedSlot.IsEquipment
                ? model.GetEquipmentSlot(selectedSlot.EquipmentSlot) != null
                : model.GetInventorySlot(selectedSlot.InventoryIndex) != null;
            if (!stillValid)
                SelectSlot(null);
        }

        private void RefreshOpenTooltip()
        {
            if (tooltip == null)
                return;

            if (tooltipSlot != null && tooltipSlot.IsHovered)
                RequestTooltip(tooltipSlot, tooltipSlot.LastPointerPosition);
            else if (tooltip.IsVisible)
                HideTooltip();
        }

        // ------------------------------------------------------------------ tooltip

        public void RequestTooltip(InventorySlotView slot, Vector2 screenPosition)
        {
            if (tooltip == null || slot == null || !isOpen || model == null)
                return;

            InventoryStack stack = slot.IsEquipment
                ? model.GetEquipmentSlot(slot.EquipmentSlot)
                : model.GetInventorySlot(slot.InventoryIndex);
            if (stack == null || stack.IsEmpty)
            {
                HideTooltip();
                return;
            }

            bool active = slot.IsEquipment && InventoryModel.IsWeaponSlot(slot.EquipmentSlot) &&
                          (int)slot.EquipmentSlot == model.ActiveWeaponIndex;
            EquipmentSlotId? hoveredSlot = slot.IsEquipment ? slot.EquipmentSlot : (EquipmentSlotId?)null;

            tooltipSlot = slot;
            tooltip.Show(stack.item, stack.count, active, hoveredSlot, screenPosition);
        }

        /// <summary>Hides the tooltip. An exit from a different slot than the one being shown is ignored.</summary>
        public void HideTooltip(InventorySlotView requester = null)
        {
            if (requester != null && tooltipSlot != null && requester != tooltipSlot)
                return;

            tooltipSlot = null;
            tooltip?.Hide();
        }

        // ------------------------------------------------------------------ selection

        public void SelectSlot(InventorySlotView slot)
        {
            // Interacting with the grid means the player is no longer typing, so the text focus has to go.
            ReleaseFieldFocus();

            if (selectedSlot == slot)
                return;

            if (selectedSlot != null)
                selectedSlot.SetSelected(false);

            selectedSlot = slot;

            if (selectedSlot != null)
                selectedSlot.SetSelected(true);
        }

        // ------------------------------------------------------------------ quick move

        /// <summary>
        /// Equips a bag item into the first empty compatible slot, or moves a worn item back into the bag. Reports
        /// the model's real outcome: a refusal never changes the data, so it only changes the status line.
        /// </summary>
        public bool QuickMove(InventorySlotView slot)
        {
            if (slot == null || model == null)
                return false;

            CloseContextMenu();
            HideTooltip(slot);

            if (!slot.IsEquipment)
            {
                InventoryStack stack = model.GetInventorySlot(slot.InventoryIndex);
                if (stack == null || stack.IsEmpty)
                    return false;

                string itemName = stack.item.displayName;
                if (!stack.item.IsEquipment)
                {
                    ShowStatus($"'{itemName}' không phải vật phẩm trang bị nên không thể trang bị.");
                    return false;
                }

                if (!model.TryQuickEquip(slot.InventoryIndex, out EquipmentSlotId usedSlot, out bool replaced))
                {
                    ShowStatus($"Không thể trang bị '{itemName}': không còn ô phù hợp và túi đã đầy để nhận lại vật phẩm.");
                    return false;
                }

                ShowStatus(replaced
                    ? $"Đã trang bị '{itemName}' vào {InventoryModel.GetSlotLabel(usedSlot)}; vật phẩm cũ đã được trả về túi."
                    : $"Đã trang bị '{itemName}' vào {InventoryModel.GetSlotLabel(usedSlot)}.");
                return true;
            }

            InventoryStack equipped = model.GetEquipmentSlot(slot.EquipmentSlot);
            if (equipped == null || equipped.IsEmpty)
                return false;

            string equippedName = equipped.item.displayName;
            if (!model.TryQuickUnequip(slot.EquipmentSlot, out int destination))
            {
                ShowStatus($"Không thể tháo '{equippedName}': túi đã đầy.");
                return false;
            }

            ShowStatus($"Đã tháo '{equippedName}' vào ô {destination + 1}.");
            return true;
        }

        // ------------------------------------------------------------------ context menu

        public void OpenContextMenu(InventorySlotView slot, Vector2 screenPosition)
        {
            if (slot == null || model == null || !isOpen || contextMenu == null)
                return;

            contextSlot = slot;
            HideTooltip(slot);

            InventoryStack stack = slot.IsEquipment
                ? model.GetEquipmentSlot(slot.EquipmentSlot)
                : model.GetInventorySlot(slot.InventoryIndex);
            bool hasItem = stack != null && !stack.IsEmpty;

            bool showEquip = hasItem && (slot.IsEquipment || stack.item.IsEquipment);
            contextEquipButton.gameObject.SetActive(showEquip);
            contextEquipButton.GetComponentInChildren<Text>().text = slot.IsEquipment ? "Tháo vào túi" : "Trang bị nhanh";

            bool showSplit = !slot.IsEquipment && hasItem && model.MaxSplitAmount(slot.InventoryIndex) >= 1;
            contextSplitButton.gameObject.SetActive(showSplit);

            bool showWeapon = slot.IsEquipment && InventoryModel.IsWeaponSlot(slot.EquipmentSlot) &&
                              (int)slot.EquipmentSlot != model.ActiveWeaponIndex;
            contextWeaponButton.gameObject.SetActive(showWeapon);

            bool anyAction = showEquip || showSplit || showWeapon;
            contextHint.gameObject.SetActive(!anyAction);
            if (!anyAction)
                contextHint.text = hasItem
                    ? "Không có thao tác nào cho vật phẩm này."
                    : "Ô trống: không có thao tác.";

            // Only the rows that exist are shown, and the card is sized to the visible rows, so there is never a
            // button that does nothing.
            int visibleRows = 0;
            if (showEquip) visibleRows++;
            if (showSplit) visibleRows++;
            if (showWeapon) visibleRows++;
            if (visibleRows == 0) visibleRows = 1;

            var card = contextMenu.transform.Find("Menu Card").GetComponent<RectTransform>();
            float cardHeight = 150f + visibleRows * 54f;
            card.sizeDelta = new Vector2(340f, cardHeight);

            float y = cardHeight * 0.5f - 58f;
            foreach (string name in new[] { "Menu Equip", "Menu Split", "Menu Weapon", "Menu Hint" })
            {
                Transform row = card.Find(name);
                if (row == null || !row.gameObject.activeSelf)
                    continue;

                row.GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, y);
                y -= 54f;
            }

            Transform closeRow = card.Find("Menu Close");
            if (closeRow != null)
                closeRow.GetComponent<RectTransform>().anchoredPosition = new Vector2(0f, y - 6f);

            contextMenu.SetActive(true);
            contextMenu.transform.SetAsLastSibling();
            PositionFloating(card, screenPosition);
        }

        public void CloseContextMenu()
        {
            contextSlot = null;
            if (contextMenu != null && contextMenu.activeSelf)
                contextMenu.SetActive(false);
        }

        // ------------------------------------------------------------------ split prompt

        public void OpenSplitPrompt(InventorySlotView slot)
        {
            if (slot == null || slot.IsEquipment || model == null)
                return;

            OpenSplitPrompt(slot.InventoryIndex);
        }

        public void OpenSplitPrompt(int inventoryIndex)
        {
            if (model == null || splitPrompt == null)
                return;

            InventoryStack stack = model.GetInventorySlot(inventoryIndex);
            int max = model.MaxSplitAmount(inventoryIndex);
            if (stack == null || stack.IsEmpty || max < 1)
            {
                ShowStatus(stack == null || stack.IsEmpty
                    ? "Không còn vật phẩm để tách."
                    : "Không thể tách: cần ít nhất một ô trống trong túi và stack phải có từ 2 vật phẩm.");
                return;
            }

            splitIndex = inventoryIndex;
            splitSummary.text = $"{stack.item.displayName} × {stack.count}  →  tách ra (1 – {max})";
            splitField.text = Mathf.Max(1, stack.count / 2).ToString();
            splitPrompt.SetActive(true);
            splitPrompt.transform.SetAsLastSibling();
            splitField.Select();
            splitField.ActivateInputField();
        }

        /// <summary>
        /// Applies the split. The source stack is re-read here, so a stack that shrank or vanished between opening
        /// the prompt and confirming cannot make the model accept a stale amount.
        /// </summary>
        public bool ConfirmSplit()
        {
            if (splitIndex < 0 || model == null)
            {
                CloseSplitPrompt();
                return false;
            }

            InventoryStack source = model.GetInventorySlot(splitIndex);
            if (source == null || source.IsEmpty)
            {
                ShowStatus("Vật phẩm nguồn không còn trong túi — đã huỷ tách stack.");
                CloseSplitPrompt();
                return false;
            }

            int max = model.MaxSplitAmount(splitIndex);
            if (max < 1)
            {
                ShowStatus("Không thể tách stack này: cần ô trống trong túi và stack phải có từ 2 vật phẩm.");
                CloseSplitPrompt();
                return false;
            }

            if (!int.TryParse(splitField.text, out int amount))
            {
                ShowStatus($"Số lượng không hợp lệ: hãy nhập số nguyên từ 1 đến {max}.");
                return false;
            }

            if (amount < 1 || amount > max)
            {
                ShowStatus($"Số lượng phải từ 1 đến {max} (không thể tách hết cả stack).");
                return false;
            }

            string itemName = source.item.displayName;
            bool ok = model.TrySplitStack(splitIndex, amount, out int newIndex);
            ShowStatus(ok
                ? $"Đã tách {amount} × {itemName} sang ô {newIndex + 1}."
                : "Tách stack thất bại: túi không còn ô trống.");

            if (ok)
                CloseSplitPrompt();

            return ok;
        }

        public void CancelSplit() => CloseSplitPrompt();

        private void CloseSplitPrompt()
        {
            splitIndex = -1;
            if (splitPrompt != null && splitPrompt.activeSelf)
                splitPrompt.SetActive(false);

            ReleaseFieldFocus();
        }

        // ------------------------------------------------------------------ bag tools

        public int AutoStack()
        {
            if (model == null)
                return 0;

            InventorySlotView.CancelActiveDrag();
            HideTooltip();
            CloseContextMenu();

            int merges = model.AutoStackInventory();
            ShowStatus(merges > 0
                ? $"Đã gộp {merges} stack cùng loại."
                : "Không có stack nào cần gộp.");
            return merges;
        }

        public bool SortBag()
        {
            if (model == null)
                return false;

            InventorySlotView.CancelActiveDrag();
            HideTooltip();
            CloseContextMenu();
            SelectSlot(null);

            bool changed = model.SortInventory();
            ShowStatus(changed
                ? "Đã sắp xếp túi theo loại, tên rồi ID."
                : "Túi đã đúng thứ tự, không cần sắp xếp.");
            return changed;
        }

        public void SetSearch(string query)
        {
            searchQuery = query ?? string.Empty;
            ApplyFilter();
        }

        public void CycleFilter()
        {
            filterIndex = (filterIndex + 1) % filterCycle.Count;
            ApplyFilter();
        }

        public void SetFilter(InventoryItemCategory? category)
        {
            int index = filterCycle.IndexOf(category);
            filterIndex = index >= 0 ? index : 0;
            ApplyFilter();
        }

        /// <summary>
        /// Applies the search box and category filter to the bag view only. Slots keep their index and the model is
        /// never touched, so filtering cannot move or hide an item anywhere but on screen.
        /// </summary>
        public void ApplyFilter()
        {
            if (filterLabel != null)
                filterLabel.text = DescribeFilter();

            if (model == null || inventoryViews == null)
                return;

            for (int i = 0; i < inventoryViews.Length; i++)
            {
                InventoryStack stack = model.GetInventorySlot(i);
                inventoryViews[i]?.SetFiltered(!MatchesFilter(stack));
            }
        }

        private bool MatchesFilter(InventoryStack stack)
        {
            if (stack == null || stack.IsEmpty)
                return true;

            if (ActiveCategoryFilter.HasValue && stack.item.category != ActiveCategoryFilter.Value)
                return false;

            if (string.IsNullOrWhiteSpace(searchQuery))
                return true;

            string name = stack.item.displayName ?? string.Empty;
            string id = stack.item.itemId ?? string.Empty;
            return name.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0
                || id.IndexOf(searchQuery, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string DescribeFilter()
        {
            InventoryItemCategory? category = ActiveCategoryFilter;
            return category.HasValue ? "Lọc: " + DescribeCategory(category.Value) : "Lọc: Tất cả";
        }

        private static string DescribeCategory(InventoryItemCategory category)
        {
            switch (category)
            {
                case InventoryItemCategory.Weapon: return "Vũ khí";
                case InventoryItemCategory.Helmet: return "Mũ";
                case InventoryItemCategory.Chestplate: return "Giáp thân";
                case InventoryItemCategory.Legging: return "Giáp chân";
                case InventoryItemCategory.Boots: return "Giày";
                case InventoryItemCategory.Earring: return "Bông tai";
                case InventoryItemCategory.Necklace: return "Dây chuyền";
                case InventoryItemCategory.Ring: return "Nhẫn";
                case InventoryItemCategory.Bracelet: return "Vòng tay";
                case InventoryItemCategory.Amulet: return "Bùa";
                case InventoryItemCategory.Accessory: return "Phụ kiện";
                case InventoryItemCategory.Consumable: return "Tiêu hao";
                case InventoryItemCategory.Material: return "Nguyên liệu";
                case InventoryItemCategory.Quest: return "Nhiệm vụ";
                default: return category.ToString();
            }
        }

        private static List<InventoryItemCategory?> BuildFilterCycle()
        {
            var cycle = new List<InventoryItemCategory?>();
            cycle.Add(null);
            foreach (InventoryItemCategory category in Enum.GetValues(typeof(InventoryItemCategory)))
                cycle.Add(category);
            return cycle;
        }

        /// <summary>Human-readable result of a successful drop, for the status line.</summary>
        public string DescribeTransfer(InventorySlotView from, InventorySlotView to)
        {
            if (from == null || to == null || model == null)
                return string.Empty;

            string itemName = from.Item != null ? from.Item.displayName : "Vật phẩm";

            if (!from.IsEquipment && !to.IsEquipment)
                return $"Đã chuyển '{itemName}' sang ô {to.InventoryIndex + 1}.";
            if (!from.IsEquipment && to.IsEquipment)
                return $"Đã trang bị '{itemName}' vào {InventoryModel.GetSlotLabel(to.EquipmentSlot)}.";
            if (from.IsEquipment && !to.IsEquipment)
                return $"Đã tháo '{itemName}' vào ô {to.InventoryIndex + 1}.";
            return $"Đã chuyển '{itemName}' sang {InventoryModel.GetSlotLabel(to.EquipmentSlot)}.";
        }

        private void HandleActiveWeaponChanged(int index, InventoryItemDefinition item)
        {
            RefreshAll();
            onActiveWeaponChanged?.Invoke(item);
        }

        private void PositionFloating(RectTransform target, Vector2 screenPosition)
        {
            if (target == null || canvas == null)
                return;

            RectTransform canvasRect = canvas.transform as RectTransform;
            if (canvasRect == null)
                return;

            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screenPosition, null, out Vector2 local))
                return;

            Rect bounds = canvasRect.rect;
            float halfWidth = target.rect.width * 0.5f;
            float halfHeight = target.rect.height * 0.5f;

            float x = Mathf.Clamp(local.x + halfWidth, bounds.xMin + halfWidth, Mathf.Max(bounds.xMin + halfWidth, bounds.xMax - halfWidth));
            float y = Mathf.Clamp(local.y - halfHeight, bounds.yMin + halfHeight, Mathf.Max(bounds.yMin + halfHeight, bounds.yMax - halfHeight));
            target.anchoredPosition = new Vector2(x, y);
        }

        // ------------------------------------------------------------------ primitives

        private static GameObject MakePanel(string name, Transform parent, Vector2 size, Vector2 position, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
            go.GetComponent<Image>().color = color;
            return go;
        }

        private static Image MakeImage(string name, Transform parent, Sprite sprite, Vector2 position, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            Image image = go.GetComponent<Image>();
            image.sprite = sprite;
            image.raycastTarget = false;
            return image;
        }

        private static Text MakeText(string name, Transform parent, string value, int fontSize, TextAnchor alignment,
            Vector2 position, Vector2 size, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            Text text = go.GetComponent<Text>();
            text.text = value;
            text.font = ResolveBuiltinFont();
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Truncate;
            text.raycastTarget = false;
            return text;
        }

        private static Font builtinFont;

        /// <summary>
        /// Unity 6 removed the "Arial.ttf" built-in font: asking for it now THROWS instead of returning null, which
        /// used to abort the whole interface build after the first text element. The current name is preferred and
        /// the older name is only used as a fallback, both resolved once and cached.
        /// </summary>
        private static Font ResolveBuiltinFont()
        {
            if (builtinFont != null)
                return builtinFont;

            try { builtinFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
            catch { builtinFont = null; }

            if (builtinFont == null)
            {
                try { builtinFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
                catch { builtinFont = null; }
            }

            return builtinFont;
        }

        private static Button MakeButton(string name, Transform parent, string label, int fontSize, Vector2 position, Vector2 size)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition = position;
            rect.sizeDelta = size;
            go.GetComponent<Image>().color = new Color(0.24f, 0.15f, 0.13f, 1f);
            Button button = go.GetComponent<Button>();
            button.targetGraphic = go.GetComponent<Image>();
            button.transition = Selectable.Transition.ColorTint;
            Text text = MakeText("Label", go.transform, label, fontSize, TextAnchor.MiddleCenter, Vector2.zero, size, Color.white);
            text.raycastTarget = false;
            return button;
        }
    }
}

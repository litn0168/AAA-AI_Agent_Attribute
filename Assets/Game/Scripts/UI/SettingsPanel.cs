using System;
using DarkRealms.Core;
using UnityEngine;
using UnityEngine.UI;

namespace DarkRealms.UI
{
    /// <summary>
    /// The Settings page, reached from the pause menu. It is a sub-page of the pause menu and not a second pause
    /// system: it never touches Time.timeScale and never enables gameplay input - it only shows itself, writes
    /// through <see cref="AppSettings"/>, and reports being closed so the pause menu can bring its own panel back.
    /// The page is built at runtime, like the inventory window, so no scene layout has to be kept in sync.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class SettingsPanel : MonoBehaviour
    {
        [Tooltip("Canvas the page is built into. Left empty, the parent canvas is used.")]
        [SerializeField] private Canvas hostCanvas;

        /// <summary>Raised when the page closes, whatever closed it (Back button or Escape).</summary>
        public event Action Closed;

        private GameObject page;
        private Toggle escapeToggle;
        private Text escapeStateLabel;
        private Text escapeNoteLabel;

        public bool IsOpen => page != null && page.activeSelf;

        private void Awake()
        {
            Build();
        }

        private void OnEnable()
        {
            AppSettings.Changed += HandleSettingsChanged;
        }

        private void OnDisable()
        {
            AppSettings.Changed -= HandleSettingsChanged;
        }

        private void OnDestroy()
        {
            if (page != null)
                Destroy(page);
        }

        public void Open()
        {
            if (page == null)
                Build();

            RefreshLabels();

            if (page != null)
            {
                page.SetActive(true);
                page.transform.SetAsLastSibling();
            }

            Cursor.visible = true;
        }

        /// <summary>Hides the page. Does nothing when it is already closed, so a repeated Escape cannot fire twice.</summary>
        public void Close()
        {
            if (!IsOpen)
                return;

            page.SetActive(false);
            Closed?.Invoke();
        }

        public void TogglePage()
        {
            if (IsOpen) Close();
            else Open();
        }

        private void Build()
        {
            if (page != null)
                return;

            Canvas canvas = ResolveCanvas();
            if (canvas == null)
            {
                Debug.LogError($"[{nameof(SettingsPanel)}] No canvas to build into: assign one or parent this under a canvas.", this);
                return;
            }

            GameObject root = RuntimeUiFactory.Panel("Settings Page", canvas.transform,
                new Vector2(1920f, 1080f), Vector2.zero, new Color(0.02f, 0.02f, 0.04f, 0.88f));
            RuntimeUiFactory.StretchFull(root.GetComponent<RectTransform>());
            root.GetComponent<Image>().raycastTarget = true;
            page = root;

            GameObject card = RuntimeUiFactory.Panel("Settings Card", root.transform,
                new Vector2(720f, 460f), Vector2.zero, new Color(0.055f, 0.062f, 0.075f, 0.98f));

            RuntimeUiFactory.Text("Title", card.transform, "SETTINGS", 34, TextAnchor.MiddleCenter,
                new Vector2(660f, 46f), new Vector2(0f, 180f), new Color(0.91f, 0.82f, 0.61f, 0f + 1f));

            RuntimeUiFactory.Text("Subtitle", card.transform, "CÀI ĐẶT", 20, TextAnchor.MiddleCenter,
                new Vector2(660f, 30f), new Vector2(0f, 145f), new Color(0.70f, 0.73f, 0.78f, 1f));

            Text toggleLabel;
            escapeToggle = RuntimeUiFactory.Toggle("UseEscapeToPause Toggle", card.transform,
                "Dùng ESC để tạm dừng game   (Use ESC to Pause)", 22,
                new Vector2(620f, 46f), new Vector2(0f, 86f), out toggleLabel);
            escapeToggle.isOn = AppSettings.UseEscapeToPause;
            escapeToggle.onValueChanged.AddListener(HandleToggleChanged);

            escapeStateLabel = RuntimeUiFactory.Text("State", card.transform, string.Empty, 20, TextAnchor.MiddleCenter,
                new Vector2(660f, 28f), new Vector2(0f, 40f), new Color(0.88f, 0.69f, 0.42f, 1f));

            escapeNoteLabel = RuntimeUiFactory.Text("Note", card.transform, string.Empty, 17, TextAnchor.UpperCenter,
                new Vector2(640f, 92f), new Vector2(0f, -30f), new Color(0.66f, 0.69f, 0.74f, 1f));

            Button back = RuntimeUiFactory.Button("Back", card.transform, "Quay lại   (Esc)", 22,
                new Vector2(280f, 54f), new Vector2(0f, -170f), new Color(0.24f, 0.15f, 0.13f, 1f), Color.white);
            back.onClick.AddListener(() => Close());

            RefreshLabels();
            page.SetActive(false);
        }

        /// <summary>
        /// The canvas the page is drawn into: the assigned one, otherwise the canvas this component lives under,
        /// otherwise any canvas in the scene. The last fallback matters because the page can be created at runtime
        /// by <see cref="PauseMenu"/>, which must not depend on the exact scene hierarchy.
        /// </summary>
        private Canvas ResolveCanvas()
        {
            if (hostCanvas != null)
                return hostCanvas;

            Canvas parent = GetComponentInParent<Canvas>();
            if (parent != null)
                return parent;

            return UnityEngine.Object.FindAnyObjectByType<Canvas>();
        }

        private void HandleToggleChanged(bool value)
        {
            // The setting is written the moment it is flipped, so Escape changes behaviour immediately and the
            // choice is already on disk if the player quits from here.
            AppSettings.UseEscapeToPause = value;
            RefreshLabels();
        }

        private void HandleSettingsChanged()
        {
            if (escapeToggle != null)
                escapeToggle.SetIsOnWithoutNotify(AppSettings.UseEscapeToPause);
            RefreshLabels();
        }

        private void RefreshLabels()
        {
            if (escapeStateLabel != null)
            {
                escapeStateLabel.text = AppSettings.UseEscapeToPause
                    ? "Trạng thái: BẬT — ESC mở Pause Menu"
                    : "Trạng thái: TẮT — ESC không mở Pause Menu";
            }

            if (escapeNoteLabel != null)
            {
                escapeNoteLabel.text = AppSettings.UseEscapeToPause
                    ? "Đang bật: khi đang chơi, ESC mở Pause Menu.\nTúi đồ luôn được ưu tiên: nếu túi đang mở, ESC chỉ đóng túi.\nESC luôn thoát được màn hình pause."
                    : "Đang tắt: ESC không mở Pause Menu khi đang chơi.\nESC vẫn đóng túi và vẫn thoát được màn hình pause.\nTuỳ chọn được lưu lại cho lần chơi sau.";
            }
        }
    }
}

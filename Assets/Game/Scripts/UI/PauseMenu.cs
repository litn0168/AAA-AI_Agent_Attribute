using DarkRealms.Core;
using DarkRealms.Inventory;
using DarkRealms.Player;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace DarkRealms.UI
{
    /// <summary>
    /// Escape/Start handled through the Input System (the project is Input System only). Freezes time via
    /// GameManager and shows a simple pause overlay with a Settings sub-page.
    ///
    /// This component is the single owner of the Escape key: the bag and the Settings page never pause anything
    /// themselves, they only report whether they consumed the press. GameManager stays the only owner of the
    /// paused state and of Time.timeScale.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class PauseMenu : MonoBehaviour
    {
        [SerializeField] private GameObject pausePanel;
        [SerializeField] private PlayerInputReader inputToDisable;

        [Tooltip("Optional. When the bag is open, Escape closes it instead of toggling pause, so a single press " +
                 "never does both.")]
        [SerializeField] private InventoryUIController inventory;

        [Tooltip("Optional Settings sub-page, opened from the pause panel. Escape closes it before anything else. " +
                 "Left empty, one is created on this object, which keeps the scene free of an extra component to " +
                 "keep in sync while still being the single Settings page for the game.")]
        [SerializeField] private SettingsPanel settingsPanel;

        private bool buttonsBuilt;

        private void Start()
        {
            if (pausePanel != null)
                pausePanel.SetActive(false);

            settingsPanel = ResolveSettingsPanel();
            if (settingsPanel != null)
                settingsPanel.Closed += HandleSettingsClosed;

            BuildPausePanelButtons();
        }

        /// <summary>
        /// Uses the authored reference when there is one, otherwise adds the page to this object. Reaching the page
        /// only through the pause menu is the intended design, so the fallback never creates a second page: it
        /// reuses this game object's component if one is already attached.
        /// </summary>
        private SettingsPanel ResolveSettingsPanel()
        {
            if (settingsPanel != null)
                return settingsPanel;

            SettingsPanel existing = GetComponent<SettingsPanel>();
            return existing != null ? existing : gameObject.AddComponent<SettingsPanel>();
        }

        private void OnDestroy()
        {
            if (settingsPanel != null)
                settingsPanel.Closed -= HandleSettingsClosed;
        }

        private void Update()
        {
            if (GameManager.Instance == null)
                return;

            HandlePausePressed(PausePressed());
        }

        /// <summary>
        /// Routes one pause press, in the exact order the brief specifies: a sub-page on top of the pause menu
        /// first, then the bag (which outranks pause), then pause itself, then gameplay.
        ///
        /// Kept separate from Update so the routing rule is one obvious, directly exercisable decision instead of
        /// being buried in a frame callback.
        /// </summary>
        public void HandlePausePressed(bool pressed)
        {
            if (GameManager.Instance == null || !pressed)
                return;

            // 1. A sub-page of the pause menu owns Escape while it is open, and closing it never unpauses.
            if (settingsPanel != null && settingsPanel.IsOpen)
            {
                settingsPanel.Close();
                return;
            }

            // 2. The bag consumes the press when it acts on it: closing a sub-window inside it, closing itself,
            //    or having already done so earlier in this same frame. Either way exactly one action runs.
            if (inventory != null && inventory.HandleEscapePress())
                return;

            // 3. While the game is paused, Escape always resumes, whatever the setting says. The player must
            //    never be stuck behind a pause they cannot leave.
            if (GameManager.Instance.IsPaused)
            {
                SetPaused(false);
                return;
            }

            // 4. In gameplay, the setting decides whether Escape pauses at all.
            if (AppSettings.UseEscapeToPause)
                SetPaused(true);
        }

        private static bool PausePressed()
        {
            bool keyboard = Keyboard.current != null && Keyboard.current.escapeKey.wasPressedThisFrame;
            bool gamepad = Gamepad.current != null && Gamepad.current.startButton.wasPressedThisFrame;
            return keyboard || gamepad;
        }

        /// <summary>Applies a paused state. The only place that shows the pause overlay and gates gameplay input.</summary>
        public void SetPaused(bool paused)
        {
            GameManager.Instance.SetPaused(paused);

            // Leaving the pause menu also leaves its sub-page, so the next pause starts at the top level.
            if (!paused && settingsPanel != null && settingsPanel.IsOpen)
                settingsPanel.Close();

            if (pausePanel != null)
                pausePanel.SetActive(paused);

            if (inputToDisable != null)
            {
                // One owner for the gameplay input gate: paused OR bag open both mean "no gameplay input". This
                // also covers resuming from the pause menu while the bag happens to be open.
                bool bagOpen = inventory != null && inventory.IsOpen;
                inputToDisable.Enabled = !paused && !bagOpen;
            }

            Cursor.visible = true;
        }

        public void Toggle()
        {
            SetPaused(!GameManager.Instance.IsPaused);
        }

        public void Resume() => SetPaused(false);

        /// <summary>Opens the Settings sub-page, hiding the pause panel behind it.</summary>
        public void OpenSettings()
        {
            if (settingsPanel == null)
                return;

            if (pausePanel != null)
                pausePanel.SetActive(false);

            settingsPanel.Open();
        }

        private void HandleSettingsClosed()
        {
            // The page was closed while the game is still paused: bring the pause panel back so the player is not
            // left staring at an empty screen.
            if (GameManager.Instance != null && GameManager.Instance.IsPaused && pausePanel != null)
                pausePanel.SetActive(true);
        }

        private void BuildPausePanelButtons()
        {
            if (buttonsBuilt || pausePanel == null)
                return;

            buttonsBuilt = true;

            Text pauseText = FindText(pausePanel.transform, "PauseText");
            if (pauseText != null)
                pauseText.text = "PAUSED\n\nEsc to resume";

            Button resume = RuntimeUiFactory.Button("Resume Button", pausePanel.transform, "TIẾP TỤC   (Resume)", 22,
                new Vector2(340f, 54f), new Vector2(0f, -140f), new Color(0.24f, 0.15f, 0.13f, 1f), Color.white);
            resume.onClick.AddListener(Resume);

            Button settings = RuntimeUiFactory.Button("Settings Button", pausePanel.transform, "CÀI ĐẶT   (Settings)", 22,
                new Vector2(340f, 54f), new Vector2(0f, -206f), new Color(0.16f, 0.18f, 0.22f, 1f), Color.white);
            settings.onClick.AddListener(OpenSettings);
        }

        private static Text FindText(Transform root, string name)
        {
            foreach (Text candidate in root.GetComponentsInChildren<Text>(true))
                if (candidate.name == name)
                    return candidate;
            return null;
        }
    }
}

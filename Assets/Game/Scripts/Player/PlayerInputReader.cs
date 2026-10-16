using UnityEngine;
using UnityEngine.InputSystem;

namespace DarkRealms.Player
{
    /// <summary>
    /// Thin adapter over the project's Input System actions asset. Keeps every other
    /// gameplay script free of direct Input System dependencies so input can be
    /// swapped (AI, replay, rebinding) without touching movement or combat code.
    ///
    /// Execution order is pinned to the front of the frame so that every consumer reading
    /// these properties from Update (PlayerController, PlayerCombat) always sees input that
    /// was sampled earlier in the same frame. Without this, the reads depend on unspecified
    /// script ordering and can be one frame stale - which is felt directly as input latency.
    /// </summary>
    [DefaultExecutionOrder(-200)]
    [DisallowMultipleComponent]
    public class PlayerInputReader : MonoBehaviour
    {
        [SerializeField] private InputActionAsset actions;
        [SerializeField] private string actionMapName = "Player";

        private InputActionMap actionMap;
        private InputAction moveAction;
        private InputAction jumpAction;
        private InputAction attackAction;
        private InputAction sprintAction;

        /// <summary>When false all reads return neutral values (used for pause/menus).</summary>
        public bool Enabled { get; set; } = true;

        public Vector2 Move { get; private set; }
        public float MoveX => Move.x;
        public bool JumpPressed { get; private set; }
        public bool JumpHeld { get; private set; }
        public bool AttackPressed { get; private set; }
        public bool SprintHeld { get; private set; }

        /// <summary>
        /// Number of Jump press EDGES observed since this component was enabled. Diagnostics only, no
        /// gameplay behaviour depends on it. It lets a verifier prove whether one synthetic press
        /// produced one edge (so a duplicated jump launch is a gameplay defect) or several (so the
        /// fault is in event delivery), instead of guessing which side duplicated the input.
        /// </summary>
        public int JumpPressCount { get; private set; }

        /// <summary>Press edges observed for Attack since enable. Diagnostics only; see <see cref="JumpPressCount"/>.</summary>
        public int AttackPressCount { get; private set; }

        private void Awake()
        {
            if (actions == null)
            {
                Debug.LogError($"[{nameof(PlayerInputReader)}] No InputActionAsset assigned.", this);
                return;
            }

            actionMap = actions.FindActionMap(actionMapName, false);
            if (actionMap == null)
            {
                Debug.LogError($"[{nameof(PlayerInputReader)}] Action map '{actionMapName}' not found in '{actions.name}'.", this);
                return;
            }

            moveAction = actionMap.FindAction("Move", false);
            jumpAction = actionMap.FindAction("Jump", false);
            attackAction = actionMap.FindAction("Attack", false);
            sprintAction = actionMap.FindAction("Sprint", false);
        }

        private void OnEnable() => actionMap?.Enable();

        private void OnDisable() => actionMap?.Disable();

        private void Update()
        {
            if (actionMap == null)
                return;

            if (!Enabled)
            {
                Move = Vector2.zero;
                JumpPressed = JumpHeld = AttackPressed = SprintHeld = false;
                return;
            }

            if (moveAction != null)
                Move = moveAction.ReadValue<Vector2>();

            // Each action is polled exactly once per frame: WasPressedThisFrame() advances edge state
            // for the frame, so querying it twice could consume the edge and lose a press.
            bool jumpPressed = jumpAction != null && jumpAction.WasPressedThisFrame();
            bool attackPressed = attackAction != null && attackAction.WasPressedThisFrame();

            JumpPressed = jumpPressed;
            JumpHeld = jumpAction != null && jumpAction.IsPressed();
            AttackPressed = attackPressed;
            SprintHeld = sprintAction != null && sprintAction.IsPressed();

            if (jumpPressed)
                JumpPressCount++;

            if (attackPressed)
                AttackPressCount++;
        }
    }
}

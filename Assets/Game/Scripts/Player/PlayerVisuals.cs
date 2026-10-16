using UnityEngine;
using DarkRealms.Core;

namespace DarkRealms.Player
{
    /// <summary>
    /// Reads PlayerController / PlayerCombat state and selects the matching animation clip.
    /// Kept separate from the controller so movement logic stays free of presentation concerns.
    /// </summary>
    [RequireComponent(typeof(FrameAnimator))]
    [DisallowMultipleComponent]
    public class PlayerVisuals : MonoBehaviour
    {
        [SerializeField] private PlayerController controller;
        [SerializeField] private PlayerCombat combat;
        [SerializeField] private SpriteRenderer spriteRenderer;

        private FrameAnimator animator;

        private void Awake()
        {
            animator = GetComponent<FrameAnimator>();

            if (spriteRenderer == null)
                spriteRenderer = GetComponent<SpriteRenderer>();

            if (controller == null)
                controller = GetComponentInParent<PlayerController>();

            if (combat == null)
                combat = GetComponentInParent<PlayerCombat>();
        }

        private void LateUpdate()
        {
            if (controller == null)
                return;

            if (spriteRenderer != null)
                spriteRenderer.flipX = controller.Facing < 0;

            if (animator == null)
                return;

            if (controller.State == PlayerState.Dead)
            {
                animator.PlayIfNot("Death");
                return;
            }

            if (combat != null && combat.IsAttacking)
            {
                animator.PlayIfNot("Attack");
                return;
            }

            switch (controller.State)
            {
                case PlayerState.Jump:
                    animator.PlayIfNot("Jump");
                    break;
                case PlayerState.Fall:
                    animator.PlayIfNot("Fall");
                    break;
                case PlayerState.Land:
                    animator.PlayIfNot("Land");
                    break;
                case PlayerState.Hurt:
                    animator.PlayIfNot("Hurt");
                    break;
                case PlayerState.Run:
                    animator.PlayIfNot("Run", controller.IsRunning ? 1.3f : 0.85f);
                    break;
                case PlayerState.Walk:
                    animator.PlayIfNot("Walk");
                    break;
                default:
                    animator.PlayIfNot("Idle");
                    break;
            }
        }
    }
}

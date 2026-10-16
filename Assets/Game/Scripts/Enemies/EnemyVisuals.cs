using UnityEngine;
using DarkRealms.Core;

namespace DarkRealms.Enemies
{
    /// <summary>Selects the enemy animation clip from EnemyController state.</summary>
    [RequireComponent(typeof(FrameAnimator))]
    [DisallowMultipleComponent]
    public class EnemyVisuals : MonoBehaviour
    {
        [SerializeField] private EnemyController controller;

        private FrameAnimator animator;

        private void Awake()
        {
            animator = GetComponent<FrameAnimator>();

            if (controller == null)
                controller = GetComponentInParent<EnemyController>();
        }

        private void LateUpdate()
        {
            if (animator == null || controller == null)
                return;

            switch (controller.State)
            {
                case EnemyState.Dead:
                    animator.PlayIfNot("Death");
                    break;
                case EnemyState.Attack:
                    animator.PlayIfNot("Attack");
                    break;
                case EnemyState.Hurt:
                    animator.PlayIfNot("Hurt");
                    break;
                case EnemyState.Chase:
                    animator.PlayIfNot("Move", 1.25f);
                    break;
                default:
                    animator.PlayIfNot("Move");
                    break;
            }
        }
    }
}

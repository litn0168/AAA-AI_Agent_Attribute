using UnityEngine;
using DarkRealms.Core;

namespace DarkRealms.CameraSystem
{
    /// <summary>
    /// Smooth orthographic follow camera with optional world bounds so the view never
    /// shows past the level edges. Adds look-ahead in the direction of travel and a
    /// slight horizontal offset while facing.
    /// </summary>
    [RequireComponent(typeof(Camera))]
    [DisallowMultipleComponent]
    public class CameraFollow : MonoBehaviour
    {
        [SerializeField] private Transform target;
        [SerializeField] private Vector2 offset = new(0f, 1.2f);
        [SerializeField] private float smoothTime = 0.18f;
        [SerializeField] private float maxSpeed = 60f;

        [Header("Look Ahead")]
        [Tooltip("Any component implementing IFacingProvider (e.g. PlayerController).")]
        [SerializeField] private MonoBehaviour facingSource;
        [SerializeField] private float lookAheadDistance = 1.6f;
        [SerializeField] private float lookAheadSmoothing = 4f;

        [Header("Bounds")]
        [SerializeField] private bool useBounds = true;
        [SerializeField] private Vector2 boundsMin = new(-20f, -5f);
        [SerializeField] private Vector2 boundsMax = new(80f, 30f);

        [Header("Shake")]
        [SerializeField] private float shakeDamping = 8f;

        private Camera cam;
        private Vector3 velocity;
        private float currentLookAhead;
        private float shakeAmount;

        private void Awake()
        {
            cam = GetComponent<Camera>();
        }

        private void LateUpdate()
        {
            if (target == null)
                return;

            float targetLookAhead = 0f;
            if (facingSource is IFacingProvider facing)
                targetLookAhead = facing.Facing >= 0 ? lookAheadDistance : -lookAheadDistance;

            currentLookAhead = Mathf.Lerp(currentLookAhead, targetLookAhead, 1f - Mathf.Exp(-lookAheadSmoothing * Time.unscaledDeltaTime));

            Vector3 desired = (Vector2)target.position
                              + offset
                              + new Vector2(currentLookAhead, 0f);

            desired.z = transform.position.z;

            if (useBounds)
                desired = ApplyBounds(desired);

            Vector3 next = Vector3.SmoothDamp(
                transform.position,
                desired,
                ref velocity,
                smoothTime,
                maxSpeed,
                Time.unscaledDeltaTime);

            if (shakeAmount > 0.0001f)
            {
                next += (Vector3)(Random.insideUnitCircle * shakeAmount);
                shakeAmount = Mathf.Lerp(shakeAmount, 0f, 1f - Mathf.Exp(-shakeDamping * Time.unscaledDeltaTime));
            }

            transform.position = next;
        }

        private Vector3 ApplyBounds(Vector3 desired)
        {
            if (cam == null)
                cam = GetComponent<Camera>();

            float halfHeight = cam.orthographicSize;
            float halfWidth = halfHeight * cam.aspect;

            float minX = boundsMin.x + halfWidth;
            float maxX = boundsMax.x - halfWidth;
            float minY = boundsMin.y + halfHeight;
            float maxY = boundsMax.y - halfHeight;

            // When the level is narrower/shorter than the view, centre instead of clamping oddly.
            desired.x = minX > maxX ? (boundsMin.x + boundsMax.x) * 0.5f : Mathf.Clamp(desired.x, minX, maxX);
            desired.y = minY > maxY ? (boundsMin.y + boundsMax.y) * 0.5f : Mathf.Clamp(desired.y, minY, maxY);

            return desired;
        }

        /// <summary>Adds a one-off camera shake impulse (e.g. on landing or taking a hit).</summary>
        public void Shake(float amount) => shakeAmount = Mathf.Max(shakeAmount, amount);

        public void SetTarget(Transform newTarget) => target = newTarget;

        private void OnDrawGizmosSelected()
        {
            if (!useBounds)
                return;

            Gizmos.color = new Color(0.3f, 0.8f, 1f, 0.7f);
            Vector3 center = new((boundsMin.x + boundsMax.x) * 0.5f, (boundsMin.y + boundsMax.y) * 0.5f, 0f);
            Vector3 size = new(boundsMax.x - boundsMin.x, boundsMax.y - boundsMin.y, 0f);
            Gizmos.DrawWireCube(center, size);
        }
    }
}

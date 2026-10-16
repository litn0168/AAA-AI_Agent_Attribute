using UnityEngine;

namespace DarkRealms.Environment
{
    /// <summary>
    /// Moves a background layer relative to the camera to fake depth. Attach to any
    /// far/mid/near layer sprite; the layer itself stays static in the world.
    /// </summary>
    [DisallowMultipleComponent]
    public class ParallaxLayer : MonoBehaviour
    {
        [Tooltip("0 = pinned to the camera (infinitely far), 1 = fully in world space.")]
        [SerializeField, Range(0f, 1f)] private float parallaxFactor = 0.5f;

        [SerializeField] private bool followVertical = true;
        [SerializeField] private float verticalFactor = 0.5f;
        [SerializeField] private bool infiniteHorizontalScroll;

        private Transform cam;
        private Vector3 startPosition;
        private float startCameraX;
        private float startCameraY;
        private float spriteWidth;

        private void Start()
        {
            var mainCamera = Camera.main;
            if (mainCamera != null)
                cam = mainCamera.transform;

            startPosition = transform.position;

            if (cam != null)
            {
                startCameraX = cam.position.x;
                startCameraY = cam.position.y;
            }

            var renderer = GetComponent<SpriteRenderer>();
            if (renderer != null)
                spriteWidth = renderer.bounds.size.x;
        }

        private void LateUpdate()
        {
            if (cam == null)
                return;

            float deltaX = cam.position.x - startCameraX;
            float deltaY = cam.position.y - startCameraY;

            Vector3 position = startPosition;
            position.x += deltaX * parallaxFactor;

            if (followVertical)
                position.y += deltaY * verticalFactor;

            if (infiniteHorizontalScroll && spriteWidth > 0.01f)
            {
                float relative = cam.position.x * (1f - parallaxFactor);
                position.x = startPosition.x + relative;

                while (cam.position.x - position.x > spriteWidth * 0.5f)
                    position.x += spriteWidth;

                while (position.x - cam.position.x > spriteWidth * 0.5f)
                    position.x -= spriteWidth;
            }

            transform.position = position;
        }
    }
}

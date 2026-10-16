using UnityEngine;
using UnityEngine.EventSystems;

namespace DarkRealms.World
{
    /// <summary>
    /// Drag and scroll plumbing for the atlas viewport, kept as its own component so the screen's own logic stays
    /// about the atlas rather than about pointer events - and so a verification can drive a drag directly.
    ///
    /// A click on a map card does not pan: only a real drag does. That is what stops "select a map" from moving
    /// the map out from under the player.
    /// </summary>
    public sealed class MapViewportInput : MonoBehaviour, IDragHandler, IPointerDownHandler, IScrollHandler
    {
        public WorldMapScreen Owner;

        [Tooltip("True for the small local-map window, which is a read-only view and never pans the atlas.")]
        public bool LocalMap;

        public void OnPointerDown(PointerEventData eventData)
        {
            // Clear any focused text field, so a field cannot keep eating keyboard input after the player clicks
            // away from it.
            if (!LocalMap && Owner != null && EventSystem.current != null)
                EventSystem.current.SetSelectedGameObject(null);
        }

        public void OnDrag(PointerEventData eventData)
        {
            if (Owner == null || LocalMap)
                return;

            Owner.PanBy(eventData.delta);
        }

        public void OnScroll(PointerEventData eventData)
        {
            if (Owner == null || LocalMap)
                return;

            Vector2 local = Owner.ScreenToViewportLocal(eventData.position);
            Owner.ZoomAtPoint(local, eventData.scrollDelta.y > 0f ? 1.12f : 1f / 1.12f);
        }
    }
}

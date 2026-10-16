using UnityEngine;

namespace DarkRealms.World
{
    /// <summary>
    /// A map boundary inside a level: crossing it asks the transition service to traverse one specific
    /// connection. It holds no travel logic of its own and never loads a scene directly, so the atlas data
    /// stays the single description of what is traversable and the world map cannot disagree with it.
    /// </summary>
    [RequireComponent(typeof(Collider2D))]
    [DisallowMultipleComponent]
    public sealed class MapTravelTrigger : MonoBehaviour
    {
        [Tooltip("Atlas connection id this boundary traverses.")]
        [SerializeField] private string connectionId = string.Empty;

        [Tooltip("Tag that activates the boundary. The player object uses the Player tag.")]
        [SerializeField] private string activatorTag = "Player";

        [Tooltip("A boundary that stays armed can retrigger the moment the player is placed on top of it. " +
                 "Disarming until the player leaves prevents an accidental immediate return trip.")]
        [SerializeField] private bool disarmUntilExit = true;

        [SerializeField] private bool logRequests = true;

        private bool armed = true;

        public string ConnectionId => connectionId;
        public bool Armed => armed;

        private void OnTriggerEnter2D(Collider2D other)
        {
            if (!armed || other == null)
                return;

            if (!string.IsNullOrEmpty(activatorTag) && !other.CompareTag(activatorTag))
                return;

            if (disarmUntilExit)
                armed = false;

            RequestTravel();
        }

        private void OnTriggerExit2D(Collider2D other)
        {
            if (!disarmUntilExit || other == null)
                return;

            if (string.IsNullOrEmpty(activatorTag) || other.CompareTag(activatorTag))
                armed = true;
        }

        /// <summary>Public so a verification can exercise the boundary exactly as gameplay would.</summary>
        public MapTravelResult RequestTravel()
        {
            if (WorldAtlasRuntime.Instance == null)
                return MapTravelResult.Refused(MapTravelOutcome.NoRuntime, connectionId, "no WorldAtlasRuntime in the session");

            MapTravelResult result = WorldAtlasRuntime.Instance.Transition.RequestTravel(connectionId);

            if (logRequests)
            {
                if (result.Started)
                    Debug.Log($"[MapTravelTrigger] '{name}' traversing connection '{connectionId}' -> {result.ToMapId}");
                else
                    Debug.Log($"[MapTravelTrigger] '{name}' refused connection '{connectionId}': {result.Message}");
            }

            return result;
        }

#if UNITY_EDITOR
        /// <summary>Authoring entry point used by the prototype scene builder.</summary>
        public void EditorConfigure(string id)
        {
            connectionId = id;
        }
#endif
    }
}

using System;
using System.Collections;
using DarkRealms.Core;
using DarkRealms.Player;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DarkRealms.World
{
    /// <summary>Why a travel request did not happen. Every refusal names a cause so the UI can explain it.</summary>
    public enum MapTravelOutcome
    {
        /// <summary>The load was accepted and is running.</summary>
        Started,
        Travelled,
        NoRuntime,
        UnknownConnection,
        WrongMap,
        OneWay,
        Locked,
        SceneMissing,
        Busy,
        LoadFailed,
        MapMismatch,
        NoPlayer,
        Cancelled
    }

    /// <summary>Result of a travel request or of one completed transition.</summary>
    public readonly struct MapTravelResult
    {
        public readonly MapTravelOutcome Outcome;
        public readonly string ConnectionId;
        public readonly string FromMapId;
        public readonly string ToMapId;
        public readonly string ArrivalAnchorId;
        public readonly string Message;

        public MapTravelResult(MapTravelOutcome outcome, string connectionId, string fromMapId, string toMapId,
            string arrivalAnchorId, string message)
        {
            Outcome = outcome;
            ConnectionId = connectionId ?? string.Empty;
            FromMapId = fromMapId ?? string.Empty;
            ToMapId = toMapId ?? string.Empty;
            ArrivalAnchorId = arrivalAnchorId ?? string.Empty;
            Message = message ?? string.Empty;
        }

        /// <summary>True once a load has been accepted. A refusal is not a failure, it is a declined request.</summary>
        public bool Started => Outcome == MapTravelOutcome.Started;

        public bool Success => Outcome == MapTravelOutcome.Travelled;

        public static MapTravelResult Refused(MapTravelOutcome outcome, string connectionId, string message) =>
            new MapTravelResult(outcome, connectionId, string.Empty, string.Empty, string.Empty, message);

        public override string ToString() => $"{Outcome} ({ConnectionId}): {Message}";
    }

    /// <summary>
    /// The only place a scene is loaded during gameplay.
    ///
    /// Travel is validated against the atlas before anything is touched: the connection must exist, the current
    /// map must be one of its two ends, a one-way link must be used in its own direction, its availability rule
    /// must be satisfied and its scene must actually be loadable. Only then is the scene loaded, and the player's
    /// map is recorded - so a refused or failed transition leaves the session exactly where it was.
    ///
    /// One scene at a time. That is the simplest thing that is actually true about this project today; the
    /// interface is written so additive streaming could replace the load without changing any caller.
    /// </summary>
    [DisallowMultipleComponent]
    [RequireComponent(typeof(WorldAtlasRuntime))]
    public sealed class MapTransitionService : MonoBehaviour
    {
        [Tooltip("Logs each transition. Useful while integrating; can be switched off later.")]
        [SerializeField] private bool logTransitions = true;

        [Tooltip("Frames to wait for an asynchronous load before treating it as failed.")]
        [SerializeField] private int loadTimeoutFrames = 900;

        private bool travelling;
        private string lastDepartureMapId = string.Empty;
        private string lastDepartureAnchorId = string.Empty;
        private Vector2 lastDeparturePosition;

        public bool IsTravelling => travelling;
        public int CompletedTransitions { get; private set; }
        public int FailedTransitions { get; private set; }
        public string LastDepartureMapId => lastDepartureMapId;
        public string LastDepartureAnchorId => lastDepartureAnchorId;
        public Vector2 LastDeparturePosition => lastDeparturePosition;

        private WorldAtlasRuntime Runtime => WorldAtlasRuntime.Instance != null
            ? WorldAtlasRuntime.Instance
            : GetComponent<WorldAtlasRuntime>();

        // ------------------------------------------------------------------ validation

        /// <summary>
        /// Decides whether a request could go through, without touching the scene. The world map UI uses the same
        /// call, so what the map offers to click is exactly what travel will accept.
        /// </summary>
        public MapTravelResult ValidateTravel(string connectionId)
        {
            WorldAtlasRuntime runtime = Runtime;
            if (runtime == null || runtime.Atlas == null)
                return MapTravelResult.Refused(MapTravelOutcome.NoRuntime, connectionId, "no atlas runtime in the session");

            if (travelling)
                return MapTravelResult.Refused(MapTravelOutcome.Busy, connectionId, "a transition is already running");

            MapConnectionDefinition connection = runtime.Atlas.GetConnection(connectionId);
            if (connection == null)
                return MapTravelResult.Refused(MapTravelOutcome.UnknownConnection, connectionId, "connection is not in the atlas");

            string currentMapId = runtime.CurrentMapId;
            if (string.IsNullOrEmpty(currentMapId))
                return MapTravelResult.Refused(MapTravelOutcome.WrongMap, connectionId, "the current map is not known yet");

            if (!connection.IsTraversableFrom(currentMapId))
                return MapTravelResult.Refused(MapTravelOutcome.WrongMap, connectionId,
                    $"connection does not touch map '{currentMapId}'");

            if (!connection.TryResolveDestination(currentMapId, out string destinationMapId, out string arrivalAnchorId))
                return MapTravelResult.Refused(MapTravelOutcome.OneWay, connectionId,
                    $"'{connectionId}' is one-way from '{connection.fromMapId}'");

            MapDefinition destination = runtime.Atlas.GetMap(destinationMapId);
            if (destination == null)
                return MapTravelResult.Refused(MapTravelOutcome.UnknownConnection, connectionId,
                    $"destination map '{destinationMapId}' is not registered");

            if (!runtime.IsConnectionAvailable(connection, out string reason))
                return MapTravelResult.Refused(MapTravelOutcome.Locked, connectionId,
                    string.IsNullOrEmpty(reason) ? "connection is not available" : reason);

            if (ConnectionViolatesGeometry(runtime, connection))
                return MapTravelResult.Refused(MapTravelOutcome.Locked, connectionId,
                    "connection geometry crosses a blocked area; the route needs authoring");

            if (string.IsNullOrEmpty(destination.scenePath))
                return MapTravelResult.Refused(MapTravelOutcome.SceneMissing, connectionId,
                    $"map '{destination.id}' has no scene path");

            if (!Application.CanStreamedLevelBeLoaded(destination.scenePath))
                return MapTravelResult.Refused(MapTravelOutcome.SceneMissing, connectionId,
                    $"scene '{destination.scenePath}' is not in Build Settings");

            return new MapTravelResult(MapTravelOutcome.Started, connectionId, currentMapId, destinationMapId,
                arrivalAnchorId, "accepted");
        }

        private static bool ConnectionViolatesGeometry(WorldAtlasRuntime runtime, MapConnectionDefinition connection)
        {
            if (connection == null || runtime.Atlas.routeSettings == null || !runtime.Atlas.routeSettings.rejectGeometryThroughBlockers)
                return false;

            if (!connection.HasAuthoredGeometry)
                return false;

            // Connector geometry is authored on the world map, so it is tested against atlas-space blockers
            // (a range or a sea drawn across the atlas), not against a level's own blockers.
            return RouteGeometry.CrossesAnyAtlasBlocker(connection.atlasPolyline, runtime.Atlas);
        }

        // ------------------------------------------------------------------ travel

        /// <summary>Validates and starts a transition. Never throws and never leaves a half-applied state.</summary>
        public MapTravelResult RequestTravel(string connectionId)
        {
            MapTravelResult validation = ValidateTravel(connectionId);
            if (!validation.Started)
            {
                if (logTransitions && validation.Outcome != MapTravelOutcome.Busy)
                    Debug.Log($"[MapTransitionService] Refused '{connectionId}': {validation.Message}");

                return validation;
            }

            StartCoroutine(TravelRoutine(connectionId, null));
            return validation;
        }

        /// <summary>
        /// Performs the transition step by step. Yielded directly by verification so the whole sequence is
        /// observable, rather than fire-and-forget through a callback.
        /// </summary>
        public IEnumerator TravelRoutine(string connectionId, Action<MapTravelResult> onComplete)
        {
            MapTravelResult validation = ValidateTravel(connectionId);

            if (travelling && validation.Outcome != MapTravelOutcome.Busy)
            {
                // ValidateTravel would have refused already; this guard exists so a direct call cannot interleave.
                onComplete?.Invoke(MapTravelResult.Refused(MapTravelOutcome.Busy, connectionId, "already travelling"));
                yield break;
            }

            if (!validation.Started)
            {
                onComplete?.Invoke(validation);
                yield break;
            }

            WorldAtlasRuntime runtime = Runtime;
            travelling = true;

            CaptureDeparture(runtime, connectionId);

            // Progress is written before the load so a crash during the transition does not lose the last
            // completed state; the destination map is only recorded after the load has actually succeeded.
            runtime.SaveNow();

            MapDefinition destination = runtime.Atlas.GetMap(validation.ToMapId);

            AsyncOperation operation = null;
            string loadError = null;
            try
            {
                operation = SceneManager.LoadSceneAsync(destination.scenePath, LoadSceneMode.Single);
            }
            catch (Exception ex)
            {
                loadError = "scene load threw: " + ex.Message;
            }

            if (loadError == null && operation == null)
                loadError = "scene load returned no operation";

            MapTravelResult result;

            if (loadError != null)
            {
                // Nothing was unloaded: a refused or failed load must leave the session exactly where it was.
                result = new MapTravelResult(MapTravelOutcome.LoadFailed, validation.ConnectionId, validation.FromMapId,
                    destination.id, validation.ArrivalAnchorId, loadError);
            }
            else
            {
                operation.allowSceneActivation = true;

                // The wait is counted in frames rather than seconds because a step-driven verifier advances the
                // player loop explicitly; a timeout is reported as a failure instead of hanging the request.
                int frames = 0;
                while (!operation.isDone)
                {
                    if (frames++ > loadTimeoutFrames)
                    {
                        loadError = "scene load timed out after " + loadTimeoutFrames + " frames";
                        break;
                    }

                    yield return null;
                }

                result = loadError != null
                    ? new MapTravelResult(MapTravelOutcome.LoadFailed, validation.ConnectionId, validation.FromMapId,
                        destination.id, validation.ArrivalAnchorId, loadError)
                    : CompleteAfterLoad(runtime, validation, destination);
            }

            if (result.Success)
                CompletedTransitions++;
            else
                FailedTransitions++;

            if (logTransitions)
                Debug.Log($"[MapTransitionService] {result}");

            travelling = false;
            onComplete?.Invoke(result);
        }

        private void CaptureDeparture(WorldAtlasRuntime runtime, string connectionId)
        {
            MapConnectionDefinition connection = runtime.Atlas.GetConnection(connectionId);
            lastDepartureMapId = runtime.CurrentMapId;

            string anchorId = string.Equals(connection.fromMapId, lastDepartureMapId, StringComparison.Ordinal)
                ? connection.fromAnchorId
                : connection.toAnchorId;

            lastDepartureAnchorId = anchorId;
            lastDeparturePosition = ResolvePlayerPosition();
        }

        private static Vector2 ResolvePlayerPosition()
        {
            GameObject player = ResolvePlayerObject();
            return player != null ? (Vector2)player.transform.position : Vector2.zero;
        }

        /// <summary>Everything that happens once the destination scene is up: identity check, placement,
        /// completion tracking and the write that records the new map.</summary>
        private MapTravelResult CompleteAfterLoad(WorldAtlasRuntime runtime, MapTravelResult validation, MapDefinition destination)
        {
            Scene loaded = SceneManager.GetActiveScene();

            // The scene that came up must be the map that was asked for. A mismatch is reported instead of being
            // assumed away, and the scene's own identity wins because the loaded content is what the player sees.
            MapIdentity identity = UnityEngine.Object.FindAnyObjectByType<MapIdentity>();
            if (identity == null || !string.Equals(identity.MapId, destination.id, StringComparison.Ordinal))
            {
                string actual = identity != null ? identity.MapId : "<none>";
                PlacePlayerAtFallback(loaded);
                runtime.NotifySceneMapEntered(identity);
                return new MapTravelResult(MapTravelOutcome.MapMismatch, validation.ConnectionId, validation.FromMapId,
                    destination.id, validation.ArrivalAnchorId,
                    $"loaded scene reports map '{actual}' instead of '{destination.id}'; player placed at the scene default port");
            }

            MapEntryPoint port = MapEntryPoint.Find(loaded, validation.ArrivalAnchorId);
            if (port == null)
                port = MapEntryPoint.FindDefault(loaded);

            GameObject player = ResolvePlayerObject();
            if (player == null)
            {
                // The map itself loaded fine; only the actor is missing. Record the arrival so the session is
                // still consistent and let the caller decide what to do.
                runtime.SetCurrentMap(destination.id, validation.ArrivalAnchorId);
                runtime.SaveNow();
                return new MapTravelResult(MapTravelOutcome.NoPlayer, validation.ConnectionId, validation.FromMapId,
                    destination.id, validation.ArrivalAnchorId, "map loaded but no player object was found in the scene");
            }

            Vector2 arrival = port != null
                ? port.Position2D
                : ResolveAnchorPosition(runtime.Atlas, destination, validation.ArrivalAnchorId);

            PlacePlayer(player, arrival);

            // Respawn checkpoints follow the player, so dying in the new map does not send them back to the old one.
            if (GameManager.Instance != null)
                GameManager.Instance.SetSpawnPoint(port != null ? port.transform : player.transform);

            runtime.SetCurrentMap(destination.id, validation.ArrivalAnchorId);
            runtime.Progress.NoteDestination(destination.id);
            runtime.SaveNow();

            string detail = port != null
                ? $"travelled via '{validation.ConnectionId}' and placed at port '{port.AnchorId}'"
                : $"travelled via '{validation.ConnectionId}'; the scene has no port for '{validation.ArrivalAnchorId}', " +
                  "placed at the authored anchor position";

            return new MapTravelResult(MapTravelOutcome.Travelled, validation.ConnectionId, validation.FromMapId,
                destination.id, port != null ? port.AnchorId : validation.ArrivalAnchorId, detail);
        }

        private static void PlacePlayerAtFallback(Scene scene)
        {
            GameObject player = ResolvePlayerObject();
            if (player == null)
                return;

            MapEntryPoint port = MapEntryPoint.FindDefault(scene);
            if (port != null)
            {
                PlacePlayer(player, port.Position2D);
                return;
            }

            if (GameManager.Instance != null && GameManager.Instance.ActiveSpawn != null)
                PlacePlayer(player, GameManager.Instance.ActiveSpawn.position);
        }

        private static Vector2 ResolveAnchorPosition(WorldAtlasDefinition atlas, MapDefinition map, string anchorId)
        {
            MapAnchorDefinition anchor = map.FindAnchor(anchorId) ?? map.ResolveDefaultEntryAnchor();
            if (anchor != null)
                return anchor.position;

            return map.localBounds.center;
        }

        private static GameObject ResolvePlayerObject()
        {
            GameObject tagged = null;
            try
            {
                tagged = GameObject.FindWithTag("Player");
            }
            catch (UnityException)
            {
                tagged = null;
            }

            if (tagged != null)
                return tagged;

            PlayerController controller = UnityEngine.Object.FindAnyObjectByType<PlayerController>();
            return controller != null ? controller.gameObject : null;
        }

        private static void PlacePlayer(GameObject player, Vector2 position)
        {
            player.transform.position = new Vector3(position.x, position.y, player.transform.position.z);

            Rigidbody2D body = player.GetComponent<Rigidbody2D>();
            if (body != null)
            {
                body.linearVelocity = Vector2.zero;
                body.angularVelocity = 0f;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Text;
using DarkRealms.Core;
using DarkRealms.Player;
using DarkRealms.UI;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace DarkRealms.World
{
    /// <summary>
    /// The world map: an abstract top-down atlas of the world, plus the search, filter and route tools built on
    /// the same data the travel service uses.
    ///
    /// Two things it deliberately does not do. It never derives connectivity from layout - a line is drawn only
    /// for a connection that exists in the atlas - and it never invents a position: the player indicator comes
    /// from the level's own world-to-atlas transform, and a map with no recorded danger keeps saying so.
    ///
    /// The whole screen is built at runtime, matching the interface style the inventory module established. The
    /// atlas screen is the one interface that must outlive a scene load, so it is created once under the
    /// persistent atlas runtime instead of inside a map scene.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class WorldMapScreen : MonoBehaviour
    {
        // ------------------------------------------------------------------ layout
        private const float WindowWidth = 1600f;
        private const float WindowHeight = 880f;
        private const float ViewportWidth = 1080f;
        private const float ViewportHeight = 660f;
        private const float SideColumnWidth = 430f;
        private const float AtlasBaseExtent = 1500f;
        private const float MinZoom = 0.55f;
        private const float MaxZoom = 3.4f;
        private const float ClusterZoom = 0.9f;
        private const int ResultRowsPerPage = 11;

        // ------------------------------------------------------------------ state
        private WorldAtlasDefinition atlas;
        private readonly AtlasQueryBuilder builder = new AtlasQueryBuilder();
        private AtlasQueryResults results = new AtlasQueryResults();
        private RoutePlan route;
        private RouteObjective objective = RouteObjective.ShortestDistance;
        private string selectedMapId = string.Empty;
        private string destinationMapId = string.Empty;
        private int resultPage;
        private float zoom = 1f;
        private Vector2 pan;
        private bool built;
        private bool inputLockedByMap;
        private bool inputWasEnabledBeforeLock;
        private Text objectiveLabel;

        // ------------------------------------------------------------------ pools
        private sealed class Marker
        {
            public GameObject Go;
            public RectTransform Rect;
            public Image Icon;
            public Text Label;
        }

        private sealed class MarkerPool
        {
            private readonly List<Marker> pool = new List<Marker>();
            private readonly Transform parent;
            private int used;

            public MarkerPool(Transform parent) => this.parent = parent;

            public int ActiveCount => used;

            public void Begin() => used = 0;

            public Marker Next(MapMarkerShape shape, string name)
            {
                Marker marker;
                if (used < pool.Count)
                {
                    marker = pool[used];
                    marker.Go.SetActive(true);
                    marker.Icon.sprite = WorldMapIcons.Shape(shape);
                }
                else
                {
                    marker = Create(shape, name + "_" + pool.Count);
                    pool.Add(marker);
                }

                used++;
                return marker;
            }

            private Marker Create(MapMarkerShape shape, string name)
            {
                GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                go.transform.SetParent(parent, false);

                var rect = go.GetComponent<RectTransform>();
                rect.anchorMin = new Vector2(0.5f, 0.5f);
                rect.anchorMax = new Vector2(0.5f, 0.5f);
                rect.pivot = new Vector2(0.5f, 0.5f);

                var icon = go.GetComponent<Image>();
                icon.sprite = WorldMapIcons.Shape(shape);
                icon.raycastTarget = false;

                Text label = RuntimeUiFactory.Text("Label", go.transform, string.Empty, 15, TextAnchor.MiddleCenter,
                    new Vector2(180f, 24f), new Vector2(0f, -26f), Color.white);
                label.raycastTarget = false;

                return new Marker { Go = go, Rect = rect, Icon = icon, Label = label };
            }

            public void End()
            {
                for (int i = used; i < pool.Count; i++)
                    pool[i].Go.SetActive(false);
            }

            public void DestroyAll()
            {
                for (int i = 0; i < pool.Count; i++)
                    if (pool[i].Go != null)
                        Destroy(pool[i].Go);

                pool.Clear();
                used = 0;
            }
        }

        private sealed class LinePool
        {
            private readonly List<Image> pool = new List<Image>();
            private readonly Transform parent;
            private int used;

            public LinePool(Transform parent) => this.parent = parent;

            public int ActiveCount => used;

            public void Begin() => used = 0;

            /// <summary>Draws one straight quad between two content-space points. A polyline is drawn as a run of
            /// these, which keeps the route on the exact authored vertices instead of a smoothed UI approximation.</summary>
            public Image Segment(Vector2 from, Vector2 to, float thickness, Color color)
            {
                Image image;
                if (used < pool.Count)
                {
                    image = pool[used];
                    image.gameObject.SetActive(true);
                }
                else
                {
                    GameObject go = new GameObject("Segment", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
                    go.transform.SetParent(parent, false);
                    var rect = go.GetComponent<RectTransform>();
                    rect.anchorMin = new Vector2(0.5f, 0.5f);
                    rect.anchorMax = new Vector2(0.5f, 0.5f);
                    rect.pivot = new Vector2(0.5f, 0.5f);
                    image = go.GetComponent<Image>();
                    image.raycastTarget = false;
                    pool.Add(image);
                }

                used++;

                var imageRect = image.rectTransform;
                Vector2 delta = to - from;
                imageRect.sizeDelta = new Vector2(delta.magnitude, thickness);
                imageRect.anchoredPosition = (from + to) * 0.5f;
                imageRect.localRotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg);
                image.color = color;
                return image;
            }

            public void End()
            {
                for (int i = used; i < pool.Count; i++)
                    pool[i].gameObject.SetActive(false);
            }

            public void DestroyAll()
            {
                for (int i = 0; i < pool.Count; i++)
                    if (pool[i] != null)
                        Destroy(pool[i].gameObject);

                pool.Clear();
                used = 0;
            }
        }

        // ------------------------------------------------------------------ UI references
        private Canvas canvas;
        private RectTransform window;
        private RectTransform viewport;
        private RectTransform content;
        private RectTransform nodeLayer;
        private RectTransform markerLayer;
        private RectTransform routeLayer;
        private RectTransform localView;
        private Image atlasBackdrop;
        private Text statusText;
        private Text routeText;
        private Text resultCountText;
        private Text detailsText;
        private Text filterText;
        private Text pageText;
        private InputField searchField;
        private InputField dangerField;
        private readonly List<Button> resultButtons = new List<Button>();
        private readonly List<Toggle> facetToggles = new List<Toggle>();
        private readonly Dictionary<string, Image> nodeIcons = new Dictionary<string, Image>(StringComparer.Ordinal);
        private readonly Dictionary<string, Text> nodeLabels = new Dictionary<string, Text>(StringComparer.Ordinal);
        private MarkerPool nodeMarkers;
        private MarkerPool poiMarkers;
        private LinePool npcRouteLines;
        private MarkerPool localMarkers;
        private LinePool connectorLines;
        private LinePool routeLines;
        private LinePool localLines;
        private Marker playerMarker;

        // ------------------------------------------------------------------ read-only surface for verification
        public bool IsOpen { get; private set; }
        public float Zoom => zoom;
        public Vector2 Pan => pan;
        public string SelectedMapId => selectedMapId;
        public string DestinationMapId => destinationMapId;
        public RoutePlan LastRoute => route;
        public RouteObjective Objective => objective;
        public int ResultCount => results != null ? results.Count : 0;
        public int VisibleNodeCount => nodeMarkers != null ? nodeMarkers.ActiveCount : 0;
        public int VisibleMarkerCount => poiMarkers != null ? poiMarkers.ActiveCount : 0;
        public int VisibleRouteSegments => routeLines != null ? routeLines.ActiveCount : 0;
        public int VisibleConnectorSegments => connectorLines != null ? connectorLines.ActiveCount : 0;
        public int VisibleNpcRouteSegments => npcRouteLines != null ? npcRouteLines.ActiveCount : 0;
        public int VisibleLocalMarkerCount => localMarkers != null ? localMarkers.ActiveCount : 0;
        public RectTransform ViewportRect => viewport;
        public RectTransform ContentRect => content;
        public string StatusText => statusText != null ? statusText.text : string.Empty;
        public string RouteSummary => routeText != null ? routeText.text : string.Empty;
        public string DetailsText => detailsText != null ? detailsText.text : string.Empty;
        public string FilterText => filterText != null ? filterText.text : string.Empty;

        /// <summary>The drawn card icon for a map, so a verification can assert the current map is highlighted.</summary>
        public Image NodeIcon(string mapId) =>
            mapId != null && nodeIcons.TryGetValue(mapId, out Image icon) ? icon : null;

        public IReadOnlyList<Toggle> FacetToggles => facetToggles;

        private WorldAtlasRuntime Runtime => WorldAtlasRuntime.Instance;
        private WorldAtlasDefinition Atlas => atlas != null ? atlas : (Runtime != null ? Runtime.Atlas : null);
        private WorldProgress Progress => Runtime != null ? Runtime.Progress : null;
        private AtlasQueryContext Context => new AtlasQueryContext(Atlas, Progress, Runtime != null ? Runtime.CurrentMapId : string.Empty);

        public static WorldMapScreen Create(Transform parent, WorldAtlasDefinition definition)
        {
            var go = new GameObject("DarkRealms World Map Screen", typeof(RectTransform));
            go.transform.SetParent(parent, false);
            var screen = go.AddComponent<WorldMapScreen>();
            screen.Initialize(definition);
            return screen;
        }

        // ================================================================== construction

        public void Initialize(WorldAtlasDefinition definition)
        {
            atlas = definition;

            if (built)
            {
                RebuildAtlasLayer();
                RunQuery();
                return;
            }

            built = true;
            BuildInterface();
            RebuildAtlasLayer();
            RunQuery();
            SetOpen(false);
        }

        private void BuildInterface()
        {
            canvas = BuildCanvas();
            window = MakeWindow(canvas.transform);
            BuildHeader(window);
            viewport = BuildViewport(window);
            BuildSideColumn(window);
            BuildFooter(window);
            BuildLegend(window);
        }

        private Canvas BuildCanvas()
        {
            GameObject canvasObject = new GameObject("World Map Canvas", typeof(RectTransform), typeof(Canvas),
                typeof(CanvasScaler), typeof(GraphicRaycaster));
            canvasObject.transform.SetParent(transform, false);

            Canvas built = canvasObject.GetComponent<Canvas>();
            built.renderMode = RenderMode.ScreenSpaceOverlay;
            built.sortingOrder = 200;

            CanvasScaler scaler = canvasObject.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            // A dimmer that swallows clicks, so the world map really is modal instead of clicking through to the
            // inventory or the level behind it.
            Image dim = RuntimeUiFactory.Image("Dimmer", canvasObject.transform, new Vector2(6000f, 4000f), Vector2.zero,
                new Color(0.02f, 0.025f, 0.035f, 0.86f), true);
            RuntimeUiFactory.StretchFull(dim.rectTransform);

            return built;
        }

        private RectTransform MakeWindow(Transform parent)
        {
            GameObject frame = RuntimeUiFactory.Panel("World Map Window", parent,
                new Vector2(WindowWidth, WindowHeight), Vector2.zero, new Color(0.055f, 0.062f, 0.075f, 0.98f));

            RectTransform rect = frame.GetComponent<RectTransform>();

            // Outer frame: four bars, plus a thin inner accent, so the border reads as a frame rather than a
            // slightly different coloured edge.
            var border = new Color(0.55f, 0.46f, 0.28f, 1f);
            float hw = WindowWidth * 0.5f, hh = WindowHeight * 0.5f;
            RuntimeUiFactory.Image("Frame_Top", rect, new Vector2(WindowWidth, 6f), new Vector2(0f, hh), border, false);
            RuntimeUiFactory.Image("Frame_Bottom", rect, new Vector2(WindowWidth, 6f), new Vector2(0f, -hh), border, false);
            RuntimeUiFactory.Image("Frame_Left", rect, new Vector2(6f, WindowHeight), new Vector2(-hw, 0f), border, false);
            RuntimeUiFactory.Image("Frame_Right", rect, new Vector2(6f, WindowHeight), new Vector2(hw, 0f), border, false);

            var inner = new Color(0.30f, 0.26f, 0.17f, 1f);
            float iw = hw - 10f, ih = hh - 10f;
            RuntimeUiFactory.Image("Frame_Inner_Top", rect, new Vector2(WindowWidth - 20f, 2f), new Vector2(0f, ih), inner, false);
            RuntimeUiFactory.Image("Frame_Inner_Bottom", rect, new Vector2(WindowWidth - 20f, 2f), new Vector2(0f, -ih), inner, false);
            RuntimeUiFactory.Image("Frame_Inner_Left", rect, new Vector2(2f, WindowHeight - 20f), new Vector2(-iw, 0f), inner, false);
            RuntimeUiFactory.Image("Frame_Inner_Right", rect, new Vector2(2f, WindowHeight - 20f), new Vector2(iw, 0f), inner, false);

            return rect;
        }

        private void BuildHeader(RectTransform parent)
        {
            RuntimeUiFactory.Text("Title", parent, "WORLD ATLAS", 34, TextAnchor.MiddleLeft,
                new Vector2(600f, 46f), new Vector2(-WindowWidth * 0.5f + 340f, WindowHeight * 0.5f - 44f),
                new Color(0.93f, 0.85f, 0.62f, 1f));

            RuntimeUiFactory.Text("Subtitle", parent, "top-down atlas · travel graph · search and route planning", 17,
                TextAnchor.MiddleLeft, new Vector2(700f, 26f), new Vector2(-WindowWidth * 0.5f + 390f, WindowHeight * 0.5f - 74f),
                new Color(0.63f, 0.67f, 0.73f, 1f));

            Button close = RuntimeUiFactory.Button("Close", parent, "X", 24,
                new Vector2(52f, 46f), new Vector2(WindowWidth * 0.5f - 50f, WindowHeight * 0.5f - 44f),
                new Color(0.24f, 0.16f, 0.16f, 1f), Color.white);
            close.onClick.AddListener(() => SetOpen(false));

            Button zoomOut = RuntimeUiFactory.Button("ZoomOut", parent, "-", 26, new Vector2(52f, 40f),
                new Vector2(WindowWidth * 0.5f - 126f, WindowHeight * 0.5f - 44f), new Color(0.15f, 0.17f, 0.21f, 1f), Color.white);
            zoomOut.onClick.AddListener(() => ZoomBy(1f / 1.25f));

            Button zoomIn = RuntimeUiFactory.Button("ZoomIn", parent, "+", 26, new Vector2(52f, 40f),
                new Vector2(WindowWidth * 0.5f - 70f, WindowHeight * 0.5f - 44f), new Color(0.15f, 0.17f, 0.21f, 1f), Color.white);
            zoomIn.onClick.AddListener(() => ZoomBy(1.25f));

            Button fit = RuntimeUiFactory.Button("Fit", parent, "FIT", 18, new Vector2(78f, 40f),
                new Vector2(WindowWidth * 0.5f - 200f, WindowHeight * 0.5f - 44f), new Color(0.15f, 0.17f, 0.21f, 1f), Color.white);
            fit.onClick.AddListener(FitToAll);

            Button center = RuntimeUiFactory.Button("Center", parent, "CENTER", 18, new Vector2(108f, 40f),
                new Vector2(WindowWidth * 0.5f - 292f, WindowHeight * 0.5f - 44f), new Color(0.15f, 0.17f, 0.21f, 1f), Color.white);
            center.onClick.AddListener(CenterOnCurrentMap);
        }

        private RectTransform BuildViewport(RectTransform parent)
        {
            GameObject viewportObject = RuntimeUiFactory.Panel("Atlas Viewport", parent,
                new Vector2(ViewportWidth, ViewportHeight),
                new Vector2(-WindowWidth * 0.5f + ViewportWidth * 0.5f + 18f, -6f),
                new Color(0.035f, 0.042f, 0.055f, 1f));

            RectTransform viewportRect = viewportObject.GetComponent<RectTransform>();
            viewportObject.AddComponent<RectMask2D>();

            // Dragging the viewport pans; a drag that starts on a marker still pans, but a click does not, so
            // selecting a marker never moves the map under the player's finger.
            var drag = viewportObject.AddComponent<MapViewportInput>();
            drag.Owner = this;

            GameObject contentObject = new GameObject("Atlas Content", typeof(RectTransform));
            contentObject.transform.SetParent(viewportObject.transform, false);
            content = contentObject.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0.5f, 0.5f);
            content.anchorMax = new Vector2(0.5f, 0.5f);
            content.pivot = new Vector2(0.5f, 0.5f);

            atlasBackdrop = RuntimeUiFactory.Image("Backdrop", content, new Vector2(AtlasBaseExtent, AtlasBaseExtent),
                Vector2.zero, new Color(0.075f, 0.095f, 0.115f, 1f), false);

            nodeLayer = MakeLayer("Connections", content);
            routeLayer = MakeLayer("Route", content);
            markerLayer = MakeLayer("Markers", content);

            nodeMarkers = new MarkerPool(nodeLayer);
            poiMarkers = new MarkerPool(markerLayer);
            routeLines = new LinePool(routeLayer);
            connectorLines = new LinePool(nodeLayer);

            playerMarker = CreatePlayerMarker(content);

            return viewportRect;
        }

        private static RectTransform MakeLayer(string name, RectTransform parent)
        {
            GameObject layer = new GameObject(name, typeof(RectTransform));
            layer.transform.SetParent(parent, false);
            RectTransform rect = layer.GetComponent<RectTransform>();
            RuntimeUiFactory.StretchFull(rect);
            return rect;
        }

        private Marker CreatePlayerMarker(RectTransform parent)
        {
            GameObject go = new GameObject("PlayerIndicator", typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var rect = go.GetComponent<RectTransform>();
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);

            var marker = new Marker { Go = go, Rect = rect, Icon = go.GetComponent<Image>() };
            marker.Icon.sprite = WorldMapIcons.Shape(MapMarkerShape.Ring);
            marker.Icon.color = new Color(0.99f, 0.93f, 0.55f, 1f);
            marker.Icon.raycastTarget = false;
            marker.Label = RuntimeUiFactory.Text("Label", go.transform, "YOU", 13, TextAnchor.MiddleCenter,
                new Vector2(120f, 20f), new Vector2(0f, -22f), new Color(0.99f, 0.93f, 0.55f, 1f));
            marker.Label.raycastTarget = false;
            return marker;
        }

        private void BuildSideColumn(RectTransform parent)
        {
            float x = WindowWidth * 0.5f - SideColumnWidth * 0.5f - 18f;

            GameObject panel = RuntimeUiFactory.Panel("Search And Details", parent, new Vector2(SideColumnWidth, ViewportHeight),
                new Vector2(x, -6f), new Color(0.045f, 0.052f, 0.064f, 0.98f));
            RectTransform rect = panel.GetComponent<RectTransform>();

            RuntimeUiFactory.Text("SearchLabel", rect, "SEARCH  (name / alias)", 15, TextAnchor.MiddleLeft,
                new Vector2(SideColumnWidth - 40f, 22f), new Vector2(0f, ViewportHeight * 0.5f - 26f),
                new Color(0.72f, 0.76f, 0.82f, 1f));

            searchField = RuntimeUiFactory.InputField("SearchField", rect, "type a name, then Enter", 17,
                new Vector2(SideColumnWidth - 40f, 36f), new Vector2(0f, ViewportHeight * 0.5f - 54f),
                UnityEngine.UI.InputField.ContentType.Standard, false);
            searchField.onEndEdit.AddListener(HandleSearchEdited);

            filterText = RuntimeUiFactory.Text("Filters", rect, string.Empty, 14, TextAnchor.UpperLeft,
                new Vector2(SideColumnWidth - 40f, 122f), new Vector2(0f, ViewportHeight * 0.5f - 138f),
                new Color(0.66f, 0.72f, 0.80f, 1f));

            BuildFacetToggles(rect);

            resultCountText = RuntimeUiFactory.Text("ResultCount", rect, string.Empty, 16, TextAnchor.MiddleLeft,
                new Vector2(SideColumnWidth - 40f, 22f), new Vector2(0f, -44f), new Color(0.90f, 0.84f, 0.62f, 1f));

            for (int i = 0; i < ResultRowsPerPage; i++)
            {
                int index = i;
                Button row = RuntimeUiFactory.Button("Result" + i, rect, string.Empty, 15,
                    new Vector2(SideColumnWidth - 40f, 22f), new Vector2(0f, -66f - i * 24f),
                    new Color(0.075f, 0.085f, 0.10f, 1f), new Color(0.88f, 0.90f, 0.94f, 1f));
                row.onClick.AddListener(() => FocusResultRow(index));
                resultButtons.Add(row);
            }

            Button prev = RuntimeUiFactory.Button("PagePrev", rect, "<", 18, new Vector2(40f, 26f),
                new Vector2(-170f, -352f), new Color(0.13f, 0.15f, 0.19f, 1f), Color.white);
            prev.onClick.AddListener(() => ChangePage(-1));

            pageText = RuntimeUiFactory.Text("PageText", rect, string.Empty, 15, TextAnchor.MiddleCenter,
                new Vector2(120f, 22f), new Vector2(0f, -352f), new Color(0.70f, 0.74f, 0.80f, 1f));

            Button next = RuntimeUiFactory.Button("PageNext", rect, ">", 18, new Vector2(40f, 26f),
                new Vector2(170f, -352f), new Color(0.13f, 0.15f, 0.19f, 1f), Color.white);
            next.onClick.AddListener(() => ChangePage(1));

            Button clear = RuntimeUiFactory.Button("Clear", rect, "CLEAR FILTERS", 15, new Vector2(150f, 28f),
                new Vector2(-130f, -384f), new Color(0.20f, 0.16f, 0.16f, 1f), Color.white);
            clear.onClick.AddListener(ClearFilters);

            Button advanced = RuntimeUiFactory.Button("AdvancedAnd", rect, "+AND GROUP", 14, new Vector2(110f, 26f),
                new Vector2(-176f, -412f), new Color(0.14f, 0.19f, 0.24f, 1f), Color.white);
            advanced.onClick.AddListener(() => AddAdvancedGroup(FilterGroupOp.And));

            Button advancedOr = RuntimeUiFactory.Button("AdvancedOr", rect, "+OR", 14, new Vector2(56f, 26f),
                new Vector2(-88f, -412f), new Color(0.14f, 0.19f, 0.24f, 1f), Color.white);
            advancedOr.onClick.AddListener(() => AddAdvancedGroup(FilterGroupOp.Or));

            Button advancedNot = RuntimeUiFactory.Button("AdvancedNot", rect, "+NOT", 14, new Vector2(56f, 26f),
                new Vector2(-24f, -412f), new Color(0.20f, 0.14f, 0.14f, 1f), Color.white);
            advancedNot.onClick.AddListener(() => AddAdvancedGroup(FilterGroupOp.Not));

            Button unknownDanger = RuntimeUiFactory.Button("UnknownDanger", rect, "ONLY UNKNOWN DANGER", 13,
                new Vector2(180f, 26f), new Vector2(122f, -412f), new Color(0.12f, 0.14f, 0.18f, 1f), Color.white);
            unknownDanger.onClick.AddListener(() =>
            {
                builder.IncludeUnknownDanger = !builder.IncludeUnknownDanger;
                RunQuery();
            });

            dangerField = RuntimeUiFactory.InputField("DangerField", rect, "danger max", 15,
                new Vector2(110f, 28f), new Vector2(-152f, -444f), UnityEngine.UI.InputField.ContentType.IntegerNumber, true);
            dangerField.onEndEdit.AddListener(HandleDangerEdited);

            RuntimeUiFactory.Text("DangerHint", rect, "danger <=", 13, TextAnchor.MiddleLeft,
                new Vector2(80f, 22f), new Vector2(-232f, -444f), new Color(0.66f, 0.72f, 0.80f, 1f));

            detailsText = RuntimeUiFactory.Text("Details", rect, string.Empty, 14, TextAnchor.UpperLeft,
                new Vector2(SideColumnWidth - 40f, 130f), new Vector2(0f, -520f), new Color(0.84f, 0.88f, 0.94f, 1f));

            routeText = RuntimeUiFactory.Text("Route", rect, string.Empty, 14, TextAnchor.UpperLeft,
                new Vector2(SideColumnWidth - 40f, 100f), new Vector2(0f, -660f), new Color(0.78f, 0.90f, 0.72f, 1f));

            GameObject localObject = RuntimeUiFactory.Panel("LocalMap", rect, new Vector2(SideColumnWidth - 40f, 150f),
                new Vector2(0f, -790f), new Color(0.03f, 0.036f, 0.046f, 1f));
            localView = localObject.GetComponent<RectTransform>();
            var localDrag = localObject.AddComponent<MapViewportInput>();
            localDrag.Owner = this;
            localDrag.LocalMap = true;
            localMarkers = new MarkerPool(localView);
            localLines = new LinePool(localView);
            npcRouteLines = new LinePool(localView);
        }

        private void BuildFacetToggles(RectTransform parent)
        {
            var entries = new (string label, Action<bool> apply)[]
            {
                ("has dungeon", on => SetFlag(MapFlag.HasDungeon, on)),
                ("has town", on => SetFlag(MapFlag.HasTown, on)),
                ("has boss", on => SetFlag(MapFlag.HasBoss, on)),
                ("has chest", on => SetAnyChest(on)),
                ("has guild/altar", on => SetFlagPair(MapFlag.HasGuild, MapFlag.HasAltar, on)),
                ("story marker", on => SetStory(on)),
                ("visited", on => SetVisited(on)),
                ("undiscovered", on => SetUndiscovered(on)),
                ("reachable", on => SetReachable(on)),
                ("npc route data", on => SetNpcRoute(on)),
                ("unfinished", on => SetPending(on))
            };

            const float columnX = -110f;
            for (int i = 0; i < entries.Length; i++)
            {
                int column = i / 6;
                float y = ViewportHeight * 0.5f - 208f - (i % 6) * 30f;
                float x = columnX + column * 215f;

                Toggle toggle = RuntimeUiFactory.Toggle("Facet" + i, parent, entries[i].label, 14,
                    new Vector2(206f, 28f), new Vector2(x, y), out Text label);
                label.raycastTarget = false;
                Action<bool> apply = entries[i].apply;
                toggle.onValueChanged.AddListener(on => apply(on));
                facetToggles.Add(toggle);
            }
        }

        private void BuildFooter(RectTransform parent)
        {
            statusText = RuntimeUiFactory.Text("Status", parent, string.Empty, 15, TextAnchor.MiddleLeft,
                new Vector2(WindowWidth - 620f, 24f), new Vector2(-WindowWidth * 0.5f + 300f, -WindowHeight * 0.5f + 32f),
                new Color(0.74f, 0.78f, 0.84f, 1f));

            Button objectiveButton = RuntimeUiFactory.Button("Objective", parent, ObjectiveLabel(objective), 15,
                new Vector2(230f, 32f), new Vector2(WindowWidth * 0.5f - 420f, -WindowHeight * 0.5f + 32f),
                new Color(0.13f, 0.17f, 0.22f, 1f), Color.white);
            objectiveButton.onClick.AddListener(CycleObjective);
            objectiveLabel = objectiveButton.GetComponentInChildren<Text>();

            Button planButton = RuntimeUiFactory.Button("PlanRoute", parent, "PLAN ROUTE", 15,
                new Vector2(150f, 32f), new Vector2(WindowWidth * 0.5f - 240f, -WindowHeight * 0.5f + 32f),
                new Color(0.14f, 0.22f, 0.16f, 1f), Color.white);
            planButton.onClick.AddListener(() => PlanRouteTo(selectedMapId));

            Button travelButton = RuntimeUiFactory.Button("Travel", parent, "TRAVEL", 15,
                new Vector2(110f, 32f), new Vector2(WindowWidth * 0.5f - 100f, -WindowHeight * 0.5f + 32f),
                new Color(0.24f, 0.18f, 0.12f, 1f), Color.white);
            travelButton.onClick.AddListener(() => TravelThroughRoute());
        }

        private void BuildLegend(RectTransform parent)
        {
            GameObject panel = RuntimeUiFactory.Panel("Legend", parent, new Vector2(320f, 168f),
                new Vector2(-WindowWidth * 0.5f + 178f, -WindowHeight * 0.5f + 108f), new Color(0.04f, 0.047f, 0.058f, 0.96f));

            RuntimeUiFactory.Text("LegendTitle", panel.transform, "LEGEND", 15, TextAnchor.MiddleLeft,
                new Vector2(280f, 22f), new Vector2(0f, 66f), new Color(0.88f, 0.82f, 0.62f, 1f));

            var entries = new (string label, MapMarkerShape shape, Color color)[]
            {
                ("current map / you", MapMarkerShape.Ring, new Color(0.99f, 0.93f, 0.55f, 1f)),
                ("visited map", MapMarkerShape.Circle, new Color(0.72f, 0.78f, 0.86f, 1f)),
                ("undiscovered map", MapMarkerShape.Circle, new Color(0.42f, 0.46f, 0.55f, 1f)),
                ("chest (tier by colour)", MapMarkerShape.Square, new Color(0.78f, 0.72f, 0.52f, 1f)),
                ("boss / boss arena", MapMarkerShape.Diamond, new Color(0.86f, 0.30f, 0.32f, 1f)),
                ("schematic link", MapMarkerShape.Bar, new Color(0.58f, 0.62f, 0.70f, 0.75f))
            };

            for (int i = 0; i < entries.Length; i++)
            {
                float y = 40f - i * 20f;
                Image icon = RuntimeUiFactory.Image("LegendIcon" + i, panel.transform, new Vector2(16f, 16f),
                    new Vector2(-134f, y), entries[i].color, false);
                icon.sprite = WorldMapIcons.Shape(entries[i].shape);

                RuntimeUiFactory.Text("LegendLabel" + i, panel.transform, entries[i].label, 13, TextAnchor.MiddleLeft,
                    new Vector2(240f, 18f), new Vector2(8f, y), new Color(0.80f, 0.84f, 0.90f, 1f));
            }
        }

        // ================================================================== atlas rendering

        private void RebuildAtlasLayer()
        {
            WorldAtlasDefinition definition = Atlas;
            if (definition == null || nodeMarkers == null)
                return;

            nodeIcons.Clear();
            nodeLabels.Clear();

            connectorLines.Begin();
            nodeMarkers.Begin();
            poiMarkers.Begin();

            var current = Context;
            string currentMapId = current.CurrentMapId;

            if (definition.connections != null)
            {
                for (int i = 0; i < definition.connections.Length; i++)
                {
                    MapConnectionDefinition connection = definition.connections[i];
                    if (connection == null)
                        continue;

                    MapDefinition fromMap = definition.GetMap(connection.fromMapId);
                    MapDefinition toMap = definition.GetMap(connection.toMapId);
                    if (fromMap == null || toMap == null)
                        continue;

                    Vector2[] atlasPoints = connection.HasAuthoredGeometry
                        ? connection.atlasPolyline
                        : new[] { fromMap.atlasPosition, toMap.atlasPosition };

                    bool available = connection.condition == null || connection.condition.IsSatisfied(Progress) ||
                                     (Progress != null && Progress.IsConnectionUnlocked(connection.id));

                    Color color = available
                        ? (connection.HasAuthoredGeometry
                            ? new Color(0.52f, 0.58f, 0.68f, 0.95f)
                            : new Color(0.58f, 0.62f, 0.70f, 0.45f))
                        : new Color(0.72f, 0.34f, 0.30f, 0.85f);

                    DrawAtlasPolyline(connectorLines, atlasPoints, 3f, color);
                }
            }

            if (definition.maps == null)
                return;

            for (int i = 0; i < definition.maps.Length; i++)
            {
                MapDefinition map = definition.maps[i];
                if (map == null)
                    continue;

                bool isCurrent = string.Equals(map.id, currentMapId, StringComparison.Ordinal);
                bool isSelected = string.Equals(map.id, selectedMapId, StringComparison.Ordinal);
                bool isDestination = string.Equals(map.id, destinationMapId, StringComparison.Ordinal);
                bool isVisited = Progress != null && Progress.HasVisited(map.id);
                bool isKnown = isVisited || (Progress != null && Progress.IsDiscovered(map.id)) || isCurrent;

                Marker marker = nodeMarkers.Next(WorldMapIcons.ShapeForMap(map.mapType), "Map_" + map.id);
                Vector2 position = AtlasToContent(map.atlasPosition);
                marker.Rect.sizeDelta = isSelected ? new Vector2(40f, 40f) : new Vector2(30f, 30f);
                marker.Rect.anchoredPosition = position;
                marker.Icon.color = WorldMapIcons.ColorForMap(isCurrent, isSelected, isDestination, isVisited);
                marker.Icon.raycastTarget = true;

                // Undiscovered maps stay legible as "something is here" without naming it: the atlas is a spoiler
                // surface, so an unvisited map shows its type shape and a placeholder name only when it has one.
                marker.Label.text = isKnown ? map.displayName : "???";
                marker.Label.color = isKnown ? new Color(0.90f, 0.93f, 0.97f, 1f) : new Color(0.58f, 0.61f, 0.66f, 1f);

                Button button = marker.Go.GetComponent<Button>();
                if (button == null)
                    button = marker.Go.AddComponent<Button>();

                button.targetGraphic = marker.Icon;
                string mapId = map.id;
                button.onClick.RemoveAllListeners();
                button.onClick.AddListener(() => SelectMap(mapId));

                nodeIcons[map.id] = marker.Icon;
                nodeLabels[map.id] = marker.Label;
            }

            RefreshPoiMarkers();
        }

        private void DrawAtlasPolyline(LinePool pool, Vector2[] atlasPoints, float thickness, Color color)
        {
            if (atlasPoints == null || atlasPoints.Length < 2)
                return;

            for (int i = 1; i < atlasPoints.Length; i++)
                pool.Segment(AtlasToContent(atlasPoints[i - 1]), AtlasToContent(atlasPoints[i]), thickness, color);
        }

        /// <summary>
        /// Draws markers, clustered when zoomed out. Clustering keeps the atlas readable instead of stacking
        /// hundreds of icons on three cards - and it is a rendering decision only: the underlying marker set is
        /// unchanged, so a click still resolves to a real marker (or to the group's map).
        /// </summary>
        private void RefreshPoiMarkers()
        {
            WorldAtlasDefinition definition = Atlas;
            poiMarkers.Begin();

            if (definition == null || definition.pois == null)
            {
                poiMarkers.End();
                return;
            }

            bool cluster = zoom <= ClusterZoom;
            var clusters = new Dictionary<string, List<MapPoiDefinition>>(StringComparer.Ordinal);

            for (int i = 0; i < definition.pois.Length; i++)
            {
                MapPoiDefinition poi = definition.pois[i];
                if (poi == null)
                    continue;

                MapDefinition map = definition.GetMap(poi.mapId);
                if (map == null)
                    continue;

                if (cluster)
                {
                    string key = poi.mapId + "|" + poi.kind;
                    if (!clusters.TryGetValue(key, out List<MapPoiDefinition> list))
                    {
                        list = new List<MapPoiDefinition>();
                        clusters[key] = list;
                    }

                    list.Add(poi);
                    continue;
                }

                DrawPoiMarker(poi, map, 1);
            }

            if (!cluster)
            {
                poiMarkers.End();
                return;
            }

            foreach (KeyValuePair<string, List<MapPoiDefinition>> pair in clusters)
            {
                MapPoiDefinition first = pair.Value[0];
                MapDefinition map = definition.GetMap(first.mapId);
                if (map == null)
                    continue;

                DrawPoiMarker(first, map, pair.Value.Count);
            }

            poiMarkers.End();
        }

        private void DrawPoiMarker(MapPoiDefinition poi, MapDefinition map, int count)
        {
            Marker marker = poiMarkers.Next(WorldMapIcons.ShapeForPoi(poi.kind), "Poi_" + poi.id);
            marker.Rect.sizeDelta = new Vector2(18f, 18f);
            marker.Rect.anchoredPosition = AtlasToContent(RouteGeometry.LevelToAtlas(map, poi.position));
            marker.Icon.color = WorldMapIcons.ColorForPoi(poi.kind);
            marker.Label.text = count > 1 ? count.ToString() : string.Empty;
            marker.Label.raycastTarget = false;
        }

        private void RefreshPlayerMarker()
        {
            if (playerMarker == null)
                return;

            MapDefinition map = Atlas != null ? Atlas.GetMap(Runtime != null ? Runtime.CurrentMapId : string.Empty) : null;
            if (map == null)
            {
                playerMarker.Go.SetActive(false);
                return;
            }

            Vector2 levelPosition = map.ResolveDefaultEntryAnchor() != null
                ? map.ResolveDefaultEntryAnchor().position
                : map.localBounds.center;

            GameObject player = ResolvePlayer();
            if (player != null)
                levelPosition = player.transform.position;

            playerMarker.Go.SetActive(true);
            playerMarker.Rect.sizeDelta = new Vector2(30f, 30f);
            playerMarker.Rect.anchoredPosition = AtlasToContent(RouteGeometry.LevelToAtlas(map, levelPosition));
        }

        private static GameObject ResolvePlayer()
        {
            try
            {
                GameObject tagged = GameObject.FindWithTag("Player");
                if (tagged != null)
                    return tagged;
            }
            catch (UnityException)
            {
                // The tag can be missing in a bare test scene; falling through to the controller lookup is fine.
            }

            PlayerController controller = FindAnyObjectByType<PlayerController>();
            return controller != null ? controller.gameObject : null;
        }

        /// <summary>Routes are drawn from the planner's own leg geometry, so the drawn line and the walked route
        /// cannot disagree: no centre-to-centre chord is ever drawn for a road.</summary>
        private void RefreshRouteLayer()
        {
            routeLines.Begin();

            if (route == null || !route.Found)
            {
                routeLines.End();
                RefreshLocalMap();
                return;
            }

            for (int i = 0; i < route.Legs.Count; i++)
            {
                RouteLeg leg = route.Legs[i];
                MapDefinition map = Atlas.GetMap(leg.MapId);
                Vector2 previous = Vector2.zero;
                bool hasPrevious = false;

                for (int p = 0; p < leg.Points.Count; p++)
                {
                    Vector2 atlasPoint = leg.IsConnection || map == null
                        ? leg.Points[p]
                        : RouteGeometry.LevelToAtlas(map, leg.Points[p]);

                    Vector2 content = AtlasToContent(atlasPoint);
                    if (hasPrevious)
                        routeLines.Segment(previous, content, 5f,
                            leg.IsSchematic ? new Color(0.95f, 0.72f, 0.30f, 0.85f) : new Color(0.52f, 0.92f, 0.55f, 0.95f));

                    previous = content;
                    hasPrevious = true;
                }
            }

            routeLines.End();
            RefreshLocalMap();
        }

        /// <summary>The local view of the selected map: level bounds, blockers, authored road geometry, markers
        /// and - when a route runs through this map - that route's waypoints.</summary>
        private void RefreshLocalMap()
        {
            if (localMarkers == null || localLines == null)
                return;

            localMarkers.Begin();
            localLines.Begin();

            MapDefinition map = Atlas != null ? Atlas.GetMap(selectedMapId) : null;
            if (map == null)
            {
                localMarkers.End();
                localLines.End();
                return;
            }

            Rect bounds = map.localBounds;

            // Blockers first, as filled shapes.
            IReadOnlyList<AtlasBlockerDefinition> blockers = Atlas.BlockersOf(map.id);
            for (int i = 0; i < blockers.Count; i++)
            {
                AtlasBlockerDefinition blocker = blockers[i];
                if (blocker == null || !blocker.IsWellFormed)
                    continue;

                for (int p = 0; p < blocker.points.Length; p++)
                {
                    Vector2 a = LocalToView(blocker.points[p], bounds);
                    Vector2 b = LocalToView(blocker.points[(p + 1) % blocker.points.Length], bounds);
                    localLines.Segment(a, b, 4f, new Color(0.30f, 0.16f, 0.16f, 0.95f));
                }
            }

            // Authored road geometry.
            if (map.roadSegments != null)
            {
                for (int s = 0; s < map.roadSegments.Length; s++)
                {
                    MapRoadSegmentDefinition segment = map.roadSegments[s];
                    if (segment == null)
                        continue;

                    Vector2[] points = RouteGeometry.ResolveSegmentPoints(map, segment);
                    for (int p = 1; p < points.Length; p++)
                        localLines.Segment(LocalToView(points[p - 1], bounds), LocalToView(points[p], bounds),
                            2.5f, segment.HasAuthoredGeometry
                                ? new Color(0.62f, 0.66f, 0.74f, 0.9f)
                                : new Color(0.62f, 0.66f, 0.74f, 0.35f));
                }
            }

            // The selected route inside this map, when there is one.
            if (route != null && route.Found)
            {
                for (int i = 0; i < route.Legs.Count; i++)
                {
                    RouteLeg leg = route.Legs[i];
                    if (leg.IsConnection || !string.Equals(leg.MapId, map.id, StringComparison.Ordinal))
                        continue;

                    for (int p = 1; p < leg.Points.Count; p++)
                        localLines.Segment(LocalToView(leg.Points[p - 1], bounds), LocalToView(leg.Points[p], bounds),
                            3.5f, new Color(0.52f, 0.92f, 0.55f, 0.95f));
                }
            }

            // NPC routes are drawn only when the data supports a route and the visibility policy allows it: an
            // authored-but-incomplete route stays invisible instead of becoming a line to nowhere.
            npcRouteLines.Begin();
            IReadOnlyList<NpcRouteDefinition> npcRoutes = Atlas.NpcRoutesOf(map.id);
            for (int i = 0; i < npcRoutes.Count; i++)
            {
                NpcRouteDefinition npcRoute = npcRoutes[i];
                if (!ShouldDrawNpcRoute(npcRoute, Progress))
                    continue;

                for (int p = 1; p < npcRoute.waypoints.Length; p++)
                    npcRouteLines.Segment(LocalToView(npcRoute.waypoints[p - 1], bounds),
                        LocalToView(npcRoute.waypoints[p], bounds), 2f, new Color(0.72f, 0.82f, 0.72f, 0.75f));
            }

            npcRouteLines.End();

            // Markers.
            IReadOnlyList<MapPoiDefinition> pois = Atlas.PoisOf(map.id);
            for (int i = 0; i < pois.Count; i++)
            {
                MapPoiDefinition poi = pois[i];
                Marker marker = localMarkers.Next(WorldMapIcons.ShapeForPoi(poi.kind), "Local_" + poi.id);
                marker.Rect.sizeDelta = new Vector2(11f, 11f);
                marker.Rect.anchoredPosition = LocalToView(poi.position, bounds);
                marker.Icon.color = WorldMapIcons.ColorForPoi(poi.kind);
                marker.Label.text = string.Empty;
            }

            // Player position inside the level, from the level's own bounds.
            GameObject player = ResolvePlayer();
            MapDefinition currentMap = Atlas.GetMap(Runtime != null ? Runtime.CurrentMapId : string.Empty);
            if (player != null && currentMap != null && string.Equals(currentMap.id, map.id, StringComparison.Ordinal))
            {
                Marker marker = localMarkers.Next(MapMarkerShape.Ring, "Local_Player");
                marker.Rect.sizeDelta = new Vector2(14f, 14f);
                marker.Rect.anchoredPosition = LocalToView(player.transform.position, bounds);
                marker.Icon.color = new Color(0.99f, 0.93f, 0.55f, 1f);
                marker.Label.text = string.Empty;
            }

            localLines.End();
            localMarkers.End();
        }

        /// <summary>
        /// Whether an NPC route may be drawn at all. Route lines are hidden by default: a route needs usable data
        /// (at least two waypoints) and a visibility policy that permits it, and "no data" is never treated as a
        /// route of length zero.
        /// </summary>
        public static bool ShouldDrawNpcRoute(NpcRouteDefinition route, WorldProgress progress)
        {
            if (route == null || !route.HasUsableRoute)
                return false;

            switch (route.visibility)
            {
                case NpcRouteVisibility.Hidden:
                    return false;
                case NpcRouteVisibility.ShownWhenDataExists:
                    return true;
                case NpcRouteVisibility.HiddenUntilKnown:
                    return route.knownByDefault || (progress != null && progress.IsNpcRouteKnown(route.id));
                default:
                    return false;
            }
        }

        private Vector2 LocalToView(Vector2 levelPoint, Rect bounds)
        {
            float width = localView != null ? localView.sizeDelta.x - 16f : 390f;
            float height = localView != null ? localView.sizeDelta.y - 16f : 134f;

            float u = bounds.width > 0.0001f ? Mathf.Clamp01((levelPoint.x - bounds.xMin) / bounds.width) : 0.5f;
            float v = bounds.height > 0.0001f ? Mathf.Clamp01((levelPoint.y - bounds.yMin) / bounds.height) : 0.5f;

            return new Vector2((u - 0.5f) * width, (v - 0.5f) * height);
        }

        // ================================================================== view maths

        /// <summary>Atlas space (0..1, y up) to pixel offset inside the content layer, measured from its centre.</summary>
        public Vector2 AtlasToContent(Vector2 atlasPosition) => (atlasPosition - new Vector2(0.5f, 0.5f)) * (AtlasBaseExtent * zoom);

        /// <summary>Content-space pixel offset back into atlas space.</summary>
        public Vector2 ContentToAtlas(Vector2 contentPosition) => contentPosition / (AtlasBaseExtent * zoom) + new Vector2(0.5f, 0.5f);

        /// <summary>A viewport-local point (0,0 at the viewport centre) to atlas space, used by zoom-to-cursor and
        /// by the verification that a given world point stays under the cursor.</summary>
        public Vector2 ViewportPointToAtlas(Vector2 viewportLocalPoint) => ContentToAtlas(viewportLocalPoint - pan);

        private void ApplyView()
        {
            if (content == null)
                return;

            content.sizeDelta = new Vector2(AtlasBaseExtent, AtlasBaseExtent) * zoom;
            content.anchoredPosition = pan;

            if (atlasBackdrop != null)
            {
                atlasBackdrop.rectTransform.sizeDelta = new Vector2(AtlasBaseExtent, AtlasBaseExtent) * zoom;
                atlasBackdrop.rectTransform.anchoredPosition = Vector2.zero;
            }

            RebuildAtlasLayer();
            RefreshRouteLayer();
            RefreshPlayerMarker();
        }

        /// <summary>Zoom around a point so the content under it stays put. Zoom is clamped, so a scroll cannot
        /// push the atlas past a readable scale.</summary>
        public void ZoomAtPoint(Vector2 viewportLocalPoint, float factor)
        {
            float previousZoom = zoom;
            float nextZoom = Mathf.Clamp(previousZoom * factor, MinZoom, MaxZoom);
            if (Mathf.Abs(nextZoom - previousZoom) < 1e-4f)
                return;

            Vector2 atlasUnderPoint = ViewportPointToAtlas(viewportLocalPoint);
            zoom = nextZoom;
            pan = viewportLocalPoint - AtlasToContent(atlasUnderPoint);
            ClampPan();
            ApplyView();
        }

        public void ZoomBy(float factor) => ZoomAtPoint(Vector2.zero, factor);

        private void ClampPan()
        {
            if (viewport == null)
                return;

            float contentSize = AtlasBaseExtent * zoom;
            float maxX = Mathf.Max(0f, (contentSize - viewport.sizeDelta.x) * 0.5f);
            float maxY = Mathf.Max(0f, (contentSize - viewport.sizeDelta.y) * 0.5f);
            pan = new Vector2(Mathf.Clamp(pan.x, -maxX, maxX), Mathf.Clamp(pan.y, -maxY, maxY));
        }

        public void PanBy(Vector2 delta)
        {
            pan += delta;
            ClampPan();
            ApplyView();
        }

        public void FitToAll()
        {
            zoom = 1f;
            pan = Vector2.zero;
            ApplyView();
        }

        public void ResetView() => FitToAll();

        public void CenterOnCurrentMap()
        {
            MapDefinition map = Atlas != null ? Atlas.GetMap(Runtime != null ? Runtime.CurrentMapId : string.Empty) : null;
            if (map == null)
                return;

            SelectMap(map.id);
        }

        public void SelectMap(string mapId)
        {
            selectedMapId = mapId ?? string.Empty;
            resultPage = 0;
            RebuildAtlasLayer();
            RefreshDetails();
            RefreshPlayerMarker();
        }

        private void CenterOn(string mapId)
        {
            MapDefinition map = Atlas != null ? Atlas.GetMap(mapId) : null;
            if (map == null || viewport == null)
                return;

            pan = -AtlasToContent(map.atlasPosition);
            ClampPan();
            ApplyView();
        }

        /// <summary>Selects a map, brings it into view and opens its details. Used by a result row click.</summary>
        public void FocusMap(string mapId)
        {
            SelectMap(mapId);
            CenterOn(mapId);
        }

        // ================================================================== search and filters

        private void HandleSearchEdited(string text)
        {
            builder.Text = text ?? string.Empty;
            resultPage = 0;
            RunQuery();
        }

        private void HandleDangerEdited(string text)
        {
            if (int.TryParse(text, out int value) && value > 0)
            {
                builder.Danger = new NumericComparison { op = NumericOp.LessOrEqual, value = value };
            }
            else
            {
                builder.Danger = default;
            }

            resultPage = 0;
            RunQuery();
        }

        public void RunQuery()
        {
            results = builder.Evaluate(Context);
            RefreshResults();
            RefreshFilterText();
            RefreshLegendHighlight();
            SetStatus($"filter: {builder.Describe()} | {results.ResultCountText}");
        }

        public void ClearFilters()
        {
            builder.Reset();
            if (searchField != null)
                searchField.SetTextWithoutNotify(string.Empty);
            if (dangerField != null)
                dangerField.SetTextWithoutNotify(string.Empty);

            for (int i = 0; i < facetToggles.Count; i++)
                facetToggles[i].SetIsOnWithoutNotify(false);

            resultPage = 0;
            RunQuery();
        }

        /// <summary>Sets a facet without going through the widget, so a verification can exercise the filter
        /// exactly as the checkbox would.</summary>
        public void SetFacet(MapFlag flag, bool value)
        {
            builder.RequiredFlags.Remove(flag);
            builder.ExcludedFlags.Remove(flag);
            if (value)
                builder.RequiredFlags.Add(flag);
            else
                builder.ExcludedFlags.Add(flag);

            RunQuery();
        }

        public void SetText(string text)
        {
            builder.Text = text ?? string.Empty;
            if (searchField != null)
                searchField.SetTextWithoutNotify(builder.Text);

            RunQuery();
        }

        public void SetBiomeTags(params string[] tags)
        {
            builder.BiomeTags.Clear();
            if (tags != null)
                builder.BiomeTags.AddRange(tags);

            RunQuery();
        }

        public void SetDanger(NumericComparison comparison)
        {
            builder.Danger = comparison;
            RunQuery();
        }

        public void SetWithinLocation(string locationId)
        {
            builder.WithinLocationId = locationId ?? string.Empty;
            RunQuery();
        }

        public void SetAdvancedGroup(FilterGroupOp op)
        {
            builder.Advanced = new AtlasFilterGroup(op);
            RunQuery();
        }

        public void AddAdvancedPredicate(AtlasPredicate predicate)
        {
            if (builder.Advanced == null)
                builder.Advanced = new AtlasFilterGroup(FilterGroupOp.And);

            builder.Advanced.Add(predicate);
            RunQuery();
        }

        private void SetFlag(MapFlag flag, bool on)
        {
            var flags = new List<MapFlag> { flag };
            SetFlags(flags, on);
        }

        private void SetFlagPair(MapFlag a, MapFlag b, bool on) => SetFlags(new List<MapFlag> { a, b }, on);

        private void SetFlags(List<MapFlag> flags, bool on)
        {
            for (int i = 0; i < flags.Count; i++)
            {
                builder.RequiredFlags.Remove(flags[i]);
                builder.ExcludedFlags.Remove(flags[i]);
                if (on)
                    builder.RequiredFlags.Add(flags[i]);
            }

            RunQuery();
        }

        private void SetAnyChest(bool on)
        {
            if (on)
            {
                // Any chest is expressed as "has a chest", not as a count, so a map with one common chest matches.
                builder.RequiredFlags.Remove(MapFlag.HasAnyChest);
                builder.RequiredFlags.Add(MapFlag.HasAnyChest);
            }
            else
            {
                builder.RequiredFlags.Remove(MapFlag.HasAnyChest);
            }

            RunQuery();
        }

        private void SetStory(bool on)
        {
            builder.StoryMarkersOnly = on;
            RunQuery();
        }

        private void SetVisited(bool on)
        {
            builder.VisitedOnly = on;
            RunQuery();
        }

        private void SetUndiscovered(bool on)
        {
            builder.UndiscoveredOnly = on;
            RunQuery();
        }

        private void SetReachable(bool on)
        {
            builder.RouteReachableOnly = on;
            RunQuery();
        }

        private void SetNpcRoute(bool on)
        {
            builder.NpcRouteDataOnly = on;
            RunQuery();
        }

        private void SetPending(bool on)
        {
            builder.PendingCompletionOnly = on;
            RunQuery();
        }

        private void AddAdvancedGroup(FilterGroupOp op)
        {
            // A group added from the UI starts with one obvious predicate so the nesting is visible immediately
            // instead of appearing to do nothing.
            var group = new AtlasFilterGroup(op);
            group.Add(new FlagPredicate(MapFlag.HasDungeon, op != FilterGroupOp.Not));

            builder.Advanced = group;
            RunQuery();
        }

        private void RefreshResults()
        {
            int pageCount = Mathf.Max(1, Mathf.CeilToInt(results.Count / (float)ResultRowsPerPage));
            resultPage = Mathf.Clamp(resultPage, 0, pageCount - 1);

            for (int i = 0; i < resultButtons.Count; i++)
            {
                int index = resultPage * ResultRowsPerPage + i;
                bool has = index < results.Count;
                Button row = resultButtons[i];
                row.gameObject.SetActive(has);

                if (!has)
                    continue;

                AtlasQueryResult result = results.Items[index];
                Text label = row.GetComponentInChildren<Text>();
                if (label != null)
                {
                    label.text = result.Name + "  —  " + result.Breadcrumb;
                    label.alignment = TextAnchor.MiddleLeft;
                    label.fontSize = 15;
                }
            }

            if (resultCountText != null)
                resultCountText.text = results.ResultCountText + (pageCount > 1 ? $"  (page {resultPage + 1}/{pageCount})" : string.Empty);

            if (pageText != null)
                pageText.text = $"{resultPage + 1} / {pageCount}";
        }

        private void ChangePage(int delta)
        {
            int pageCount = Mathf.Max(1, Mathf.CeilToInt(results.Count / (float)ResultRowsPerPage));
            resultPage = Mathf.Clamp(resultPage + delta, 0, pageCount - 1);
            RefreshResults();
        }

        private void FocusResultRow(int row)
        {
            int index = resultPage * ResultRowsPerPage + row;
            if (index < 0 || index >= results.Count)
                return;

            FocusMap(results.Items[index].MapId);
            PlanRouteTo(results.Items[index].MapId);
        }

        private void RefreshFilterText()
        {
            if (filterText == null)
                return;

            var sb = new StringBuilder();
            sb.Append("active: ").Append(builder.HasAnyFilter ? builder.Describe() : "none");
            if (builder.Advanced != null && !builder.Advanced.IsEmpty)
                sb.Append("\nadvanced: ").Append(builder.Advanced.Describe());

            filterText.text = sb.ToString();
        }

        private void RefreshLegendHighlight()
        {
            // Node colours already carry current/selected/visited; the legend text states the shapes, which is
            // what keeps the map readable without relying on colour.
        }

        private void RefreshDetails()
        {
            if (detailsText == null || Atlas == null)
                return;

            MapDefinition map = Atlas.GetMap(selectedMapId);
            if (map == null)
            {
                detailsText.text = "Select a map on the atlas.";
                return;
            }

            MapFacts facts = Context.Describe(map.id);
            var sb = new StringBuilder();
            sb.Append(map.displayName).Append("    [").Append(map.mapType).Append("]\n");
            sb.Append(facts.Breadcrumb).Append('\n');
            sb.Append("tags: ").Append(facts.Tags.Count == 0 ? "none recorded" : string.Join(", ", facts.Tags)).Append('\n');
            sb.Append("chests: ").Append(facts.Chests.IsEmpty ? "none recorded" : facts.Chests.ToString()).Append('\n');
            sb.Append("chest progress: ").Append(facts.ChestsOpened).Append(" opened, ")
              .Append(facts.ChestsDiscovered).Append(" discovered\n");
            sb.Append("danger: ").Append(facts.Danger.IsKnown ? facts.Danger.ToString() : "unknown / not recorded").Append('\n');
            sb.Append("recommended level: ").Append(map.recommendedLevel > 0 ? map.recommendedLevel.ToString() : "unknown").Append('\n');
            sb.Append("enemies: ").Append(facts.EnemySpecies.Count == 0 ? "unknown / not recorded" : string.Join(", ", facts.EnemySpecies)).Append('\n');
            sb.Append("completion: ").Append(facts.Completion).Append('\n');
            sb.Append("npc routes: ").Append(facts.HasNpcRouteData
                ? (facts.HasKnownNpcRoute ? "authored and known" : "authored, not yet known")
                : "none recorded").Append('\n');
            sb.Append("scene: ").Append(string.IsNullOrEmpty(map.scenePath) ? "not authored" : map.scenePath);

            detailsText.text = sb.ToString();
        }

        private void SetStatus(string text)
        {
            if (statusText != null)
                statusText.text = text;
        }

        // ================================================================== routes

        public void SetObjective(RouteObjective value)
        {
            objective = value;
            if (objectiveLabel != null)
                objectiveLabel.text = ObjectiveLabel(objective);

            if (selectedMapId.Length > 0)
                PlanRouteTo(selectedMapId);
        }

        private static string ObjectiveLabel(RouteObjective value)
        {
            switch (value)
            {
                case RouteObjective.Safest: return "OBJECTIVE: SAFEST";
                case RouteObjective.Fastest: return "OBJECTIVE: FASTEST";
                case RouteObjective.Custom: return "OBJECTIVE: CUSTOM";
                default: return "OBJECTIVE: SHORTEST";
            }
        }

        private void CycleObjective()
        {
            switch (objective)
            {
                case RouteObjective.ShortestDistance: SetObjective(RouteObjective.Safest); break;
                case RouteObjective.Safest: SetObjective(RouteObjective.Fastest); break;
                case RouteObjective.Fastest: SetObjective(RouteObjective.ShortestDistance); break;
                default: SetObjective(RouteObjective.ShortestDistance); break;
            }
        }

        /// <summary>
        /// Plans a route to a destination and selects it. Planning uses the same call the travel service uses, so
        /// what the map offers to travel through is what travel will accept.
        /// </summary>
        public RoutePlan PlanRouteTo(string mapId)
        {
            destinationMapId = mapId ?? string.Empty;
            selectedMapId = string.IsNullOrEmpty(selectedMapId) ? destinationMapId : selectedMapId;

            if (Atlas == null || Runtime == null || string.IsNullOrEmpty(destinationMapId))
            {
                route = null;
                if (routeText != null)
                    routeText.text = "no destination selected";

                return null;
            }

            var options = new RouteOptions
            {
                Objective = objective,
                // The heuristic is requested but the planner keeps it only if it proves to be a lower bound for
                // this data; otherwise the plan reports that plain Dijkstra was used.
                Heuristic = RouteHeuristic.AtlasDistanceLowerBound,
                RespectAvailability = true
            };

            // Route from where the player actually stands, not from the port they first arrived at.
            GameObject player = ResolvePlayer();
            if (player != null && Atlas.GetMap(Runtime.CurrentMapId) != null)
            {
                options.HasStartPosition = true;
                options.StartPosition = player.transform.position;
            }

            route = RoutePlanner.Plan(Atlas, Progress, Runtime.CurrentMapId, destinationMapId, options);

            if (routeText != null)
            {
                var sb = new StringBuilder();
                sb.Append(route.Found ? "ROUTE " : "NO ROUTE ");
                sb.Append(route.Objective).Append(": ").Append(route.Found ? route.CostText() : route.FailureReason);
                if (route.Found)
                {
                    sb.Append('\n').Append(route.MapSequenceText);
                    sb.Append("\nheuristic: ").Append(route.HeuristicUsed);
                    if (!string.IsNullOrEmpty(route.HeuristicNote))
                        sb.Append(" (").Append(route.HeuristicNote).Append(')');

                    if (route.IsSchematic)
                        sb.Append("\ncontains a schematic link with no authored geometry");
                }

                routeText.text = sb.ToString();
            }

            RefreshRouteLayer();
            RefreshDetails();
            return route;
        }

        /// <summary>Travels the destination by walking the planned route's own connections in order.</summary>
        public void TravelThroughRoute()
        {
            if (route == null || !route.Found || Runtime == null)
            {
                SetStatus("no route to travel");
                return;
            }

            for (int i = 0; i < route.Legs.Count; i++)
            {
                RouteLeg leg = route.Legs[i];
                if (!leg.IsConnection)
                    continue;

                MapTravelResult result = Runtime.Transition.RequestTravel(leg.ConnectionId);
                SetStatus(result.Started
                    ? $"travelling through '{leg.ConnectionId}' to {result.ToMapId}"
                    : $"travel refused for '{leg.ConnectionId}': {result.Message}");
                return;
            }

            SetStatus("this map needs no inter-map travel");
        }

        // ================================================================== visibility

        public void Toggle() => SetOpen(!IsOpen);

        public void Open() => SetOpen(true);

        public void Close() => SetOpen(false);

        /// <summary>
        /// Escape is answered first by the map when it is open, because it is a full-screen modal: closing the
        /// map must never also toggle pause.
        /// </summary>
        public bool HandleEscapePress()
        {
            if (!IsOpen)
                return false;

            SetOpen(false);
            return true;
        }

        public void SetOpen(bool open)
        {
            if (!built)
                return;

            IsOpen = open;

            if (canvas != null)
                canvas.gameObject.SetActive(open);

            if (open)
            {
                if (searchField != null)
                    searchField.SetTextWithoutNotify(builder.Text);

                RefreshPlayerMarker();
                RefreshRouteLayer();
                RefreshDetails();
                SetStatus($"current map: {(Atlas != null && MapEntryIsKnown() ? Atlas.GetMap(Runtime.CurrentMapId).displayName : "unknown")}");

                if (Runtime != null && Runtime.LastLoadResult.Outcome != WorldSaveLoadOutcome.Loaded && Runtime.LastLoadResult.Outcome != WorldSaveLoadOutcome.FreshStart)
                    SetStatus(statusText.text + " | save: " + Runtime.LastLoadResult.Outcome);
            }

            ApplyInputLock(open);
        }

        private bool MapEntryIsKnown()
        {
            return Runtime != null && Atlas != null && Atlas.GetMap(Runtime.CurrentMapId) != null;
        }

        /// <summary>
        /// Borrows the same gameplay input gate the bag uses. The map is opened from gameplay, never from the
        /// pause menu, and it never pauses the world, so the gate it restores is the one that was in force before
        /// it opened.
        /// </summary>
        private void ApplyInputLock(bool open)
        {
            PlayerInputReader input = FindAnyObjectByType<PlayerInputReader>();
            if (input == null)
                return;

            if (open)
            {
                if (inputLockedByMap)
                    return;

                inputWasEnabledBeforeLock = input.Enabled;
                inputLockedByMap = true;
                input.Enabled = false;
                return;
            }

            if (!inputLockedByMap)
                return;

            inputLockedByMap = false;
            bool paused = GameManager.Instance != null && GameManager.Instance.IsPaused;
            bool bagOpen = false;
            var inventory = FindAnyObjectByType<DarkRealms.Inventory.InventoryUIController>();
            if (inventory != null)
                bagOpen = inventory.IsOpen;

            input.Enabled = inputWasEnabledBeforeLock && !paused && !bagOpen;
        }

        private void Update()
        {
            if (!built)
                return;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null && keyboard.mKey.wasPressedThisFrame)
            {
                // Opening the map while the world is paused would fight the pause menu for Escape and for the
                // input gate, so it is simply not allowed.
                bool paused = GameManager.Instance != null && GameManager.Instance.IsPaused;
                if (!paused)
                    Toggle();
            }

            if (!IsOpen)
                return;

            HandleOpenInput(keyboard);
        }

        private void HandleOpenInput(Keyboard keyboard)
        {
            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                float scroll = mouse.scroll.ReadValue().y;
                if (Mathf.Abs(scroll) > 0.01f && viewport != null)
                {
                    Vector2 local = ScreenToViewportLocal(mouse.position.ReadValue());
                    ZoomAtPoint(local, scroll > 0f ? 1.12f : 1f / 1.12f);
                }
            }

            float horizontal = 0f, vertical = 0f;
            if (keyboard != null)
            {
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed) horizontal -= 1f;
                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed) horizontal += 1f;
                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed) vertical -= 1f;
                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed) vertical += 1f;

                if (keyboard.equalsKey.wasPressedThisFrame) ZoomBy(1.12f);
                if (keyboard.minusKey.wasPressedThisFrame) ZoomBy(1f / 1.12f);
                if (keyboard.fKey.wasPressedThisFrame) FitToAll();
                if (keyboard.enterKey.wasPressedThisFrame || keyboard.numpadEnterKey.wasPressedThisFrame)
                    PlanRouteTo(selectedMapId);
            }

            Gamepad pad = Gamepad.current;
            if (pad != null)
            {
                horizontal += pad.leftStick.ReadValue().x;
                vertical += pad.leftStick.ReadValue().y;

                if (pad.rightShoulder.wasPressedThisFrame) ZoomBy(1.12f);
                if (pad.leftShoulder.wasPressedThisFrame) ZoomBy(1f / 1.12f);
            }

            if (Mathf.Abs(horizontal) > 0.1f || Mathf.Abs(vertical) > 0.1f)
                PanBy(new Vector2(horizontal, vertical) * (600f * Time.unscaledDeltaTime));
        }

        /// <summary>Screen point to a point measured from the viewport centre, which is the space pan and zoom
        /// are expressed in.</summary>
        public Vector2 ScreenToViewportLocal(Vector2 screenPoint)
        {
            if (viewport == null)
                return Vector2.zero;

            Vector2 centre = RectTransformUtility.WorldToScreenPoint(null, viewport.position);
            return screenPoint - centre;
        }

        private void OnDestroy()
        {
            nodeMarkers?.DestroyAll();
            poiMarkers?.DestroyAll();
            localMarkers?.DestroyAll();
            connectorLines?.DestroyAll();
            routeLines?.DestroyAll();
            localLines?.DestroyAll();
            npcRouteLines?.DestroyAll();
        }
    }
}

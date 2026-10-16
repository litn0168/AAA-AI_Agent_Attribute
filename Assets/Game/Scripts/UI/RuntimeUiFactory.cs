using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

namespace DarkRealms.UI
{
    /// <summary>
    /// Helpers for interface elements that are built at runtime rather than authored in the scene, which is the
    /// style the inventory module established (its window must not depend on scene layout). Centralising the
    /// pieces that are easy to get wrong leaves one implementation of each instead of one per panel.
    /// </summary>
    public static class RuntimeUiFactory
    {
        private static Font cachedFont;

        /// <summary>
        /// Unity 6 removed the "Arial.ttf" built-in font: asking for it now THROWS instead of returning null,
        /// which used to abort a whole interface build after the first text element. The current name is
        /// preferred and the older name only serves as a fallback, both resolved once and cached.
        /// </summary>
        public static Font BuiltinFont
        {
            get
            {
                if (cachedFont != null)
                    return cachedFont;

                try { cachedFont = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); }
                catch { cachedFont = null; }

                if (cachedFont == null)
                {
                    try { cachedFont = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
                    catch { cachedFont = null; }
                }

                return cachedFont;
            }
        }

        public static RectTransform CreateOverlayCanvas(string name, Transform parent, int sortingOrder)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            go.transform.SetParent(parent, false);

            Canvas canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortingOrder;

            CanvasScaler scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            return go.GetComponent<RectTransform>();
        }

        /// <summary>Adds the one EventSystem the interface needs, reusing an existing one when present.</summary>
        public static EventSystem EnsureEventSystem(Transform parent, string name)
        {
            EventSystem existing = Object.FindAnyObjectByType<EventSystem>();
            if (existing != null)
                return existing;

            GameObject go = new GameObject(name, typeof(EventSystem), typeof(InputSystemUIInputModule));
            go.transform.SetParent(parent, false);
            return go.GetComponent<EventSystem>();
        }

        public static GameObject Panel(string name, Transform parent, Vector2 size, Vector2 position, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            Center(rect, size, position);
            Image image = go.GetComponent<Image>();
            image.color = color;
            return go;
        }

        public static Image Image(string name, Transform parent, Vector2 size, Vector2 position, Color color, bool raycastTarget = false)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            Center(rect, size, position);
            Image image = go.GetComponent<Image>();
            image.color = color;
            image.raycastTarget = raycastTarget;
            return image;
        }

        public static Text Text(string name, Transform parent, string value, int fontSize, TextAnchor alignment,
            Vector2 size, Vector2 position, Color color)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            Center(rect, size, position);
            Text text = go.GetComponent<Text>();
            text.text = value;
            text.font = BuiltinFont;
            text.fontSize = fontSize;
            text.alignment = alignment;
            text.color = color;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.supportRichText = false;
            text.raycastTarget = false;
            return text;
        }

        public static Button Button(string name, Transform parent, string label, int fontSize, Vector2 size, Vector2 position,
            Color background, Color labelColor)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(Button));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            Center(rect, size, position);

            Image image = go.GetComponent<Image>();
            image.color = background;

            Button button = go.GetComponent<Button>();
            button.targetGraphic = image;
            button.transition = Selectable.Transition.ColorTint;

            Text text = Text("Label", go.transform, label, fontSize, TextAnchor.MiddleCenter, size, Vector2.zero, labelColor);
            text.raycastTarget = false;
            return button;
        }

        /// <summary>Builds a checkbox plus label. Returns the Toggle; the label text is exposed through the out parameter.</summary>
        public static Toggle Toggle(string name, Transform parent, string label, int fontSize, Vector2 size, Vector2 position,
            out Text labelText)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Toggle));
            go.transform.SetParent(parent, false);
            Center(go.GetComponent<RectTransform>(), size, position);

            Image box = Image("Box", go.transform, new Vector2(34f, 34f), new Vector2(-(size.x * 0.5f) + 26f, 0f),
                new Color(0.12f, 0.13f, 0.16f, 1f), true);
            Image check = Image("Checkmark", box.transform, new Vector2(20f, 20f), Vector2.zero,
                new Color(0.62f, 0.79f, 0.42f, 1f), false);

            labelText = Text("Label", go.transform, label, fontSize, TextAnchor.MiddleLeft,
                new Vector2(size.x - 52f, size.y), new Vector2(26f, 0f), new Color(0.92f, 0.93f, 0.96f, 1f));

            Toggle toggle = go.GetComponent<Toggle>();
            toggle.targetGraphic = box;
            toggle.graphic = check;
            toggle.transition = Selectable.Transition.None;
            return toggle;
        }

        public static InputField InputField(string name, Transform parent, string placeholder, int fontSize, Vector2 size,
            Vector2 position, InputField.ContentType contentType, bool centered)
        {
            GameObject go = new GameObject(name, typeof(RectTransform), typeof(Image), typeof(InputField));
            go.transform.SetParent(parent, false);
            RectTransform rect = go.GetComponent<RectTransform>();
            Center(rect, size, position);

            Image background = go.GetComponent<Image>();
            background.color = new Color(0.10f, 0.11f, 0.14f, 1f);

            Text text = Text("Text", go.transform, string.Empty, fontSize,
                centered ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft, new Vector2(size.x - 20f, size.y - 8f), Vector2.zero,
                new Color(0.95f, 0.95f, 0.98f, 1f));
            Text hint = Text("Placeholder", go.transform, placeholder, fontSize,
                centered ? TextAnchor.MiddleCenter : TextAnchor.MiddleLeft, new Vector2(size.x - 20f, size.y - 8f), Vector2.zero,
                new Color(0.50f, 0.53f, 0.58f, 1f));

            InputField field = go.GetComponent<InputField>();
            field.targetGraphic = background;
            field.textComponent = text;
            field.placeholder = hint;
            field.contentType = contentType;
            // Fully qualified on purpose: inside this method the simple name 'InputField' binds to the method
            // itself, not to UnityEngine.UI.InputField, so the unqualified enum names do not compile.
            field.lineType = UnityEngine.UI.InputField.LineType.SingleLine;
            field.characterLimit = contentType == UnityEngine.UI.InputField.ContentType.IntegerNumber ? 4 : 24;
            field.customCaretColor = true;
            field.caretColor = Color.white;
            field.selectionColor = new Color(0.35f, 0.45f, 0.65f, 0.6f);
            return field;
        }

        public static void StretchFull(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
            rect.anchoredPosition = Vector2.zero;
            rect.sizeDelta = Vector2.zero;
        }

        public static void Center(RectTransform rect, Vector2 size, Vector2 position)
        {
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = size;
            rect.anchoredPosition = position;
        }
    }
}

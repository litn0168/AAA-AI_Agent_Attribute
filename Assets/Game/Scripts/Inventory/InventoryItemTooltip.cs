using System;
using System.Collections.Generic;
using System.Text;
using DarkRealms.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DarkRealms.Inventory
{
    /// <summary>
    /// Hover tooltip for one inventory stack. It only ever reads the model: every number it prints comes from
    /// the item asset (or from the weapon profile the item points at), so it cannot show a stat the game does
    /// not actually have.
    ///
    /// The comparison block is deliberately conditional. In this project only weapons carry numbers
    /// (<see cref="DarkRealms.Combat.WeaponDefinition"/>); armour, jewellery and accessories have no stat
    /// fields at all. Rather than invent statistics to fill the box, the block lists only the rows that exist on
    /// both the hovered item and the item currently worn in the slot it would occupy, and adds a short note when
    /// there is nothing to compare.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class InventoryItemTooltip : MonoBehaviour
    {
        private const float Width = 480f;
        private const float Height = 340f;
        private const float CursorOffset = 18f;

        private static readonly Color PanelColor = new Color(0.028f, 0.032f, 0.042f, 0.975f);
        private static readonly Color TitleColor = new Color(0.91f, 0.82f, 0.61f, 1f);
        private static readonly Color MutedColor = new Color(0.64f, 0.67f, 0.72f, 1f);
        private static readonly Color BodyColor = new Color(0.86f, 0.88f, 0.91f, 1f);
        private static readonly Color BetterColor = new Color(0.55f, 0.84f, 0.45f, 1f);
        private static readonly Color WorseColor = new Color(0.90f, 0.47f, 0.41f, 1f);

        private Canvas canvas;
        private InventoryModel model;
        private RectTransform rect;
        private Text titleText;
        private Text subtitleText;
        private Text bodyText;
        private Text statsText;
        private Text compareText;
        private Text footerText;
        private Image iconImage;
        private Image iconFrame;

        /// <summary>True while the tooltip is on screen.</summary>
        public bool IsVisible { get; private set; }

        /// <summary>Item the visible tooltip describes; null while hidden.</summary>
        public InventoryItemDefinition CurrentItem { get; private set; }

        /// <summary>Slot the visible tooltip is comparing against, when one was found.</summary>
        public EquipmentSlotId? CurrentComparisonSlot { get; private set; }

        /// <summary>Number of comparison rows the visible tooltip is showing.</summary>
        public int CurrentComparisonRowCount { get; private set; }

        public void Initialize(Canvas hostCanvas, InventoryModel inventoryModel)
        {
            canvas = hostCanvas;
            model = inventoryModel;
            Build();
        }

        private void Build()
        {
            if (rect != null || canvas == null)
                return;

            GameObject root = RuntimeUiFactory.Panel("Item Tooltip", canvas.transform, new Vector2(Width, Height),
                Vector2.zero, PanelColor);
            rect = root.GetComponent<RectTransform>();
            rect.pivot = new Vector2(0f, 1f);           // grow down-right from the cursor
            // The tooltip must never swallow pointer events, or showing it would take hover away from the slot
            // that requested it and the tooltip would flicker in and out.
            root.GetComponent<Image>().raycastTarget = false;
            root.SetActive(false);

            iconFrame = RuntimeUiFactory.Image("Icon Frame", root.transform, new Vector2(66f, 66f),
                new Vector2(0f, 0f), new Color(0.10f, 0.11f, 0.14f, 1f), false);
            iconImage = RuntimeUiFactory.Image("Icon", iconFrame.transform, new Vector2(58f, 58f), Vector2.zero,
                Color.white, false);
            iconImage.preserveAspect = true;

            titleText = RuntimeUiFactory.Text("Title", root.transform, string.Empty, 23, TextAnchor.MiddleLeft,
                new Vector2(330f, 30f), new Vector2(0f, 0f), TitleColor);
            subtitleText = RuntimeUiFactory.Text("Subtitle", root.transform, string.Empty, 16, TextAnchor.UpperLeft,
                new Vector2(330f, 44f), new Vector2(0f, 0f), MutedColor);
            bodyText = RuntimeUiFactory.Text("Body", root.transform, string.Empty, 16, TextAnchor.UpperLeft,
                new Vector2(432f, 70f), new Vector2(0f, 0f), BodyColor);
            statsText = RuntimeUiFactory.Text("Stats", root.transform, string.Empty, 16, TextAnchor.UpperLeft,
                new Vector2(432f, 74f), new Vector2(0f, 0f), BodyColor);
            compareText = RuntimeUiFactory.Text("Compare", root.transform, string.Empty, 16, TextAnchor.UpperLeft,
                new Vector2(432f, 76f), new Vector2(0f, 0f), BodyColor);
            footerText = RuntimeUiFactory.Text("Footer", root.transform, string.Empty, 14, TextAnchor.LowerLeft,
                new Vector2(432f, 22f), new Vector2(0f, 0f), MutedColor);

            // The factory leaves rich text off by default; the stat and comparison blocks need it for the
            // better/worse colouring of each delta.
            bodyText.supportRichText = true;
            statsText.supportRichText = true;
            compareText.supportRichText = true;

            LayoutParts();
        }

        private void SetAnchored(RectTransform target, Vector2 topLeftOffset)
        {
            RectTransform parent = rect;
            float x = -parent.rect.width * 0.5f + topLeftOffset.x;
            float y = parent.rect.height * 0.5f - topLeftOffset.y;
            RectTransform part = target;
            part.anchorMin = new Vector2(0f, 1f);
            part.anchorMax = new Vector2(0f, 1f);
            part.pivot = new Vector2(0f, 1f);
            part.anchoredPosition = new Vector2(x, y);
        }

        private void LayoutParts()
        {
            SetAnchored(iconFrame.rectTransform, new Vector2(24f, 22f));
            SetAnchored(titleText.rectTransform, new Vector2(104f, 22f));
            SetAnchored(subtitleText.rectTransform, new Vector2(104f, 56f));
            SetAnchored(bodyText.rectTransform, new Vector2(24f, 104f));
            SetAnchored(statsText.rectTransform, new Vector2(24f, 178f));
            SetAnchored(compareText.rectTransform, new Vector2(24f, 252f));
            SetAnchored(footerText.rectTransform, new Vector2(24f, 312f));
        }

        /// <summary>Fills the tooltip and shows it next to <paramref name="anchorScreenPosition"/>.</summary>
        public void Show(InventoryItemDefinition item, int count, bool isActiveEquipment,
            EquipmentSlotId? hoveredEquipmentSlot, Vector2 anchorScreenPosition)
        {
            if (item == null || canvas == null)
            {
                Hide();
                return;
            }

            Build();

            CurrentItem = item;
            IsVisible = true;
            rect.gameObject.SetActive(true);
            rect.SetAsLastSibling();

            bool hasIcon = item.icon != null;
            iconImage.sprite = item.icon;
            iconImage.enabled = hasIcon;
            iconFrame.enabled = true;

            titleText.text = item.displayName;
            titleText.color = TitleColor;

            subtitleText.text = BuildSubtitle(item, count, isActiveEquipment);

            bodyText.text = string.IsNullOrWhiteSpace(item.description)
                ? "(không có mô tả)"
                : item.description;
            bodyText.color = string.IsNullOrWhiteSpace(item.description) ? MutedColor : BodyColor;

            var stats = CollectStats(item);
            statsText.text = BuildStatsBlock(stats);

            var comparison = BuildComparison(item, stats, hoveredEquipmentSlot);
            compareText.text = comparison.Text;
            CurrentComparisonRowCount = comparison.Rows;
            CurrentComparisonSlot = comparison.Slot;

            footerText.text = BuildFooter(item);

            PlaceNextTo(anchorScreenPosition);
        }

        public void Hide()
        {
            IsVisible = false;
            CurrentItem = null;
            CurrentComparisonSlot = null;
            CurrentComparisonRowCount = 0;

            if (rect != null)
                rect.gameObject.SetActive(false);
        }

        /// <summary>Replaces the canvas the tooltip is parented to, so it survives a canvas being rebuilt.</summary>
        public void SetCanvas(Canvas hostCanvas)
        {
            if (hostCanvas == null || hostCanvas == canvas)
                return;

            if (rect != null)
            {
                Destroy(rect.gameObject);
                rect = null;
            }

            canvas = hostCanvas;
            Build();
        }

        private static string BuildSubtitle(InventoryItemDefinition item, int count, bool isActiveEquipment)
        {
            var sb = new StringBuilder();
            sb.Append(DescribeCategory(item.category));

            if (item.IsEquipment)
            {
                sb.Append("  ·  vật phẩm trang bị");
                if (isActiveEquipment)
                    sb.Append("  ·  ĐANG DÙNG");
            }
            else if (item.MaxStack > 1)
            {
                sb.Append("  ·  x").Append(count).Append(" / ").Append(item.MaxStack);
            }

            return sb.ToString();
        }

        private static string BuildFooter(InventoryItemDefinition item)
        {
            if (item.category == InventoryItemCategory.Weapon && item.weapon == null)
                return "Chưa gán WeaponDefinition: trang bị vào sẽ dùng đòn tay không.";

            if (!item.IsEquipment)
                return item.MaxStack > 1 ? "Chuột phải: tách stack · Kéo-thả: di chuyển" : "Kéo-thả: di chuyển";

            return "Double-click / Shift-click: trang bị nhanh · Chuột phải: menu";
        }

        private static string DescribeCategory(InventoryItemCategory category)
        {
            switch (category)
            {
                case InventoryItemCategory.Weapon: return "Vũ khí";
                case InventoryItemCategory.Helmet: return "Mũ";
                case InventoryItemCategory.Chestplate: return "Giáp thân";
                case InventoryItemCategory.Legging: return "Giáp chân";
                case InventoryItemCategory.Boots: return "Giày";
                case InventoryItemCategory.Earring: return "Bông tai";
                case InventoryItemCategory.Necklace: return "Dây chuyền";
                case InventoryItemCategory.Ring: return "Nhẫn";
                case InventoryItemCategory.Bracelet: return "Vòng tay";
                case InventoryItemCategory.Amulet: return "Bùa";
                case InventoryItemCategory.Accessory: return "Phụ kiện";
                case InventoryItemCategory.Consumable: return "Tiêu hao";
                case InventoryItemCategory.Material: return "Nguyên liệu";
                case InventoryItemCategory.Quest: return "Nhiệm vụ";
                default: return category.ToString();
            }
        }

        /// <summary>One comparable number taken from the item asset itself.</summary>
        private readonly struct StatRow
        {
            public readonly string Label;
            public readonly float Value;
            public readonly string Format;
            public readonly string Suffix;
            public readonly bool HigherIsBetter;

            public StatRow(string label, float value, string format, string suffix, bool higherIsBetter)
            {
                Label = label;
                Value = value;
                Format = format;
                Suffix = suffix;
                HigherIsBetter = higherIsBetter;
            }

            public string Formatted => Value.ToString(Format) + Suffix;
        }

        /// <summary>
        /// The numbers this item actually exposes. Only Weapon items with a bound WeaponDefinition have any; the
        /// list stays empty for everything else instead of repeating a design document that does not exist yet.
        /// </summary>
        private static List<StatRow> CollectStats(InventoryItemDefinition item)
        {
            var rows = new List<StatRow>();
            if (item == null || item.category != InventoryItemCategory.Weapon || item.weapon == null)
                return rows;

            var attack = item.weapon.attack;
            rows.Add(new StatRow("Sát thương", attack.damage, "0.##", string.Empty, true));
            rows.Add(new StatRow("Đẩy lùi", attack.knockbackForce, "0.##", string.Empty, true));
            rows.Add(new StatRow("Choáng", attack.stunTime, "0.###", "s", true));
            rows.Add(new StatRow("Thời gian đánh", attack.attackDuration, "0.###", "s", false));
            rows.Add(new StatRow("Hồi chiêu", attack.cooldown, "0.###", "s", false));
            return rows;
        }

        private static string BuildStatsBlock(List<StatRow> stats)
        {
            if (stats.Count == 0)
                return string.Empty;

            var sb = new StringBuilder();
            sb.Append("<b>Chỉ số</b>\n");
            for (int i = 0; i < stats.Count; i++)
            {
                sb.Append(stats[i].Label).Append(": ").Append(stats[i].Formatted);
                if (i < stats.Count - 1)
                    sb.Append('\n');
            }
            return sb.ToString();
        }

        private struct ComparisonResult
        {
            public string Text;
            public int Rows;
            public EquipmentSlotId? Slot;
        }

        private ComparisonResult BuildComparison(InventoryItemDefinition item, List<StatRow> stats,
            EquipmentSlotId? hoveredEquipmentSlot)
        {
            var result = new ComparisonResult { Text = string.Empty, Rows = 0, Slot = null };

            if (model == null || stats.Count == 0)
            {
                if (item.IsEquipment && stats.Count == 0)
                    result.Text = "<b>So sánh</b>\nChưa có chỉ số trên vật phẩm loại này.";
                return result;
            }

            EquipmentSlotId? slot = FindComparisonSlot(item, hoveredEquipmentSlot);
            if (slot == null)
            {
                result.Text = "<b>So sánh</b>\nChưa trang bị món tương ứng.";
                return result;
            }

            InventoryStack equipped = model.GetEquipmentSlot(slot.Value);
            if (equipped == null || equipped.IsEmpty)
            {
                result.Text = "<b>So sánh</b>\n" + InventoryModel.GetSlotLabel(slot.Value) + " đang trống.";
                result.Slot = slot;
                return result;
            }

            var equippedStats = CollectStats(equipped.item);
            if (equippedStats.Count == 0)
            {
                result.Text = "<b>So sánh</b>\nĐang dùng " + equipped.item.displayName +
                              " (không có chỉ số để so sánh).";
                result.Slot = slot;
                return result;
            }

            var sb = new StringBuilder();
            sb.Append("<b>So sánh</b> với ").Append(equipped.item.displayName)
              .Append(" (").Append(InventoryModel.GetSlotLabel(slot.Value)).Append(")\n");

            int rows = 0;
            foreach (StatRow candidate in stats)
            {
                StatRow? worn = FindByLabel(equippedStats, candidate.Label);
                if (worn == null)
                    continue;

                float delta = candidate.Value - worn.Value.Value;
                string mark = Mathf.Approximately(delta, 0f)
                    ? "±0"
                    : (delta > 0f ? "+" : "-") + Mathf.Abs(delta).ToString(candidate.Format);

                Color color = Mathf.Approximately(delta, 0f)
                    ? MutedColor
                    : (delta > 0f) == candidate.HigherIsBetter ? BetterColor : WorseColor;

                string hex = ColorUtility.ToHtmlStringRGB(color);
                sb.Append(candidate.Label).Append(": ").Append(candidate.Formatted)
                  .Append("  <color=#").Append(hex).Append(">").Append(mark).Append("</color>");
                if (rows < stats.Count - 1)
                    sb.Append('\n');
                rows++;
            }

            if (rows == 0)
            {
                result.Text = "<b>So sánh</b>\nKhông có chỉ số chung để so sánh.";
                result.Slot = slot;
                return result;
            }

            result.Text = sb.ToString();
            result.Rows = rows;
            result.Slot = slot;
            return result;
        }

        private EquipmentSlotId? FindComparisonSlot(InventoryItemDefinition item, EquipmentSlotId? hoveredEquipmentSlot)
        {
            if (hoveredEquipmentSlot.HasValue && InventoryModel.CanEquip(item, hoveredEquipmentSlot.Value))
                return hoveredEquipmentSlot;

            EquipmentSlotId? occupied = null;
            foreach (EquipmentSlotId slot in Enum.GetValues(typeof(EquipmentSlotId)))
            {
                if (!InventoryModel.CanEquip(item, slot))
                    continue;

                InventoryStack stack = model.GetEquipmentSlot(slot);
                if (stack == null || stack.IsEmpty)
                    continue;

                occupied = slot;
                break;
            }

            return occupied;
        }

        private static StatRow? FindByLabel(List<StatRow> rows, string label)
        {
            foreach (StatRow row in rows)
                if (row.Label == label)
                    return row;
            return null;
        }

        private void PlaceNextTo(Vector2 screenPosition)
        {
            RectTransform canvasRect = canvas.transform as RectTransform;
            if (canvasRect == null)
                return;

            // Overlay canvases ignore the camera, so a null event camera is correct here.
            if (!RectTransformUtility.ScreenPointToLocalPointInRectangle(canvasRect, screenPosition, null, out Vector2 local))
                return;

            Rect canvasBounds = canvasRect.rect;

            // Pivot is top-left, so the box hangs below-right of the cursor and can be clamped against the
            // canvas edges. The Min/Max guards keep a canvas smaller than the tooltip from inverting the range.
            float lowX = canvasBounds.xMin + CursorOffset;
            float highX = Mathf.Max(lowX, canvasBounds.xMax - CursorOffset - rect.rect.width);
            float lowY = canvasBounds.yMin + CursorOffset + rect.rect.height;
            float highY = Mathf.Max(lowY, canvasBounds.yMax - CursorOffset);

            float x = Mathf.Clamp(local.x + CursorOffset, lowX, highX);
            float y = Mathf.Clamp(local.y - CursorOffset, lowY, highY);

            rect.anchoredPosition = new Vector2(x, y);
        }
    }
}

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DarkRealms.Core;
using DarkRealms.Inventory;
using DarkRealms.UI;
using UnityEditor;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace DarkRealms.EditorTools
{
public static class InventoryUiVerification
{
    private const string ReportPath = @"D:\Unity Project\My project\InventoryPlayMode_F.txt";
    private static readonly List<string> Out = new List<string>();
    private static int pass;
    private static int fail;
    private static readonly List<RaycastResult> Hits = new List<RaycastResult>();
    private static EventSystem events;

    private static void Log(string s)
    {
        Out.Add(s);
        Debug.Log("[PMF] " + s);
    }

    private static void Check(bool ok, string label, string detail)
    {
        if (ok) { pass++; Log("  [PASS] " + label + " :: " + detail); }
        else { fail++; Log("  [FAIL] " + label + " :: " + detail); }
    }

    private static string F(float v) => v.ToString("0.###");

    private static void Step(int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            if (EditorApplication.isPaused) EditorApplication.isPaused = false;
            EditorApplication.QueuePlayerLoopUpdate();
            EditorApplication.Step();
        }
    }

    private static void Flush() => File.WriteAllText(ReportPath, string.Join("\n", Out) + "\n");

    // ---------------------------------------------------------------- event plumbing

    private static Vector2 ScreenPointOf(Transform t)
    {
        var rect = t as RectTransform;
        if (rect == null) return Vector2.zero;
        return RectTransformUtility.WorldToScreenPoint(null, rect.position);
    }

    private static PointerEventData MakePointer(Vector2 position, PointerEventData.InputButton button = PointerEventData.InputButton.Left)
    {
        var data = new PointerEventData(events)
        {
            position = position,
            pressPosition = position,
            delta = Vector2.zero,
            button = button,
            clickCount = 1,
            pointerId = -1,
            pointerEnter = null
        };

        Hits.Clear();
        events.RaycastAll(data, Hits);
        data.pointerCurrentRaycast = Hits.Count > 0 ? Hits[0] : default(RaycastResult);
        return data;
    }

    /// <summary>Topmost InventorySlotView under a screen point, using the same raycast the input module uses.</summary>
    private static InventorySlotView SlotUnder(Vector2 position)
    {
        var data = MakePointer(position);
        Hits.Clear();
        events.RaycastAll(data, Hits);
        foreach (RaycastResult hit in Hits)
        {
            if (hit.gameObject == null) continue;
            var slot = hit.gameObject.GetComponentInParent<DarkRealms.Inventory.InventorySlotView>();
            if (slot != null) return slot;
        }
        return null;
    }

    private static void Click(Transform target, PointerEventData.InputButton button = PointerEventData.InputButton.Left)
    {
        var data = MakePointer(ScreenPointOf(target), button);
        ExecuteEvents.Execute(target.gameObject, data, ExecuteEvents.pointerClickHandler);
        ExecuteEvents.Execute(target.gameObject, data, ExecuteEvents.pointerDownHandler);
        ExecuteEvents.Execute(target.gameObject, data, ExecuteEvents.pointerUpHandler);
        Step(2);
    }

    private static void ClickObject(GameObject target, PointerEventData.InputButton button = PointerEventData.InputButton.Left)
    {
        var data = MakePointer(ScreenPointOf(target.transform), button);
        ExecuteEvents.Execute(target, data, ExecuteEvents.pointerClickHandler);
        Step(2);
    }

    private static void BeginDrag(DarkRealms.Inventory.InventorySlotView from, Vector2 at)
    {
        var data = MakePointer(at);
        ExecuteEvents.Execute(from.gameObject, data, ExecuteEvents.initializePotentialDrag);
        ExecuteEvents.Execute(from.gameObject, data, ExecuteEvents.beginDragHandler);
        Step(1);
    }

    private static void DragOver(DarkRealms.Inventory.InventorySlotView from, Vector2 at)
    {
        ExecuteEvents.Execute(from.gameObject, MakePointer(at), ExecuteEvents.dragHandler);
        Step(1);
    }

    private static void DropOn(DarkRealms.Inventory.InventorySlotView from, DarkRealms.Inventory.InventorySlotView to, Vector2 at)
    {
        var data = MakePointer(at);
        ExecuteEvents.Execute(to.gameObject, data, ExecuteEvents.dropHandler);
        ExecuteEvents.Execute(from.gameObject, data, ExecuteEvents.endDragHandler);
        Step(2);
    }

    private static void EndDragOutside(DarkRealms.Inventory.InventorySlotView from, Vector2 at)
    {
        ExecuteEvents.Execute(from.gameObject, MakePointer(at), ExecuteEvents.endDragHandler);
        Step(2);
    }

    // ---------------------------------------------------------------- helpers

    private static string Layout(DarkRealms.Inventory.InventoryModel m)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < DarkRealms.Inventory.InventoryModel.InventorySlotCount; i++)
        {
            var s = m.GetInventorySlot(i);
            if (s != null && !s.IsEmpty) sb.Append(i).Append(':').Append(s.item.itemId).Append('x').Append(s.count).Append(' ');
        }
        sb.Append("|| ");
        foreach (DarkRealms.Inventory.EquipmentSlotId slot in Enum.GetValues(typeof(DarkRealms.Inventory.EquipmentSlotId)))
        {
            var s = m.GetEquipmentSlot(slot);
            if (s != null && !s.IsEmpty) sb.Append(slot).Append(':').Append(s.item.itemId).Append(' ');
        }
        return sb.ToString().Trim();
    }

    public static void Run()
    {
        Out.Clear();
        pass = 0;
        fail = 0;

        var ui = UnityEngine.Object.FindAnyObjectByType<DarkRealms.Inventory.InventoryUIController>();
        var model = UnityEngine.Object.FindAnyObjectByType<DarkRealms.Inventory.InventoryModel>();
        var reader = UnityEngine.Object.FindAnyObjectByType<DarkRealms.Player.PlayerInputReader>();
        var gm = UnityEngine.Object.FindAnyObjectByType<DarkRealms.Core.GameManager>();
        var pauseMenu = UnityEngine.Object.FindAnyObjectByType<DarkRealms.UI.PauseMenu>();
        events = UnityEngine.Object.FindAnyObjectByType<EventSystem>();

        if (ui == null || model == null || reader == null || gm == null || pauseMenu == null || events == null)
        {
            Log("SETUP FAILED ui=" + (ui != null) + " model=" + (model != null) + " reader=" + (reader != null) +
                " gm=" + (gm != null) + " pause=" + (pauseMenu != null) + " events=" + (events != null));
            Flush();
            return;
        }

        Log("=== stage F: inventory UX, escape routing, settings, conservation ===");
        Log("isFocused=" + EditorApplication.isFocused + " (real key delivery needs editor focus)");

        // Test items live only for this run and are destroyed at the end; the scene model is emptied again below.
        var made = new List<DarkRealms.Inventory.InventoryItemDefinition>();
        DarkRealms.Inventory.InventoryItemDefinition Make(string id, DarkRealms.Inventory.InventoryItemCategory cat, int stack)
        {
            var d = ScriptableObject.CreateInstance<DarkRealms.Inventory.InventoryItemDefinition>();
            d.itemId = id;
            d.displayName = id;
            d.category = cat;
            d.maxStack = stack;
            made.Add(d);
            return d;
        }

        var potion = Make("f.potion", DarkRealms.Inventory.InventoryItemCategory.Consumable, 100);
        var ore = Make("f.ore", DarkRealms.Inventory.InventoryItemCategory.Material, 100);
        var sword = Make("f.sword", DarkRealms.Inventory.InventoryItemCategory.Weapon, 1);
        var sword2 = Make("f.sword2", DarkRealms.Inventory.InventoryItemCategory.Weapon, 1);
        var helmet = Make("f.helmet", DarkRealms.Inventory.InventoryItemCategory.Helmet, 1);
        var quest = Make("f.quest", DarkRealms.Inventory.InventoryItemCategory.Quest, 1);

        bool prefsExisted = PlayerPrefs.HasKey(DarkRealms.Core.AppSettings.UseEscapeToPauseKey);

        try
        {
            gm.SetPaused(false);
            ui.SetOpen(true);
            Step(4);
            var window = ui.WindowTransform;
            var autoStackButton = window.Find("Inventory Toolbar/Auto Stack Button");
            var sortButton = window.Find("Inventory Toolbar/Sort Button");
            var filterButton = window.Find("Inventory Toolbar/Filter Button");
            Check(autoStackButton != null && sortButton != null && filterButton != null,
                "F0 toolbar buttons exist", "autoStack=" + (autoStackButton != null) + " sort=" + (sortButton != null) + " filter=" + (filterButton != null));

            // ============================================================ F1: toolbar through real button clicks
            Log("--- F1: Auto-Stack / Sort / filter driven through the toolbar handlers ---");
            model.AddItem(ore, 30);
            model.AddItem(ore, 40);
            model.AddItem(ore, 50);
            model.AddItem(potion, 5);
            model.AddItem(sword, 1);
            Step(2);
            int totalBefore = model.TotalItemCount;
            ClickObject(autoStackButton.gameObject);
            bool mergedToOne = model.CountInInventory(ore) == 120;
            int oreStacks = 0;
            for (int i = 0; i < 54; i++) { var s = model.GetInventorySlot(i); if (s != null && s.item == ore) oreStacks++; }
            Check(mergedToOne && oreStacks == 2, "F1 Auto-Stack merges the compatible stacks",
                "oreTotal=" + model.CountInInventory(ore) + " stacks=" + oreStacks);
            Check(model.TotalItemCount == totalBefore, "F1 Auto-Stack preserves the item total",
                totalBefore + " -> " + model.TotalItemCount);
            Check(!string.IsNullOrEmpty(ui.StatusMessage), "F1 Auto-Stack reports what it did", "'" + ui.StatusMessage + "'");

            string beforeSort = Layout(model);
            ClickObject(sortButton.gameObject);
            Check(Layout(model) != beforeSort, "F1 Sort rearranges the bag", beforeSort + " -> " + Layout(model));
            Check(model.TotalItemCount == totalBefore, "F1 Sort preserves the item total", model.TotalItemCount.ToString());
            string sortedLayout = Layout(model);
            ClickObject(sortButton.gameObject);
            Check(Layout(model) == sortedLayout, "F1 Sort is idempotent through the button too", Layout(model));
            Check(ui.StatusMessage.Contains("không cần"), "F1 second Sort reports nothing to do", "'" + ui.StatusMessage + "'");

            string beforeAutoStack = Layout(model);
            ClickObject(autoStackButton.gameObject);
            Check(Layout(model) == beforeAutoStack, "F1 second Auto-Stack changes nothing", ui.StatusMessage);

            // ============================================================ F2: drag and drop through the real handlers
            Log("--- F2: drag and drop through IBeginDrag/IDrag/IDrop/IEndDrag ---");

            model.RemoveFromInventory(FindStack(model, ore), model.CountInInventory(ore));
            ClearBag(model);
            Step(2);

            model.AddItem(ore, 30);
            int oreA = FindStack(model, ore);
            model.AddItem(potion, 7);
            int potionIndex = FindStack(model, potion);
            int emptySlot = FindEmpty(model);
            Step(2);

            var slotA = ui.GetInventorySlotView(oreA);
            var slotB = ui.GetInventorySlotView(emptySlot);
            var pointerOnA = ScreenPointOf(slotA.transform);
            var pointerOnB = ScreenPointOf(slotB.transform);
            Check(slotA != null && slotB != null && SlotUnder(pointerOnA) == slotA && SlotUnder(pointerOnB) == slotB,
                "F2 raycast finds the intended slots", "a=" + (SlotUnder(pointerOnA) == slotA) + " b=" + (SlotUnder(pointerOnB) == slotB));

            int beforeMove = model.TotalItemCount;
            BeginDrag(slotA, pointerOnA);
            Check(DarkRealms.Inventory.InventorySlotView.IsDragging, "F2 a drag on a filled slot starts", "dragging=true");
            DragOver(slotA, pointerOnB);
            Check(slotB.Highlight == DarkRealms.Inventory.SlotHighlight.ValidTarget,
                "F2 dragging over an empty slot highlights it as a valid target", slotB.Highlight.ToString());
            var weaponSlot = ui.GetEquipmentSlotView(DarkRealms.Inventory.EquipmentSlotId.Weapon1);
            DragOver(slotA, ScreenPointOf(weaponSlot.transform));
            Check(weaponSlot.Highlight == DarkRealms.Inventory.SlotHighlight.InvalidTarget,
                "F2 dragging a potion over a weapon slot highlights it as invalid", weaponSlot.Highlight.ToString());
            DropOn(slotA, slotB, pointerOnB);
            Check(!DarkRealms.Inventory.InventorySlotView.IsDragging, "F2 the drag ends cleanly", "dragging=false");
            Check(slotB.Item == ore && slotA.Item == null, "F2 bag to bag drag moved the stack",
                "from=" + (slotA.Item?.itemId ?? "-") + " to=" + (slotB.Item?.itemId ?? "-"));
            Check(model.TotalItemCount == beforeMove, "F2 the move preserved the item total", model.TotalItemCount.ToString());
            Check(slotB.Highlight == DarkRealms.Inventory.SlotHighlight.None &&
                  weaponSlot.Highlight == DarkRealms.Inventory.SlotHighlight.None,
                "F2 highlights are cleared after the drop", "b=" + slotB.Highlight + " weapon=" + weaponSlot.Highlight);

            // merge: two partial stacks of the same item produced with a real split, then dragged together
            ClearBag(model);
            model.AddItem(ore, 70);
            int wholeStack = FindStack(model, ore);
            model.TrySplitStack(wholeStack, 30, out int partialStack);
            Step(2);
            Check(model.GetInventorySlot(wholeStack)?.count == 40 && model.GetInventorySlot(partialStack)?.count == 30,
                "F2 the setup split 70 into 40 + 30",
                "a=" + (model.GetInventorySlot(wholeStack)?.count.ToString() ?? "null") +
                " b=" + (model.GetInventorySlot(partialStack)?.count.ToString() ?? "null"));
            int beforeMerge = model.TotalItemCount;
            var mergeFrom = ui.GetInventorySlotView(partialStack);
            var mergeTarget = ui.GetInventorySlotView(wholeStack);
            BeginDrag(mergeFrom, ScreenPointOf(mergeFrom.transform));
            DragOver(mergeFrom, ScreenPointOf(mergeTarget.transform));
            Check(mergeTarget.Highlight == SlotHighlight.ValidTarget,
                "F2 a matching partial stack is highlighted as a valid merge target", mergeTarget.Highlight.ToString());
            DropOn(mergeFrom, mergeTarget, ScreenPointOf(mergeTarget.transform));
            Check(model.GetInventorySlot(wholeStack)?.count == 70 && model.GetInventorySlot(partialStack) == null,
                "F2 dropping a matching stack onto another merges them",
                "count=" + (model.GetInventorySlot(wholeStack)?.count.ToString() ?? "null"));
            Check(model.TotalItemCount == beforeMerge, "F2 the merge preserved the item total", model.TotalItemCount.ToString());

            // swap two different items
            ClearBag(model);
            model.AddItem(ore, 12);
            model.AddItem(potion, 3);
            int oreSlot = FindStack(model, ore);
            int potionSlot = FindStack(model, potion);
            Step(2);
            BeginDrag(ui.GetInventorySlotView(oreSlot), ScreenPointOf(ui.GetInventorySlotView(oreSlot).transform));
            DropOn(ui.GetInventorySlotView(oreSlot), ui.GetInventorySlotView(potionSlot),
                ScreenPointOf(ui.GetInventorySlotView(potionSlot).transform));
            Check(model.GetInventorySlot(potionSlot)?.item == ore && model.GetInventorySlot(oreSlot)?.item == potion,
                "F2 dragging onto a different item swaps the two stacks",
                "oreSlot=" + (model.GetInventorySlot(oreSlot)?.item?.itemId ?? "-") +
                " potionSlot=" + (model.GetInventorySlot(potionSlot)?.item?.itemId ?? "-"));

            // bag -> equipment
            ClearBag(model);
            model.AddItem(sword, 1);
            int swordSlot = FindStack(model, sword);
            Step(2);
            var swordView = ui.GetInventorySlotView(swordSlot);
            var weapon1 = ui.GetEquipmentSlotView(DarkRealms.Inventory.EquipmentSlotId.Weapon1);
            BeginDrag(swordView, ScreenPointOf(swordView.transform));
            DragOver(swordView, ScreenPointOf(weapon1.transform));
            Check(weapon1.Highlight == DarkRealms.Inventory.SlotHighlight.ValidTarget,
                "F2 dragging a weapon over a weapon slot is a valid target", weapon1.Highlight.ToString());
            int beforeEquip = model.TotalItemCount;
            DropOn(swordView, weapon1, ScreenPointOf(weapon1.transform));
            Check(model.GetEquipmentSlot(DarkRealms.Inventory.EquipmentSlotId.Weapon1)?.item == sword,
                "F2 dropping a weapon on a weapon slot equips it", "worn=" + (model.GetEquipmentSlot(DarkRealms.Inventory.EquipmentSlotId.Weapon1)?.item?.itemId ?? "-"));
            Check(model.TotalItemCount == beforeEquip, "F2 equipping preserved the item total", model.TotalItemCount.ToString());

            // equipment -> equipment and equipment -> bag
            model.AddItem(sword2, 1);
            int sword2Slot = FindStack(model, sword2);
            var weapon2 = ui.GetEquipmentSlotView(DarkRealms.Inventory.EquipmentSlotId.Weapon2);
            Step(2);
            BeginDrag(ui.GetInventorySlotView(sword2Slot), ScreenPointOf(ui.GetInventorySlotView(sword2Slot).transform));
            DropOn(ui.GetInventorySlotView(sword2Slot), weapon2, ScreenPointOf(weapon2.transform));
            Check(model.GetEquipmentSlot(DarkRealms.Inventory.EquipmentSlotId.Weapon2)?.item == sword2,
                "F2 a second weapon goes into weapon slot 2", "worn=" + (model.GetEquipmentSlot(DarkRealms.Inventory.EquipmentSlotId.Weapon2)?.item?.itemId ?? "-"));
            BeginDrag(weapon1, ScreenPointOf(weapon1.transform));
            DropOn(weapon1, weapon2, ScreenPointOf(weapon2.transform));
            Check(model.GetEquipmentSlot(DarkRealms.Inventory.EquipmentSlotId.Weapon1)?.item == sword2 &&
                  model.GetEquipmentSlot(DarkRealms.Inventory.EquipmentSlotId.Weapon2)?.item == sword,
                "F2 dragging between equipment slots swaps them",
                "w1=" + (model.GetEquipmentSlot(DarkRealms.Inventory.EquipmentSlotId.Weapon1)?.item?.itemId ?? "-") +
                " w2=" + (model.GetEquipmentSlot(DarkRealms.Inventory.EquipmentSlotId.Weapon2)?.item?.itemId ?? "-"));

            int beforeUnequip = model.TotalItemCount;
            int freeSlot = FindEmpty(model);
            BeginDrag(weapon1, ScreenPointOf(weapon1.transform));
            DropOn(weapon1, ui.GetInventorySlotView(freeSlot), ScreenPointOf(ui.GetInventorySlotView(freeSlot).transform));
            Check(model.GetEquipmentSlot(DarkRealms.Inventory.EquipmentSlotId.Weapon1) == null &&
                  model.GetInventorySlot(freeSlot)?.item == sword2,
                "F2 dragging from equipment to the bag unequips it",
                "worn=" + (model.GetEquipmentSlot(DarkRealms.Inventory.EquipmentSlotId.Weapon1)?.item?.itemId ?? "-"));
            Check(model.TotalItemCount == beforeUnequip, "F2 unequipping preserved the item total", model.TotalItemCount.ToString());

            // dropped outside
            ClearBag(model);
            model.AddItem(ore, 25);
            int lonely = FindStack(model, ore);
            Step(2);
            string beforeOutside = Layout(model);
            BeginDrag(ui.GetInventorySlotView(lonely), ScreenPointOf(ui.GetInventorySlotView(lonely).transform));
            EndDragOutside(ui.GetInventorySlotView(lonely), new Vector2(4f, 4f));
            Check(Layout(model) == beforeOutside && !DarkRealms.Inventory.InventorySlotView.IsDragging,
                "F2 dropping outside every slot cancels and changes nothing", ui.StatusMessage);
            Check(ui.StatusMessage.Contains("huỷ"), "F2 the cancelled drag says so", "'" + ui.StatusMessage + "'");

            // stale source: sort while a drag is in flight
            ClearBag(model);
            model.AddItem(ore, 10);
            model.AddItem(potion, 4);
            int staleFrom = FindStack(model, ore);
            int staleTo = FindStack(model, potion);
            Step(2);
            var staleView = ui.GetInventorySlotView(staleFrom);
            var staleTarget = ui.GetInventorySlotView(staleTo);
            BeginDrag(staleView, ScreenPointOf(staleView.transform));
            bool changed = ui.SortBag();
            Check(!DarkRealms.Inventory.InventorySlotView.IsDragging, "F2 sorting cancels a drag in flight", "changed=" + changed);
            Check(model.CountInInventory(ore) == 10 && model.CountInInventory(potion) == 4,
                "F2 the cancelled drag left both stacks intact", Layout(model));

            // escape mid drag
            int escapeFrom = FindStack(model, ore);
            var escapeView = ui.GetInventorySlotView(escapeFrom);
            BeginDrag(escapeView, ScreenPointOf(escapeView.transform));
            string beforeEscape = Layout(model);
            bool bagWasOpen = ui.IsOpen;
            pauseMenu.HandlePausePressed(true);
            Step(3);
            Check(bagWasOpen && !ui.IsOpen && Layout(model) == beforeEscape && !InventorySlotView.IsDragging,
                "F2 one Escape press with a drag in flight closes the bag, cancels the drag and pauses nothing",
                "wasOpen=" + bagWasOpen + " layout=" + Layout(model));
            Check(!gm.IsPaused && Mathf.Approximately(Time.timeScale, 1f),
                "F2 that Escape did not also pause the game", "paused=" + gm.IsPaused + " timeScale=" + Time.timeScale);

            // ============================================================ F3: clicks
            Log("--- F3: double-click, right-click, context menu ---");
            ui.SetOpen(true);
            ClearBag(model);
            model.AddItem(sword, 1);
            int dblSlot = FindStack(model, sword);
            Step(2);
            var dblView = ui.GetInventorySlotView(dblSlot);
            var dblPointer = ScreenPointOf(dblView.transform);
            // Both clicks in the same frame so the double-click window cannot be missed by test timing.
            ExecuteEvents.Execute(dblView.gameObject, MakePointer(dblPointer), ExecuteEvents.pointerClickHandler);
            ExecuteEvents.Execute(dblView.gameObject, MakePointer(dblPointer), ExecuteEvents.pointerClickHandler);
            Step(2);
            Check(model.GetEquipmentSlot(DarkRealms.Inventory.EquipmentSlotId.Weapon1)?.item == sword,
                "F3 double-click equips the item", "worn=" + (model.GetEquipmentSlot(DarkRealms.Inventory.EquipmentSlotId.Weapon1)?.item?.itemId ?? "-"));
            int dblWornSlot = FindEquipmentIndex(model, sword);
            var wornView = ui.GetEquipmentSlotView(DarkRealms.Inventory.EquipmentSlotId.Weapon1);
            var wornPointer = ScreenPointOf(wornView.transform);
            ExecuteEvents.Execute(wornView.gameObject, MakePointer(wornPointer), ExecuteEvents.pointerClickHandler);
            ExecuteEvents.Execute(wornView.gameObject, MakePointer(wornPointer), ExecuteEvents.pointerClickHandler);
            Step(2);
            Check(model.GetEquipmentSlot(DarkRealms.Inventory.EquipmentSlotId.Weapon1) == null &&
                  model.CountInInventory(sword) == 1,
                "F3 double-click on a worn item takes it back to the bag", Layout(model));

            ClearBag(model);
            model.AddItem(ore, 12);
            int ctxSlot = FindStack(model, ore);
            Step(2);
            var ctxView = ui.GetInventorySlotView(ctxSlot);
            var ctxPointer = ScreenPointOf(ctxView.transform);
            ExecuteEvents.Execute(ctxView.gameObject, MakePointer(ctxPointer, PointerEventData.InputButton.Right),
                ExecuteEvents.pointerClickHandler);
            Step(2);
            Check(ui.IsContextMenuOpen, "F3 right-click opens the context menu", "open=" + ui.IsContextMenuOpen);
            var splitRow = ui.WindowTransform == null ? null : GameObject.Find("Inventory Context Menu");
            var menuSplit = splitRow != null ? splitRow.transform.Find("Menu Card/Menu Split") : null;
            var menuEquip = splitRow != null ? splitRow.transform.Find("Menu Card/Menu Equip") : null;
            Check(menuSplit != null && menuSplit.gameObject.activeSelf, "F3 the menu offers Split for a stack of 12",
                "splitActive=" + (menuSplit != null && menuSplit.gameObject.activeSelf));
            Check(menuEquip != null && menuEquip.gameObject.activeSelf == false,
                "F3 the menu does not offer Equip for a material",
                "equipActive=" + (menuEquip != null && menuEquip.gameObject.activeSelf));
            string beforeMenuEscape = Layout(model);
            bool menuEscape = ui.HandleEscapePress();
            Check(menuEscape && !ui.IsContextMenuOpen && ui.IsOpen,
                "F3 Escape closes the context menu first and leaves the bag open",
                "consumed=" + menuEscape + " menu=" + ui.IsContextMenuOpen + " bag=" + ui.IsOpen);
            Check(Layout(model) == beforeMenuEscape, "F3 closing the menu changed nothing", Layout(model));

            // ============================================================ F4: split prompt
            Log("--- F4: split stack prompt ---");
            int splitSource = FindStack(model, ore);
            ui.OpenSplitPrompt(splitSource);
            Step(2);
            Check(ui.IsSplitPromptOpen, "F4 the split prompt opens for a stack of 12", "open=" + ui.IsSplitPromptOpen);
            var splitPrompt = GameObject.Find("Split Stack Prompt");
            var splitField = splitPrompt != null ? splitPrompt.transform.Find("Split Card/Split Amount")?.GetComponent<InputField>() : null;
            var splitConfirm = splitPrompt != null ? splitPrompt.transform.Find("Split Card/Split Confirm") : null;
            Check(splitField != null && splitConfirm != null, "F4 prompt has an amount field and a confirm button",
                "field=" + (splitField != null) + " confirm=" + (splitConfirm != null));

            string beforePromptEscape = Layout(model);
            bool promptEscape = ui.HandleEscapePress();
            Check(promptEscape && !ui.IsSplitPromptOpen && ui.IsOpen,
                "F4 Escape closes the split prompt first and leaves the bag open",
                "consumed=" + promptEscape + " prompt=" + ui.IsSplitPromptOpen + " bag=" + ui.IsOpen);
            Check(Layout(model) == beforePromptEscape, "F4 dismissing the prompt changed nothing", Layout(model));
            ui.OpenSplitPrompt(splitSource);
            Step(1);

            int beforeSplit = model.TotalItemCount;
            splitField.text = "999";
            Click(splitConfirm);
            Check(ui.IsSplitPromptOpen && model.TotalItemCount == beforeSplit,
                "F4 an out-of-range amount is refused without touching the model",
                "'" + ui.StatusMessage + "'");
            splitField.text = "abc";
            Click(splitConfirm);
            Check(ui.IsSplitPromptOpen, "F4 a non-numeric amount is refused", "'" + ui.StatusMessage + "'");
            splitField.text = "5";
            Click(splitConfirm);
            Step(2);
            Check(!ui.IsSplitPromptOpen, "F4 the prompt closes after a successful split", "open=" + ui.IsSplitPromptOpen);
            Check(model.CountInInventory(ore) == 12 && model.GetInventorySlot(splitSource)?.count == 7,
                "F4 the split produced 7 + 5 and kept the total",
                "source=" + (model.GetInventorySlot(splitSource)?.count.ToString() ?? "null") + " total=" + model.CountInInventory(ore));
            int afterSplitTotal = model.TotalItemCount;
            bool secondConfirm = ui.ConfirmSplit();
            Check(!secondConfirm && model.TotalItemCount == afterSplitTotal,
                "F4 confirming again after a successful split does nothing", "again=" + secondConfirm);

            // source changed while the prompt is open
            int changedSource = FindStack(model, ore);
            ui.OpenSplitPrompt(changedSource);
            Step(1);
            int sourceCount = model.GetInventorySlot(changedSource).count;
            model.RemoveFromInventory(changedSource, sourceCount - 1);
            int afterRemoval = model.CountInInventory(ore);
            splitField.text = sourceCount.ToString();
            Click(splitConfirm);
            Step(2);
            Check(!ui.IsSplitPromptOpen && model.CountInInventory(ore) == afterRemoval &&
                  model.GetInventorySlot(changedSource)?.count == 1,
                "F4 a stack that changed under the prompt is re-validated instead of trusted",
                "'" + ui.StatusMessage + "' total=" + model.CountInInventory(ore) + " source=" +
                (model.GetInventorySlot(changedSource)?.count.ToString() ?? "null"));

            // ============================================================ F5: search and filter
            Log("--- F5: search and category filter ---");
            ClearBag(model);
            model.AddItem(ore, 10);
            model.AddItem(potion, 4);
            model.AddItem(sword, 1);
            Step(2);
            int oreIdx = FindStack(model, ore);
            int potionIdx = FindStack(model, potion);
            int swordIdx = FindStack(model, sword);
            string beforeFilter = Layout(model);

            var searchField = window.Find("Inventory Toolbar/Search Field")?.GetComponent<InputField>();
            Check(searchField != null, "F5 the toolbar has a search field", searchField != null ? searchField.name : "<null>");
            searchField.text = "f.ore";
            Step(2);
            Check(!ui.GetInventorySlotView(oreIdx).IsFilteredOut &&
                  ui.GetInventorySlotView(potionIdx).IsFilteredOut &&
                  ui.GetInventorySlotView(swordIdx).IsFilteredOut,
                "F5 the search hides the slots that do not match",
                "ore=" + !ui.GetInventorySlotView(oreIdx).IsFilteredOut +
                " potion=" + ui.GetInventorySlotView(potionIdx).IsFilteredOut +
                " sword=" + ui.GetInventorySlotView(swordIdx).IsFilteredOut);
            Check(Layout(model) == beforeFilter, "F5 searching does not move or remove anything", Layout(model));
            Check(ui.GetInventorySlotView(20).InventoryIndex == 20,
                "F5 a filtered view keeps its model index", ui.GetInventorySlotView(20).InventoryIndex.ToString());

            searchField.text = "ORE";
            Step(2);
            Check(!ui.GetInventorySlotView(oreIdx).IsFilteredOut,
                "F5 the search ignores case", "ore=" + !ui.GetInventorySlotView(oreIdx).IsFilteredOut);
            searchField.text = string.Empty;
            Step(2);
            Check(!ui.GetInventorySlotView(potionIdx).IsFilteredOut, "F5 clearing the search restores every slot", "");

            ui.SetFilter(DarkRealms.Inventory.InventoryItemCategory.Weapon);
            Step(2);
            Check(!ui.GetInventorySlotView(swordIdx).IsFilteredOut &&
                  ui.GetInventorySlotView(oreIdx).IsFilteredOut &&
                  ui.GetInventorySlotView(potionIdx).IsFilteredOut,
                "F5 the category filter keeps only the matching category", ui.FilterDescription);
            Check(Layout(model) == beforeFilter, "F5 filtering does not move items", Layout(model));
            Check(ui.GetInventorySlotView(potionIdx).GetComponent<CanvasGroup>().blocksRaycasts == false,
                "F5 a filtered-out slot stops accepting pointer input", "blocksRaycasts=false");
            Check(SlotUnder(ScreenPointOf(ui.GetInventorySlotView(potionIdx).transform)) != ui.GetInventorySlotView(potionIdx),
                "F5 a raycast cannot select a filtered-out slot", "hit=hidden");
            ui.CycleFilter();
            Check(ui.ActiveCategoryFilter == DarkRealms.Inventory.InventoryItemCategory.Helmet,
                "F5 the filter button cycles to the next category", ui.FilterDescription);

            // ============================================================ F6: escape, settings, pause ownership
            Log("--- F6: escape order, settings page, pause ownership ---");
            ui.SetFilter((DarkRealms.Inventory.InventoryItemCategory?)null);
            bool defaultSetting = DarkRealms.Core.AppSettings.UseEscapeToPause;
            Check(defaultSetting, "F6 Use ESC to Pause defaults to ON on a first run", "value=" + defaultSetting);

            DarkRealms.Core.AppSettings.UseEscapeToPause = false;
            DarkRealms.Core.AppSettings.Reload();
            Check(!DarkRealms.Core.AppSettings.UseEscapeToPause,
                "F6 the setting is read back from storage after a reload", "value=" + DarkRealms.Core.AppSettings.UseEscapeToPause);
            Check(DarkRealms.Core.AppSettings.HasStoredUseEscapeToPause,
                "F6 the setting wrote a clearly named PlayerPrefs key", DarkRealms.Core.AppSettings.UseEscapeToPauseKey);

            ui.SetOpen(false);
            Step(2);
            gm.SetPaused(false);
            Step(2);
            pauseMenu.HandlePausePressed(true);
            Step(3);
            Check(!gm.IsPaused, "F6 with the toggle OFF Escape does not pause in gameplay", "paused=" + gm.IsPaused);

            DarkRealms.Core.AppSettings.UseEscapeToPause = true;
            pauseMenu.HandlePausePressed(true);
            Step(3);
            Check(gm.IsPaused && Mathf.Approximately(Time.timeScale, 0f),
                "F6 flipping the toggle ON applies immediately without a restart",
                "paused=" + gm.IsPaused + " timeScale=" + Time.timeScale);

            var settingsPanel = UnityEngine.Object.FindAnyObjectByType<DarkRealms.UI.SettingsPanel>();
            Check(settingsPanel != null, "F6 a Settings page is reachable from the pause menu", settingsPanel != null ? settingsPanel.name : "<null>");
            pauseMenu.OpenSettings();
            Step(2);
            Check(settingsPanel != null && settingsPanel.IsOpen && gm.IsPaused,
                "F6 opening Settings keeps the game paused and shows the page",
                "open=" + (settingsPanel != null && settingsPanel.IsOpen) + " paused=" + gm.IsPaused);
            pauseMenu.HandlePausePressed(true);
            Step(2);
            Check(settingsPanel != null && !settingsPanel.IsOpen && gm.IsPaused,
                "F6 Escape closes the Settings page first and does not unpause",
                "open=" + (settingsPanel != null && settingsPanel.IsOpen) + " paused=" + gm.IsPaused);

            pauseMenu.HandlePausePressed(true);
            Step(2);
            Check(!gm.IsPaused && Mathf.Approximately(Time.timeScale, 1f),
                "F6 Escape from the pause menu resumes the game", "paused=" + gm.IsPaused);

            DarkRealms.Core.AppSettings.UseEscapeToPause = false;
            ui.SetOpen(true);
            Step(2);
            bool bagWasOpenWithToggleOff = ui.IsOpen;
            pauseMenu.HandlePausePressed(true);
            Step(2);
            Check(bagWasOpenWithToggleOff && !ui.IsOpen && !gm.IsPaused,
                "F6 with the toggle OFF Escape still closes the bag and does not pause",
                "bag=" + ui.IsOpen + " paused=" + gm.IsPaused);

            DarkRealms.Core.AppSettings.UseEscapeToPause = true;
            ui.SetOpen(true);
            Step(2);
            pauseMenu.HandlePausePressed(true);
            Step(2);
            Check(!ui.IsOpen && !gm.IsPaused,
                "F6 with the toggle ON one Escape press only closes the bag", "bag=" + ui.IsOpen + " paused=" + gm.IsPaused);

            gm.SetPaused(true);
            Step(2);
            ui.SetOpen(true);
            Step(2);
            pauseMenu.HandlePausePressed(true);
            Step(2);
            Check(!ui.IsOpen && gm.IsPaused && Mathf.Approximately(Time.timeScale, 0f),
                "F6 closing the bag while paused never unpauses", "bag=" + ui.IsOpen + " paused=" + gm.IsPaused);
            pauseMenu.HandlePausePressed(true);
            Step(2);
            Check(!gm.IsPaused, "F6 Escape still resumes from a pause", "paused=" + gm.IsPaused);

            // ============================================================ F7: input lock and conservation
            Log("--- F7: input lock and item conservation ---");
            gm.SetPaused(false);
            ui.SetOpen(true);
            Step(3);
            Check(!reader.Enabled, "F7 an open bag locks gameplay input", "input=" + reader.Enabled);
            Check(!gm.IsPaused && Mathf.Approximately(Time.timeScale, 1f),
                "F7 an open bag does not pause the world", "paused=" + gm.IsPaused);
            ui.SetOpen(false);
            Step(3);
            Check(reader.Enabled, "F7 closing the bag restores input", "input=" + reader.Enabled);

            // Pause through the menu, which is the only real code path: it owns the gameplay input gate and
            // drives GameManager, the owner of the paused flag and Time.timeScale.
            pauseMenu.SetPaused(true);
            Step(2);
            Check(!reader.Enabled && gm.IsPaused, "F7 pausing through the menu locks input",
                "input=" + reader.Enabled + " paused=" + gm.IsPaused);
            ui.SetOpen(true);
            Step(2);
            Check(!reader.Enabled, "F7 the bag stays locked while paused", "input=" + reader.Enabled);
            ui.SetOpen(false);
            Step(2);
            Check(!reader.Enabled, "F7 closing the bag in a paused game keeps input locked", "input=" + reader.Enabled);
            pauseMenu.SetPaused(false);
            Step(2);
            Check(reader.Enabled && !gm.IsPaused && Mathf.Approximately(Time.timeScale, 1f),
                "F7 resuming through the menu restores input and time",
                "input=" + reader.Enabled + " paused=" + gm.IsPaused + " timeScale=" + Time.timeScale);

            // Conservation across every transfer kind, driven through the UI where a UI path exists.
            // The bag has to be open for a click to reach a slot at all, exactly as it would for a player.
            ui.SetOpen(true);
            Step(2);
            ClearBag(model);
            model.AddItem(ore, 100);
            model.AddItem(potion, 50);
            model.AddItem(sword, 1);
            model.AddItem(helmet, 1);
            Step(2);
            int startTotal = model.TotalItemCount;
            var totals = new Dictionary<string, int>
            {
                { "f.ore", model.CountInInventory(ore) },
                { "f.potion", model.CountInInventory(potion) },
                { "f.sword", model.CountInInventory(sword) },
                { "f.helmet", model.CountInInventory(helmet) }
            };

            // quick equip through a real double click, then quick unequip through shift-click
            int qSword = FindStack(model, sword);
            var qSwordView = ui.GetInventorySlotView(qSword);
            var qPointer = ScreenPointOf(qSwordView.transform);
            ExecuteEvents.Execute(qSwordView.gameObject, MakePointer(qPointer), ExecuteEvents.pointerClickHandler);
            ExecuteEvents.Execute(qSwordView.gameObject, MakePointer(qPointer), ExecuteEvents.pointerClickHandler);
            Step(2);
            Check(model.CountEquipped(sword) == 1, "F7 quick equip moved the sword to a slot",
                "worn=" + model.CountEquipped(sword) + " status='" + ui.StatusMessage + "'");

            var qHelmet = FindStack(model, helmet);
            var qHelmetView = ui.GetInventorySlotView(qHelmet);
            ui.QuickMove(qHelmetView);
            Step(2);
            Check(model.CountEquipped(helmet) == 1, "F7 the helmet quick-equips into the helmet slot",
                "worn=" + model.CountEquipped(helmet));

            var wornSword = ui.GetEquipmentSlotView(DarkRealms.Inventory.EquipmentSlotId.Weapon1);
            ui.QuickMove(wornSword);
            Step(2);
            Check(model.CountEquipped(sword) == 0 && model.CountInInventory(sword) == 1,
                "F7 quick unequip returns the sword to the bag",
                "bag=" + model.CountInInventory(sword) + " worn=" + model.CountEquipped(sword));

            ui.QuickMove(ui.GetEquipmentSlotView(DarkRealms.Inventory.EquipmentSlotId.Helmet));
            Step(2);
            Check(model.CountEquipped(helmet) == 0 && model.CountInInventory(helmet) == 1,
                "F7 quick unequip returns the helmet to the bag", "bag=" + model.CountInInventory(helmet));

            // auto stack and sort again after all that moving
            ClickObject(autoStackButton.gameObject);
            Check(model.CountInInventory(ore) == 100 && model.CountInInventory(potion) == 50,
                "F7 Auto-Stack after the UI transfers kept every item",
                "ore=" + model.CountInInventory(ore) + " potion=" + model.CountInInventory(potion));
            ClickObject(sortButton.gameObject);
            Check(model.TotalItemCount == startTotal, "F7 the item total survived every UI operation",
                startTotal + " -> " + model.TotalItemCount);
            Check(model.CountInInventory(ore) == totals["f.ore"] && model.CountInInventory(potion) == totals["f.potion"] &&
                  model.CountInInventory(sword) == totals["f.sword"] && model.CountInInventory(helmet) == totals["f.helmet"],
                "F7 per-item totals are unchanged by the whole sequence",
                "ore=" + model.CountInInventory(ore) + " potion=" + model.CountInInventory(potion) +
                " sword=" + model.CountInInventory(sword) + " helmet=" + model.CountInInventory(helmet));

            // ============================================================ F8: real input reality check
            Log("--- F8: real input delivery ---");
            Log("  editor focused: " + EditorApplication.isFocused);
            Log("  backgroundBehavior=" + InputSystem.settings.backgroundBehavior +
                " editorInputBehaviorInPlayMode=" + InputSystem.settings.editorInputBehaviorInPlayMode);
            Log("  (unchanged: switching these on only to make an automated check pass is out of scope)");
            Check(!DarkRealms.Inventory.InventorySlotView.IsDragging && EverythingClosed(ui),
                "F8 no UI state is left open at the end of the stage", "bag=" + ui.IsOpen + " menu=" + ui.IsContextMenuOpen);
        }
        catch (Exception ex)
        {
            fail++;
            Log("  [EXCEPTION] " + ex);
        }
        finally
        {
            try
            {
                DarkRealms.Core.AppSettings.UseEscapeToPause = true;
                if (!prefsExisted)
                {
                    PlayerPrefs.DeleteKey(DarkRealms.Core.AppSettings.UseEscapeToPauseKey);
                    PlayerPrefs.Save();
                }

                ui.SetOpen(false);
                gm.SetPaused(false);
                ClearBag(model);
                Step(2);
            }
            catch (Exception cleanupEx)
            {
                Log("  [CLEANUP WARNING] " + cleanupEx.Message);
            }

            foreach (var d in made) UnityEngine.Object.DestroyImmediate(d);

            Log($"RESULT: stage F {pass} passed, {fail} failed");
            Flush();
        }
    }

    private static bool EverythingClosed(DarkRealms.Inventory.InventoryUIController ui)
    {
        return !ui.IsContextMenuOpen && !ui.IsSplitPromptOpen && !ui.IsTooltipVisible;
    }

    private static void ClearBag(DarkRealms.Inventory.InventoryModel model)
    {
        // Empty the equipment first so nothing is stranded, then drop every bag stack.
        foreach (DarkRealms.Inventory.EquipmentSlotId slot in Enum.GetValues(typeof(DarkRealms.Inventory.EquipmentSlotId)))
        {
            var equipped = model.GetEquipmentSlot(slot);
            if (equipped == null || equipped.IsEmpty) continue;
            if (model.FirstEmptyInventoryIndex() < 0)
                model.RemoveFromInventory(0, model.GetInventorySlot(0).count);
            model.TryQuickUnequip(slot, out _);
        }

        for (int i = 0; i < DarkRealms.Inventory.InventoryModel.InventorySlotCount; i++)
        {
            var stack = model.GetInventorySlot(i);
            if (stack != null && !stack.IsEmpty)
                model.RemoveFromInventory(i, stack.count);
        }
    }

    private static int FindStack(DarkRealms.Inventory.InventoryModel model, DarkRealms.Inventory.InventoryItemDefinition item)
    {
        for (int i = 0; i < DarkRealms.Inventory.InventoryModel.InventorySlotCount; i++)
        {
            var s = model.GetInventorySlot(i);
            if (s != null && s.item == item) return i;
        }
        return -1;
    }

    private static int FindEmpty(DarkRealms.Inventory.InventoryModel model) => model.FirstEmptyInventoryIndex();

    private static int FindEquipmentIndex(DarkRealms.Inventory.InventoryModel model, DarkRealms.Inventory.InventoryItemDefinition item)
    {
        foreach (DarkRealms.Inventory.EquipmentSlotId slot in Enum.GetValues(typeof(DarkRealms.Inventory.EquipmentSlotId)))
        {
            var s = model.GetEquipmentSlot(slot);
            if (s != null && s.item == item) return (int)slot;
        }
        return -1;
    }
}
}
#endif

#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using DarkRealms.Inventory;
using UnityEditor;
using UnityEngine;

namespace DarkRealms.EditorTools
{
    /// <summary>
    /// Randomised and table-driven verification of <see cref="InventoryModel"/>, run against the real component
    /// rather than a copy of its logic.
    ///
    /// The harness never re-implements the rules it checks. It drives the public API with a deterministic random
    /// sequence (and with a table of boundary values), then asserts the invariants that must survive every call:
    /// no negative or zero-count stack, no stack over its own cap, equipment holding only what its slot accepts,
    /// per-item totals preserved by every reorganising operation, an exact delta for add/remove, no change at all
    /// when an operation is refused, and no silent merging of single-item stacks (the stand-in for unique
    /// instances). Sort and Auto-Stack must also be idempotent.
    ///
    /// A failure prints the seed, the operation index, the last operations, and the layouts before and after, which
    /// is everything needed to reproduce it.
    /// </summary>
    public static class InventoryVerification
    {
        private static readonly List<string> Report = new List<string>();
        private static int failures;
        private static string currentReportName = "InventoryModelReport.txt";

        private static string ReportPath =>
            Path.Combine(Path.GetDirectoryName(Application.dataPath) ?? ".", currentReportName);

        private static void Line(string text)
        {
            Report.Add(text);
            Debug.Log("[InventoryVerification] " + text);
        }

        private static void Failure(string seed, int opIndex, string op, string detail, string before, string after)
        {
            failures++;
            Line($"  [INVARIANT FAILURE] seed={seed} opIndex={opIndex}");
            Line($"    op     : {op}");
            Line($"    detail : {detail}");
            Line($"    before : {before}");
            Line($"    after  : {after}");
        }

        private static void Flush()
        {
            try
            {
                File.WriteAllText(ReportPath, string.Join("\n", Report) + "\n");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[InventoryVerification] could not write {ReportPath}: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------ observation helpers

        private static string Layout(InventoryModel model)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < InventoryModel.InventorySlotCount; i++)
            {
                InventoryStack stack = model.GetInventorySlot(i);
                if (stack != null && !stack.IsEmpty)
                    sb.Append(i).Append(':').Append(stack.item.itemId).Append('x').Append(stack.count).Append(' ');
            }

            sb.Append("|| ");
            foreach (EquipmentSlotId slot in Enum.GetValues(typeof(EquipmentSlotId)))
            {
                InventoryStack stack = model.GetEquipmentSlot(slot);
                if (stack != null && !stack.IsEmpty)
                    sb.Append(slot).Append(':').Append(stack.item.itemId).Append('x').Append(stack.count).Append(' ');
            }

            return sb.ToString().Trim();
        }

        private static Dictionary<string, int> Totals(InventoryModel model)
        {
            var map = new Dictionary<string, int>();
            Action<InventoryStack> add = stack =>
            {
                if (stack == null || stack.IsEmpty) return;
                map.TryGetValue(stack.item.itemId, out int count);
                map[stack.item.itemId] = count + stack.count;
            };

            for (int i = 0; i < InventoryModel.InventorySlotCount; i++) add(model.GetInventorySlot(i));
            foreach (EquipmentSlotId slot in Enum.GetValues(typeof(EquipmentSlotId))) add(model.GetEquipmentSlot(slot));
            return map;
        }

        /// <summary>Slots (bag + equipment) holding each item. A silent merge of single-item stacks lowers this.</summary>
        private static Dictionary<string, int> OccupiedSlots(InventoryModel model)
        {
            var map = new Dictionary<string, int>();
            Action<InventoryStack> add = stack =>
            {
                if (stack == null || stack.IsEmpty) return;
                map.TryGetValue(stack.item.itemId, out int count);
                map[stack.item.itemId] = count + 1;
            };

            for (int i = 0; i < InventoryModel.InventorySlotCount; i++) add(model.GetInventorySlot(i));
            foreach (EquipmentSlotId slot in Enum.GetValues(typeof(EquipmentSlotId))) add(model.GetEquipmentSlot(slot));
            return map;
        }

        private static bool SameMap(Dictionary<string, int> a, Dictionary<string, int> b)
        {
            if (a.Count != b.Count) return false;
            foreach (KeyValuePair<string, int> pair in a)
            {
                if (!b.TryGetValue(pair.Key, out int value) || value != pair.Value)
                    return false;
            }
            return true;
        }

        private static string Dump(Dictionary<string, int> map) =>
            string.Join(",", map.OrderBy(pair => pair.Key).Select(pair => pair.Key + "=" + pair.Value));

        /// <summary>Structural conditions that must hold after ANY call, successful or refused.</summary>
        private static string StructuralViolations(InventoryModel model)
        {
            for (int i = 0; i < InventoryModel.InventorySlotCount; i++)
            {
                InventoryStack stack = model.GetInventorySlot(i);
                if (stack == null) continue;
                if (stack.item == null) return $"bag slot {i} holds a stack with a null item";
                if (stack.count <= 0) return $"bag slot {i} has count {stack.count} and should have been cleared";
                if (stack.count > stack.item.MaxStack) return $"bag slot {i} count {stack.count} exceeds MaxStack {stack.item.MaxStack}";
                if (stack.count > InventoryModel.AbsoluteMaxStack) return $"bag slot {i} exceeds the absolute stack cap";
            }

            foreach (EquipmentSlotId slot in Enum.GetValues(typeof(EquipmentSlotId)))
            {
                InventoryStack stack = model.GetEquipmentSlot(slot);
                if (stack == null) continue;
                if (stack.item == null) return $"equipment {slot} holds a stack with a null item";
                if (stack.count != 1) return $"equipment {slot} holds {stack.count} items; a slot takes exactly one";
                if (!InventoryModel.CanEquip(stack.item, slot))
                    return $"equipment {slot} holds {stack.item.itemId} ({stack.item.category}), which that slot does not accept";
            }

            return null;
        }

        // ------------------------------------------------------------------ fixtures

        private sealed class Fixture : IDisposable
        {
            public GameObject Root;
            public InventoryModel Model;
            public InventoryItemDefinition Potion;
            public InventoryItemDefinition Ore;
            public InventoryItemDefinition Arrows;
            public InventoryItemDefinition Sword;
            public InventoryItemDefinition Sword2;
            public InventoryItemDefinition Helmet;
            public InventoryItemDefinition Ring;
            public InventoryItemDefinition Ring2;
            public InventoryItemDefinition Quest;
            public InventoryItemDefinition[] All;
            public HashSet<string> StackOne;

            public void Dispose()
            {
                if (Root != null) UnityEngine.Object.DestroyImmediate(Root);
                foreach (InventoryItemDefinition def in All)
                    if (def != null) UnityEngine.Object.DestroyImmediate(def);
            }
        }

        private static Fixture CreateFixture(string rootName)
        {
            var fixture = new Fixture();

            InventoryItemDefinition Make(string id, InventoryItemCategory category, int maxStack)
            {
                InventoryItemDefinition def = ScriptableObject.CreateInstance<InventoryItemDefinition>();
                def.itemId = id;
                def.displayName = id;
                def.category = category;
                def.maxStack = maxStack;
                return def;
            }

            fixture.Potion = Make("t.potion", InventoryItemCategory.Consumable, 100);
            fixture.Ore = Make("t.ore", InventoryItemCategory.Material, 100);
            fixture.Arrows = Make("t.arrow", InventoryItemCategory.Material, 20);
            fixture.Sword = Make("t.sword", InventoryItemCategory.Weapon, 1);
            fixture.Sword2 = Make("t.sword2", InventoryItemCategory.Weapon, 1);
            fixture.Helmet = Make("t.helmet", InventoryItemCategory.Helmet, 1);
            fixture.Ring = Make("t.ring", InventoryItemCategory.Ring, 1);
            fixture.Ring2 = Make("t.ring2", InventoryItemCategory.Ring, 1);
            fixture.Quest = Make("t.quest", InventoryItemCategory.Quest, 1);
            fixture.All = new[]
            {
                fixture.Potion, fixture.Ore, fixture.Arrows, fixture.Sword, fixture.Sword2,
                fixture.Helmet, fixture.Ring, fixture.Ring2, fixture.Quest
            };
            fixture.StackOne = new HashSet<string>(fixture.All.Where(d => d.MaxStack == 1).Select(d => d.itemId));

            fixture.Root = new GameObject(rootName);
            fixture.Model = fixture.Root.AddComponent<InventoryModel>();
            return fixture;
        }

        private static void SeedStartingBag(InventoryModel model, Fixture fixture)
        {
            model.AddItem(fixture.Potion, 250);
            model.AddItem(fixture.Ore, 130);
            model.AddItem(fixture.Arrows, 45);
            model.AddItem(fixture.Sword, 3);
            model.AddItem(fixture.Sword2, 2);
            model.AddItem(fixture.Helmet, 2);
            model.AddItem(fixture.Ring, 4);
            model.AddItem(fixture.Quest, 1);
        }

        // ------------------------------------------------------------------ fuzz

        [MenuItem("Dark Realms/Inventory/Run Model Fuzz Test (50k ops)")]
        public static void RunFuzzTest()
        {
            Report.Clear();
            failures = 0;
            currentReportName = "InventoryModelFuzzReport.txt";

            int[] seeds = { 20261014, 777, 31337, 5, 987654321 };
            const int opsPerSeed = 10000;
            int totalOps = 0;

            var fixture = CreateFixture("__InventoryFuzz__");
            try
            {
                List<InventoryItemDefinition> all = fixture.All.ToList();
                var equipSlots = (EquipmentSlotId[])Enum.GetValues(typeof(EquipmentSlotId));

                foreach (int seed in seeds)
                {
                    var rng = new System.Random(seed);
                    string seedLabel = seed.ToString();

                    SeedStartingBag(fixture.Model, fixture);

                    Dictionary<string, int> totals = Totals(fixture.Model);
                    Dictionary<string, int> occupied = OccupiedSlots(fixture.Model);
                    var history = new List<string>();
                    int events = 0;
                    Action onChanged = () => events++;
                    fixture.Model.InventoryChanged += onChanged;
                    fixture.Model.EquipmentChanged += onChanged;

                    for (int op = 0; op < opsPerSeed; op++)
                    {
                        totalOps++;
                        int totalBefore = fixture.Model.TotalItemCount;
                        string layoutBefore = Layout(fixture.Model);
                        int eventsBefore = events;

                        int pick = rng.Next(0, 100);
                        InventoryItemDefinition item = all[rng.Next(all.Count)];
                        int bagIndex = rng.Next(0, InventoryModel.InventorySlotCount);
                        var equipSlot = equipSlots[rng.Next(equipSlots.Length)];
                        var equipSlot2 = equipSlots[rng.Next(equipSlots.Length)];
                        int amount = rng.Next(-2, 130);

                        string opName;
                        bool expectDelta = false;
                        int declaredDelta = 0;
                        bool expectExactNoChange = false;

                        if (pick < 14)
                        {
                            int accepted = fixture.Model.AddItem(item, amount);
                            opName = $"AddItem({item.itemId},{amount}) accepted={accepted}";
                            expectDelta = true;
                            declaredDelta = accepted;
                        }
                        else if (pick < 24)
                        {
                            int removeAmount = rng.Next(0, 40);
                            bool ok = fixture.Model.RemoveFromInventory(bagIndex, removeAmount);
                            opName = $"RemoveFromInventory({bagIndex},{removeAmount}) ok={ok}";
                            if (!ok) expectExactNoChange = true;
                            else { expectDelta = true; declaredDelta = -removeAmount; }
                        }
                        else if (pick < 36)
                        {
                            int to = rng.Next(0, InventoryModel.InventorySlotCount);
                            bool ok = fixture.Model.MoveInventorySlots(bagIndex, to);
                            opName = $"MoveInventorySlots({bagIndex},{to}) ok={ok}";
                            if (!ok) expectExactNoChange = true;
                        }
                        else if (pick < 48)
                        {
                            bool ok = fixture.Model.TryEquipInventoryItem(bagIndex, equipSlot);
                            opName = $"TryEquipInventoryItem({bagIndex},{equipSlot}) ok={ok}";
                            if (!ok) expectExactNoChange = true;
                        }
                        else if (pick < 56)
                        {
                            bool ok = fixture.Model.TryUnequipToInventory(equipSlot, bagIndex);
                            opName = $"TryUnequipToInventory({equipSlot},{bagIndex}) ok={ok}";
                            if (!ok) expectExactNoChange = true;
                        }
                        else if (pick < 62)
                        {
                            bool ok = fixture.Model.TryMoveEquipment(equipSlot, equipSlot2);
                            opName = $"TryMoveEquipment({equipSlot},{equipSlot2}) ok={ok}";
                            if (!ok) expectExactNoChange = true;
                        }
                        else if (pick < 70)
                        {
                            bool ok = fixture.Model.TryQuickEquip(bagIndex, out EquipmentSlotId used, out bool replaced);
                            opName = $"TryQuickEquip({bagIndex}) ok={ok} slot={used} replaced={replaced}";
                            if (!ok) expectExactNoChange = true;
                        }
                        else if (pick < 76)
                        {
                            bool ok = fixture.Model.TryQuickUnequip(equipSlot, out int destination);
                            opName = $"TryQuickUnequip({equipSlot}) ok={ok} dest={destination}";
                            if (!ok) expectExactNoChange = true;
                        }
                        else if (pick < 84)
                        {
                            int max = fixture.Model.MaxSplitAmount(bagIndex);
                            bool ok = fixture.Model.TrySplitStack(bagIndex, amount, out int newIndex);
                            opName = $"TrySplitStack({bagIndex},{amount}) max={max} ok={ok} new={newIndex}";
                            if (!ok) expectExactNoChange = true;
                            else if (amount < 1 || amount > max)
                                Failure(seedLabel, op, opName, "accepted a split outside 1..MaxSplitAmount", layoutBefore, Layout(fixture.Model));
                        }
                        else if (pick < 88)
                        {
                            int merges = fixture.Model.AutoStackInventory();
                            opName = $"AutoStackInventory() merges={merges}";
                        }
                        else if (pick < 91)
                        {
                            bool changed = fixture.Model.SortInventory();
                            opName = $"SortInventory() changed={changed}";
                        }
                        else if (pick < 94)
                        {
                            fixture.Model.SelectWeaponSlot(rng.Next(-2, 6));
                            opName = $"SelectWeaponSlot(active={fixture.Model.ActiveWeaponIndex})";
                        }
                        else if (pick < 96)
                        {
                            int accepted = fixture.Model.AddItem(item, 1);
                            int acceptedAgain = fixture.Model.AddItem(item, 1);
                            opName = $"AddItem x2 duplicate trigger accepted={accepted}/{acceptedAgain}";
                            expectDelta = true;
                            declaredDelta = accepted + acceptedAgain;
                        }
                        else if (pick < 98)
                        {
                            int first = fixture.Model.AutoStackInventory();
                            int second = fixture.Model.AutoStackInventory();
                            opName = $"AutoStack x2 idempotence merges={first}/{second}";
                            if (second != 0)
                                Failure(seedLabel, op, opName, "AutoStack is not idempotent: the second pass still merged", layoutBefore, Layout(fixture.Model));
                        }
                        else
                        {
                            bool first = fixture.Model.SortInventory();
                            bool second = fixture.Model.SortInventory();
                            opName = $"Sort x2 idempotence changed={first}/{second}";
                            if (second)
                                Failure(seedLabel, op, opName, "Sort is not idempotent: the second pass still changed the layout", layoutBefore, Layout(fixture.Model));
                        }

                        history.Add(opName);
                        if (history.Count > 24) history.RemoveAt(0);

                        int totalAfter = fixture.Model.TotalItemCount;
                        string layoutAfter = Layout(fixture.Model);

                        if (expectExactNoChange)
                        {
                            if (layoutAfter != layoutBefore)
                                Failure(seedLabel, op, opName, "a refused operation changed the layout", layoutBefore, layoutAfter);
                            else if (events != eventsBefore)
                                Failure(seedLabel, op, opName, "a refused operation still raised a change event",
                                    layoutBefore + $" events={eventsBefore}", layoutAfter + $" events={events}");
                        }
                        else if (expectDelta)
                        {
                            if (totalAfter - totalBefore != declaredDelta)
                                Failure(seedLabel, op, opName,
                                    $"item total delta {totalAfter - totalBefore} != declared {declaredDelta}",
                                    layoutBefore, layoutAfter);
                        }
                        else if (totalAfter != totalBefore)
                        {
                            Failure(seedLabel, op, opName, $"conservation violated: total {totalBefore} -> {totalAfter}",
                                layoutBefore, layoutAfter);
                        }

                        if (layoutAfter != layoutBefore && events == eventsBefore && !expectExactNoChange)
                            Failure(seedLabel, op, opName, "layout changed without raising a change event", layoutBefore, layoutAfter);

                        if (!expectDelta)
                        {
                            Dictionary<string, int> nowTotals = Totals(fixture.Model);
                            if (!SameMap(totals, nowTotals))
                                Failure(seedLabel, op, opName,
                                    $"per-item totals changed: {Dump(totals)} -> {Dump(nowTotals)}", layoutBefore, layoutAfter);
                            totals = nowTotals;
                        }
                        else
                        {
                            totals = Totals(fixture.Model);
                        }

                        Dictionary<string, int> nowOccupied = OccupiedSlots(fixture.Model);
                        if (!expectDelta)
                        {
                            foreach (KeyValuePair<string, int> pair in occupied)
                            {
                                if (!fixture.StackOne.Contains(pair.Key)) continue;
                                if (nowOccupied.TryGetValue(pair.Key, out int now) && now < pair.Value)
                                    Failure(seedLabel, op, opName,
                                        $"single-item stacks were merged: {Dump(occupied)} -> {Dump(nowOccupied)}",
                                        layoutBefore, layoutAfter);
                            }
                        }
                        occupied = nowOccupied;

                        string violation = StructuralViolations(fixture.Model);
                        if (violation != null)
                        {
                            Failure(seedLabel, op, opName, violation, layoutBefore, layoutAfter);
                            Line("    last operations: " + string.Join(" | ", history));
                            break;
                        }

                        if (failures > 6) break;
                    }

                    fixture.Model.InventoryChanged -= onChanged;
                    fixture.Model.EquipmentChanged -= onChanged;

                    Line($"seed {seedLabel}: {opsPerSeed} operations, failures so far={failures}, totals={Dump(totals)}");
                    if (failures > 6) break;
                }
            }
           finally
            {
                fixture.Dispose();
            }

            Line($"TOTAL OPERATIONS={totalOps} across {seeds.Length} seeds");
            Line(failures == 0
                ? "RESULT: PASS - no invariant violation, no duplicated item, no lost item"
                : $"RESULT: FAIL - {failures} violation(s)");
            Flush();
        }

        // ------------------------------------------------------------------ boundary table

        [MenuItem("Dark Realms/Inventory/Run Model Boundary Tests")]
        public static void RunBoundaryTests()
        {
            Report.Clear();
            failures = 0;
            currentReportName = "InventoryModelBoundaryReport.txt";
            Line("=== boundary table ===");

            var fixture = CreateFixture("__InventoryBoundary__");
            try
            {
                InventoryModel model = fixture.Model;

                Check("an empty bag reports zero items", model.TotalItemCount == 0, model.TotalItemCount.ToString());
                Check("an empty bag reports no used slot", model.UsedInventorySlots == 0, model.UsedInventorySlots.ToString());
                Check("first empty slot of an empty bag is 0", model.FirstEmptyInventoryIndex() == 0);
                Check("invalid index returns no slot", model.GetInventorySlot(-1) == null && model.GetInventorySlot(54) == null);
                Check("invalid index cannot be removed from", !model.RemoveFromInventory(-1, 1) && !model.RemoveFromInventory(54, 1));
                Check("invalid index cannot be moved", !model.MoveInventorySlots(-1, 0) && !model.MoveInventorySlots(0, 54));
                Check("invalid index cannot be split", !model.TrySplitStack(54, 1, out _));
                Check("invalid index cannot be equipped", !model.TryEquipInventoryItem(-1, EquipmentSlotId.Weapon1));
                Check("MaxSplitAmount on an invalid index is 0", model.MaxSplitAmount(-1) == 0 && model.MaxSplitAmount(54) == 0);

                Check("adding zero items changes nothing", model.AddItem(fixture.Ore, 0) == 0 && model.TotalItemCount == 0);
                Check("adding a negative amount changes nothing", model.AddItem(fixture.Ore, -50) == 0 && model.TotalItemCount == 0);
                Check("adding a null item changes nothing", model.AddItem(null, 10) == 0 && model.TotalItemCount == 0);
                Check("removing zero items is refused", !model.RemoveFromInventory(0, 0));

                // 1 / maxStack-1 / maxStack / maxStack+1 around a 20-cap material.
                model.AddItem(fixture.Arrows, 1);
                Check("one item lands as a single stack of 1", model.GetInventorySlot(0)?.count == 1,
                    model.GetInventorySlot(0)?.count.ToString() ?? "null");
                Check("splitting a stack of 1 is refused", model.MaxSplitAmount(0) == 0 && !model.TrySplitStack(0, 1, out _));

                model.AddItem(fixture.Arrows, 19);
                Check("a 20-cap stack fills to exactly 20", model.GetInventorySlot(0)?.count == 20,
                    model.GetInventorySlot(0)?.count.ToString() ?? "null");
                model.AddItem(fixture.Arrows, 1);
                Check("one past the cap opens a second stack", model.GetInventorySlot(1)?.count == 1,
                    model.GetInventorySlot(1)?.count.ToString() ?? "null");

                // 20 in slot 0, 1 in slot 1: a 21 item transfer fills slot 1 to its cap of 20 and opens a third stack.
                int accepted = model.AddItem(fixture.Arrows, 21);
                Check("a transfer larger than one stack fills the partial stack first and opens a new one",
                    accepted == 21 && model.GetInventorySlot(1)?.count == 20 && model.GetInventorySlot(2)?.count == 2,
                    accepted + " slots=" + Describe(model, 0, 3));
                Check("no stack went over its cap after the spread",
                    model.GetInventorySlot(0)?.count == 20 && model.CountInInventory(fixture.Arrows) == 42,
                    "slot0=" + (model.GetInventorySlot(0)?.count.ToString() ?? "null") +
                    " total=" + model.CountInInventory(fixture.Arrows));

                Check("MaxSplitAmount is count-1 and never empties the source",
                    model.MaxSplitAmount(1) == 19 && model.TrySplitStack(1, 19, out int splitIndex) &&
                    model.GetInventorySlot(1)?.count == 1 && model.GetInventorySlot(splitIndex)?.count == 19,
                    "max=" + model.MaxSplitAmount(1));
                Check("a split preserves the item total", model.CountInInventory(fixture.Arrows) == 42,
                    model.CountInInventory(fixture.Arrows).ToString());
                Check("splitting the whole stack would be a move, so it is refused", model.MaxSplitAmount(1) == 0);
                Check("splitting zero is refused", !model.TrySplitStack(1, 0, out _));
                Check("splitting a negative amount is refused", !model.TrySplitStack(1, -3, out _));
                Check("splitting a stack of 1 is refused", !model.TrySplitStack(1, 1, out _));

                var fullBag = CreateFixture("__InventoryFullBag__");
                try
                {
                    int packed = fullBag.Model.AddItem(fullBag.Ore, 54 * 100);
                    bool allFull = true;
                    for (int i = 0; i < InventoryModel.InventorySlotCount; i++)
                        allFull &= fullBag.Model.GetInventorySlot(i)?.count == 100;
                    Check("a full bag holds exactly 54 stacks of 100", packed == 5400 && allFull, packed.ToString());
                    Check("a full bag refuses more items without losing them",
                        fullBag.Model.AddItem(fullBag.Ore, 100) == 0 && fullBag.Model.TotalItemCount == 5400,
                        fullBag.Model.TotalItemCount.ToString());
                    Check("a full bag reports no empty slot", fullBag.Model.FirstEmptyInventoryIndex() == -1);
                    Check("splitting inside a full bag is refused", fullBag.Model.MaxSplitAmount(0) == 0);

                    int oversized = fullBag.Model.AddItem(fullBag.Ore, 999999);
                    Check("an absurd amount is accepted only up to capacity and never duplicated",
                        oversized == 0 && fullBag.Model.TotalItemCount == 5400, oversized + "/" + fullBag.Model.TotalItemCount);
                }
                finally
                {
                    fullBag.Dispose();
                }

                // Displacing a DIFFERENT worn item when the bag has no room at all: the whole equip must roll back.
                // The incoming item has to be stackable for this to be reachable at all, because equipping a
                // one-per-slot item frees the bag slot it came from and the displaced item always fits there.
                var rollback = CreateFixture("__InventoryRollback__");
                try
                {
                    InventoryModel bag = rollback.Model;
                    rollback.Sword2.maxStack = 5;

                    bag.AddItem(rollback.Sword, 1);
                    int wornIndex = FindSlot(bag, rollback.Sword);
                    Check("sword worn while the bag still has room",
                        bag.TryEquipInventoryItem(wornIndex, EquipmentSlotId.Weapon1) &&
                        bag.GetEquipmentSlot(EquipmentSlotId.Weapon1)?.item == rollback.Sword);

                    bag.AddItem(rollback.Sword2, 5);
                    bag.AddItem(rollback.Ore, 53 * 100);
                    int incomingIndex = FindSlot(bag, rollback.Sword2);
                    int beforeTotal = bag.TotalItemCount;
                    string beforeLayout = Layout(bag);
                    InventoryStack incoming = bag.GetInventorySlot(incomingIndex);

                    Check("the bag is full and the incoming stack is untouched before the attempt",
                        bag.FirstEmptyInventoryIndex() == -1 && incoming != null && incoming.count == 5,
                        "emptySlot=" + bag.FirstEmptyInventoryIndex() + " incomingCount=" + (incoming?.count.ToString() ?? "null"));

                    Check("equipping over a different worn item with a full bag is refused",
                        !bag.TryEquipInventoryItem(incomingIndex, EquipmentSlotId.Weapon1));
                    Check("the refusal rolled back completely: layout and total unchanged",
                        Layout(bag) == beforeLayout && bag.TotalItemCount == beforeTotal,
                        beforeTotal + " -> " + bag.TotalItemCount);
                    Check("the worn sword survived the refused equip",
                        bag.GetEquipmentSlot(EquipmentSlotId.Weapon1)?.item == rollback.Sword,
                        bag.GetEquipmentSlot(EquipmentSlotId.Weapon1)?.item?.itemId ?? "empty");
                    Check("the incoming stack is exactly as it was",
                        bag.GetInventorySlot(incomingIndex)?.count == 5 && bag.CountInInventory(rollback.Sword2) == 5,
                        bag.GetInventorySlot(incomingIndex)?.count.ToString() ?? "null");
                    Check("no item was duplicated or lost by the refused equip",
                        bag.CountInInventory(rollback.Sword) == 0 && bag.CountEquipped(rollback.Sword) == 1,
                        "bag=" + bag.CountInInventory(rollback.Sword) + " worn=" + bag.CountEquipped(rollback.Sword));
                }
                finally
                {
                    rollback.Dispose();
                }

                // Equipment rules: wrong category, wrong slot, displaced item, ring pair behaviour.
                var equipFixture = CreateFixture("__InventoryEquip__");
                try
                {
                    InventoryModel equip = equipFixture.Model;
                    equip.AddItem(equipFixture.Helmet, 1);
                    int helmetIndex = FindSlot(equip, equipFixture.Helmet);
                    Check("a helmet is refused by a weapon slot", !equip.TryEquipInventoryItem(helmetIndex, EquipmentSlotId.Weapon1));
                    Check("a potion is not equippable at all", !equip.AddItem(equipFixture.Potion, 1).Equals(0) &&
                        !equip.TryQuickEquip(FindSlot(equip, equipFixture.Potion), out _, out _));

                    Check("a helmet fits the helmet slot", equip.TryEquipInventoryItem(helmetIndex, EquipmentSlotId.Helmet));
                    Check("the helmet is in the slot and out of the bag",
                        equip.GetEquipmentSlot(EquipmentSlotId.Helmet)?.item == equipFixture.Helmet &&
                        equip.CountInInventory(equipFixture.Helmet) == 0);

                    Check("equipping the same item twice needs a second copy", !equip.TryEquipInventoryItem(helmetIndex, EquipmentSlotId.Chestplate));

                    equip.AddItem(equipFixture.Ring, 2);
                    int ringIndex = FindSlot(equip, equipFixture.Ring);
                    Check("the first ring prefers the empty left slot",
                        equip.TryQuickEquip(ringIndex, out EquipmentSlotId firstRing, out bool replacedFirst) &&
                        firstRing == EquipmentSlotId.RingLeft && !replacedFirst, firstRing.ToString());

                    int secondRingIndex = FindSlot(equip, equipFixture.Ring);
                    Check("the second ring takes the other empty slot instead of replacing the worn one",
                        equip.TryQuickEquip(secondRingIndex, out EquipmentSlotId secondRing, out bool replacedSecond) &&
                        secondRing == EquipmentSlotId.RingRight && !replacedSecond, secondRing.ToString());
                    Check("both rings are worn and none is left in the bag",
                        equip.GetEquipmentSlot(EquipmentSlotId.RingLeft)?.item == equipFixture.Ring &&
                        equip.GetEquipmentSlot(EquipmentSlotId.RingRight)?.item == equipFixture.Ring &&
                        equip.CountInInventory(equipFixture.Ring) == 0);

                    equip.AddItem(equipFixture.Ring2, 1);
                    int ring2Index = FindSlot(equip, equipFixture.Ring2);
                    Check("a third ring reports that it replaced something",
                        equip.TryQuickEquip(ring2Index, out EquipmentSlotId thirdRing, out bool replacedThird) &&
                        thirdRing == EquipmentSlotId.RingLeft && replacedThird, thirdRing + "/replaced=" + replacedThird);
                    Check("the replaced ring went back to the bag, it was not deleted",
                        equip.CountInInventory(equipFixture.Ring) == 1, equip.CountInInventory(equipFixture.Ring).ToString());

                    int beforeUnequip = equip.TotalItemCount;
                    Check("unequipping returns the ring to the bag",
                        equip.TryQuickUnequip(EquipmentSlotId.RingRight, out int unequipIndex) && unequipIndex >= 0 &&
                        equip.TotalItemCount == beforeUnequip, equip.TotalItemCount.ToString());
                    Check("unequipping an empty slot is refused", !equip.TryQuickUnequip(EquipmentSlotId.RingRight, out _));

                    // Equipping over an item into a bag with no room at all must roll back completely.
                    var tight = CreateFixture("__InventoryTight__");
                    try
                    {
                        tight.Model.AddItem(tight.Ore, 54 * 100);
                        Check("the tight bag is full", tight.Model.FirstEmptyInventoryIndex() == -1);
                        Check("displacing an equipped item into a full bag fails and rolls back",
                            !tight.Model.TryEquipInventoryItem(0, EquipmentSlotId.Weapon1) &&
                            tight.Model.GetEquipmentSlot(EquipmentSlotId.Weapon1) == null &&
                            tight.Model.TotalItemCount == 5400, tight.Model.TotalItemCount.ToString());
                    }
                    finally
                    {
                        tight.Dispose();
                    }
                }
                finally
                {
                    equipFixture.Dispose();
                }
            }
            catch (Exception ex)
            {
                failures++;
                Line("  [EXCEPTION] " + ex);
            }
            finally
            {
                fixture.Dispose();
            }

            Line(failures == 0 ? "RESULT: PASS - all boundary checks passed" : $"RESULT: FAIL - {failures} failed");
            Flush();
        }

        private static void Check(string label, bool ok, string detail = "")
        {
            if (ok)
            {
                Line("  [PASS] " + label + (string.IsNullOrEmpty(detail) ? string.Empty : " :: " + detail));
                return;
            }

            failures++;
            Line("  [FAIL] " + label + (string.IsNullOrEmpty(detail) ? string.Empty : " :: " + detail));
        }

        private static string Describe(InventoryModel model, int from, int count)
        {
            var parts = new List<string>();
            for (int i = from; i < from + count && i < InventoryModel.InventorySlotCount; i++)
            {
                InventoryStack stack = model.GetInventorySlot(i);
                parts.Add(i + ":" + (stack == null || stack.IsEmpty ? "-" : stack.count.ToString()));
            }
            return string.Join(",", parts);
        }

        private static int FindSlot(InventoryModel model, InventoryItemDefinition item)
        {
            for (int i = 0; i < InventoryModel.InventorySlotCount; i++)
            {
                InventoryStack stack = model.GetInventorySlot(i);
                if (stack != null && stack.item == item) return i;
            }
            return -1;
        }

        // ------------------------------------------------------------------ item id integrity

        /// <summary>
        /// The model identifies items by asset reference, and the rest of the project keys saved data and weapon
        /// lookups by <c>itemId</c>. Two different assets sharing an id would therefore be a real data defect, and
        /// this reports it instead of letting it surface later as a wrong item being consumed.
        /// </summary>
        [MenuItem("Dark Realms/Inventory/Validate Item Definitions")]
        public static void ValidateItemDefinitions()
        {
            string[] guids = AssetDatabase.FindAssets("t:InventoryItemDefinition");
            var byId = new Dictionary<string, List<string>>();
            var problems = new List<string>();

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                InventoryItemDefinition item = AssetDatabase.LoadAssetAtPath<InventoryItemDefinition>(path);
                if (item == null) continue;

                if (string.IsNullOrWhiteSpace(item.itemId))
                {
                    problems.Add($"{path}: itemId is empty");
                    continue;
                }

                if (!byId.TryGetValue(item.itemId, out List<string> paths))
                {
                    paths = new List<string>();
                    byId[item.itemId] = paths;
                }
                paths.Add(path);

                if (item.category == InventoryItemCategory.Weapon && item.weapon == null)
                    problems.Add($"{path}: weapon item without a WeaponDefinition (it will fall back to unarmed)");
            }

            foreach (KeyValuePair<string, List<string>> pair in byId)
            {
                if (pair.Value.Count > 1)
                    problems.Add($"duplicate itemId '{pair.Key}' on {pair.Value.Count} assets: {string.Join(", ", pair.Value)}");
            }

            string summary = $"[DarkRealms Inventory] Validated {guids.Length} item definition(s): " +
                             (problems.Count == 0 ? "no duplicate ids, no empty ids." : problems.Count + " problem(s).");
            if (problems.Count == 0)
                Debug.Log(summary);
            else
                Debug.LogWarning(summary + "\n- " + string.Join("\n- ", problems));
        }
    }
}
#endif

#if UNITY_EDITOR
using System;
using DarkRealms.Inventory;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace DarkRealms.EditorTools
{
    public static class InventoryModuleInstaller
    {
        private const string DefinitionsFolder = "Assets/Game/Items/Definitions";

        [MenuItem("Dark Realms/Inventory/Install Inventory System")]
        public static void InstallInventorySystem()
        {
            InventoryModel model = UnityEngine.Object.FindAnyObjectByType<InventoryModel>();
            InventoryUIController ui = UnityEngine.Object.FindAnyObjectByType<InventoryUIController>();

            if (model == null)
            {
                GameObject root = new GameObject("DarkRealms_InventorySystem");
                Undo.RegisterCreatedObjectUndo(root, "Install Dark Realms Inventory");
                model = Undo.AddComponent<InventoryModel>(root);
            }

            if (ui == null)
            {
                GameObject uiRoot = model.GetComponent<InventoryUIController>() != null
                    ? model.gameObject
                    : new GameObject("DarkRealms_InventoryUI");

                if (uiRoot != model.gameObject)
                {
                    Undo.RegisterCreatedObjectUndo(uiRoot, "Create Dark Realms Inventory UI");
                    ui = Undo.AddComponent<InventoryUIController>(uiRoot);
                }
                else
                {
                    ui = Undo.AddComponent<InventoryUIController>(uiRoot);
                }
            }

            SerializedObject serializedUI = new SerializedObject(ui);
            SerializedProperty modelProperty = serializedUI.FindProperty("model");
            if (modelProperty != null)
            {
                modelProperty.objectReferenceValue = model;
                serializedUI.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorSceneManager.MarkSceneDirty(model.gameObject.scene);
            if (ui.gameObject.scene.IsValid() && ui.gameObject.scene != model.gameObject.scene)
                EditorSceneManager.MarkSceneDirty(ui.gameObject.scene);
            Selection.activeGameObject = ui.gameObject;
            Debug.Log("[DarkRealms Inventory] Installed. Press Play, then I or Tab to open the inventory. Use the optional Seed Demo Inventory menu to populate test items.");
        }

        [MenuItem("Dark Realms/Inventory/Seed Demo Inventory (Optional)")]
        public static void SeedDemoInventory()
        {
            InventoryModel model = UnityEngine.Object.FindAnyObjectByType<InventoryModel>();
            if (model == null)
            {
                EditorUtility.DisplayDialog("Inventory not installed", "Run Dark Realms > Inventory > Install Inventory System first.", "OK");
                return;
            }

            EnsureFolder("Assets/Game/Items");
            EnsureFolder(DefinitionsFolder);

            InventoryItemDefinition potion = GetOrCreateItem("Demo_HealthPotion.asset", "demo.health_potion", "Health Potion", InventoryItemCategory.Consumable, 100);
            InventoryItemDefinition ore = GetOrCreateItem("Demo_IronOre.asset", "demo.iron_ore", "Iron Ore", InventoryItemCategory.Material, 100);
            InventoryItemDefinition sword = GetOrCreateItem("Demo_RustySword.asset", "demo.rusty_sword", "Rusty Sword", InventoryItemCategory.Weapon, 1);
            InventoryItemDefinition helmet = GetOrCreateItem("Demo_IronHelmet.asset", "demo.iron_helmet", "Iron Helmet", InventoryItemCategory.Helmet, 1);
            InventoryItemDefinition ring = GetOrCreateItem("Demo_CopperRing.asset", "demo.copper_ring", "Copper Ring", InventoryItemCategory.Ring, 1);
            AssetDatabase.SaveAssets();

            int inserted = 0;
            inserted += model.AddItem(sword, 1);
            if (model.GetInventorySlot(0) != null && model.GetInventorySlot(0).item == sword)
                model.TryEquipInventoryItem(0, EquipmentSlotId.Weapon1);

            inserted += model.AddItem(potion, 35);
            inserted += model.AddItem(ore, 160);
            inserted += model.AddItem(helmet, 1);
            inserted += model.AddItem(ring, 2);

            // Demo gear is equipped only if its item actually landed in the expected slot; otherwise it stays in the bag.
            for (int i = 0; i < InventoryModel.InventorySlotCount; i++)
            {
                InventoryStack stack = model.GetInventorySlot(i);
                if (stack == null || stack.IsEmpty) continue;
                if (stack.item == helmet) model.TryEquipInventoryItem(i, EquipmentSlotId.Helmet);
                else if (stack.item == ring) model.TryEquipInventoryItem(i, EquipmentSlotId.RingLeft);
            }

            EditorSceneManager.MarkSceneDirty(model.gameObject.scene);
            EditorUtility.SetDirty(model);
            Debug.Log($"[DarkRealms Inventory] Demo inventory seeded. Accepted item count: {inserted}. Save the scene to keep this test inventory.");
        }

        [MenuItem("Dark Realms/Inventory/Run Model Self-Test")]
        public static void RunModelSelfTest()
        {
            GameObject root = new GameObject("__InventoryModelSelfTest__");
            GameObject fullBagRoot = new GameObject("__InventoryFullBagSelfTest__");
            InventoryItemDefinition potion = ScriptableObject.CreateInstance<InventoryItemDefinition>();
            InventoryItemDefinition sword = ScriptableObject.CreateInstance<InventoryItemDefinition>();
            InventoryItemDefinition helmet = ScriptableObject.CreateInstance<InventoryItemDefinition>();
            bool passed = true;
            string failure = string.Empty;

            try
            {
                potion.itemId = "test.potion";
                potion.displayName = "Test Potion";
                potion.category = InventoryItemCategory.Consumable;
                potion.maxStack = 100;

                sword.itemId = "test.sword";
                sword.displayName = "Test Sword";
                sword.category = InventoryItemCategory.Weapon;
                sword.maxStack = 1;

                helmet.itemId = "test.helmet";
                helmet.displayName = "Test Helmet";
                helmet.category = InventoryItemCategory.Helmet;
                helmet.maxStack = 1;

                if (InventoryModel.Columns != 6 || InventoryModel.Rows != 9 || InventoryModel.InventorySlotCount != 54)
                    throw new InvalidOperationException("Grid dimensions test failed: expected exactly 6 columns × 9 rows (54 slots).");

                InventoryModel fullBag = fullBagRoot.AddComponent<InventoryModel>();
                int capacityAccepted = fullBag.AddItem(potion, 5600);
                bool capacityCorrect = capacityAccepted == 54 * 100;
                for (int i = 0; i < InventoryModel.InventorySlotCount; i++)
                    capacityCorrect &= fullBag.GetInventorySlot(i) != null && fullBag.GetInventorySlot(i).count == 100;
                if (!capacityCorrect || fullBag.AddItem(potion, 100) != 0)
                    throw new InvalidOperationException("Capacity test failed: bag should contain 54 stacks of 100 and reject overflow without losing items.");

                InventoryModel model = root.AddComponent<InventoryModel>();
                int accepted = model.AddItem(potion, 150);
                bool stackingCorrect = accepted == 150 && model.GetInventorySlot(0)?.count == 100 && model.GetInventorySlot(1)?.count == 50;
                if (!stackingCorrect) throw new InvalidOperationException("Stack test failed: 150 items should split into stacks 100 + 50.");

                int swordAdded = model.AddItem(sword, 1);
                int swordIndex = FindItemSlot(model, sword);
                bool equipWorks = swordAdded == 1 && swordIndex >= 0 && model.TryEquipInventoryItem(swordIndex, EquipmentSlotId.Weapon1)
                                  && model.GetEquipmentSlot(EquipmentSlotId.Weapon1)?.item == sword;
                if (!equipWorks) throw new InvalidOperationException("Weapon equip test failed.");

                model.AddItem(helmet, 1);
                int helmetIndex = FindItemSlot(model, helmet);
                bool rejectWrongSlot = helmetIndex >= 0 && !model.TryEquipInventoryItem(helmetIndex, EquipmentSlotId.Weapon2);
                if (!rejectWrongSlot) throw new InvalidOperationException("Slot validation test failed: helmet must not fit a weapon slot.");

                model.SelectWeaponSlot(1);
                bool weaponSelectionWorks = model.ActiveWeaponIndex == 1;
                if (!weaponSelectionWorks) throw new InvalidOperationException("Weapon selection test failed.");

                Debug.Log("[DarkRealms Inventory Self-Test] PASS: 100-item stack cap, 6x9 slot model, weapon equip, wrong-slot rejection and weapon slot switching.");
            }
            catch (Exception ex)
            {
                passed = false;
                failure = ex.Message;
                Debug.LogError("[DarkRealms Inventory Self-Test] FAIL: " + ex);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
                UnityEngine.Object.DestroyImmediate(fullBagRoot);
                UnityEngine.Object.DestroyImmediate(potion);
                UnityEngine.Object.DestroyImmediate(sword);
                UnityEngine.Object.DestroyImmediate(helmet);
            }

            EditorUtility.DisplayDialog("Inventory Model Self-Test", passed ? "PASS\nSee the Console for details." : "FAIL\n" + failure, "OK");
        }

        private static int FindItemSlot(InventoryModel model, InventoryItemDefinition definition)
        {
            for (int i = 0; i < InventoryModel.InventorySlotCount; i++)
            {
                InventoryStack stack = model.GetInventorySlot(i);
                if (stack != null && stack.item == definition) return i;
            }
            return -1;
        }

        private static InventoryItemDefinition GetOrCreateItem(string fileName, string id, string label,
            InventoryItemCategory category, int maxStack)
        {
            string path = DefinitionsFolder + "/" + fileName;
            InventoryItemDefinition item = AssetDatabase.LoadAssetAtPath<InventoryItemDefinition>(path);
            if (item == null)
            {
                item = ScriptableObject.CreateInstance<InventoryItemDefinition>();
                AssetDatabase.CreateAsset(item, path);
            }
            item.itemId = id;
            item.displayName = label;
            item.description = "Demo item generated by the Dark Realms inventory module.";
            item.category = category;
            item.maxStack = maxStack;
            EditorUtility.SetDirty(item);
            return item;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = System.IO.Path.GetDirectoryName(path)?.Replace('\\', '/');
            string folder = System.IO.Path.GetFileName(path);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(parent)) EnsureFolder(parent);
            if (!string.IsNullOrEmpty(parent) && !AssetDatabase.IsValidFolder(path)) AssetDatabase.CreateFolder(parent, folder);
        }
    }
}
#endif

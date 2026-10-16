using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;
using UnityEngine.UI;
using DarkRealms.CameraSystem;
using DarkRealms.Core;
using DarkRealms.Enemies;
using DarkRealms.Environment;
using DarkRealms.Player;
using DarkRealms.UI;

namespace DarkRealms.EditorTools
{
    /// <summary>
    /// Builds the Dark Realms side-view test scene from scratch: layered ruin environment,
    /// knight player, wraith test enemy, bounded follow camera, 2D lighting and HUD.
    /// Writes to a NEW scene so no existing scene is overwritten.
    /// </summary>
    public static class SceneBuilder
    {
        private const string ScenePath = "Assets/Game/Scenes/DarkRealms_Proto_01.unity";
        private const string ArtRoot = "Assets/Game/Art";
        private const string PrefabRoot = "Assets/Game/Prefabs";
        private const float PixelPerUnit = 32f;

        // ------------------------------------------------------------------ level data

        // (xMin, xMax, topY, depth) - solid ground blocks. The gap between 18 and 22 is a pit.
        private static readonly Vector4[] GroundSegments =
        {
            new(-10f, 18f, 0f, 8f),
            new(22f, 44f, 0f, 8f),
            new(44f, 62f, 3f, 10f),
        };

        // (centreX, topY, halfWidth) - floating stone platforms.
        private static readonly Vector3[] Platforms =
        {
            new(10f, 4.5f, 2.5f),
            new(28f, 5.5f, 2f),
            new(52f, 6.5f, 2.5f),
        };

        internal struct Clip
        {
            public string name;
            public Sprite[] frames;
            public float fps;
            public bool loop;
        }

        // ================================================================== entry point

        [MenuItem("Dark Realms/Build Test Scene")]
        public static void Run()
        {
            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);

            var root = new GameObject("DarkRealms_Proto_01").transform;

            var managers = Group("--- MANAGERS ---", root);
            var spawn = CreateMarker("PlayerSpawn", managers, new Vector3(-4f, 0.2f, 0f));
            var gameManager = CreateGameManager(managers, spawn.transform);
            _ = gameManager;

            var camera = BuildCamera(root, scene);
            var environment = Group("--- ENVIRONMENT ---", root);
            BuildLighting(environment);
            BuildBackdrop(environment, camera);
            BuildTerrain(environment);
            BuildProps(environment);
            BuildForeground(environment);

            var gameplay = Group("--- GAMEPLAY ---", root);
            var player = BuildPlayer(gameplay);
            var enemies = BuildEnemies(gameplay, player.transform);

            WireCamera(camera, player);
            WireHud(managers.transform, player.GetComponent<PlayerHealth>(), player.GetComponent<PlayerController>(), player.GetComponent<PlayerInputReader>());

            BuildHitEffectPrefab(player.GetComponent<PlayerCombat>());

            _ = enemies;

            EditorSceneManager.MarkSceneDirty(scene);
            EditorSceneManager.SaveScene(scene, ScenePath);
            RegisterSceneInBuildSettings();

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"[DarkRealms] Test scene built and saved to {ScenePath}.");
        }

        /// <summary>Registers an arbitrary scene in Build Settings, used by the prototype map builder so a
        /// prototype map is reachable by scene path (which is what the transition service loads).</summary>
        internal static void RegisterSceneInBuildSettings(string path)
        {
            var list = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach (var entry in list)
            {
                if (entry.path == path)
                {
                    entry.enabled = true;
                    EditorBuildSettings.scenes = list.ToArray();
                    return;
                }
            }

            list.Insert(0, new EditorBuildSettingsScene(path, true));
            EditorBuildSettings.scenes = list.ToArray();
        }

        private static void RegisterSceneInBuildSettings()
        {
            var scenes = new List<EditorBuildSettingsScene>(EditorBuildSettings.scenes);
            foreach (var s in scenes)
            {
                if (s.path == ScenePath)
                    return;
            }

            scenes.Insert(0, new EditorBuildSettingsScene(ScenePath, true));
            EditorBuildSettings.scenes = scenes.ToArray();
        }

        // ================================================================== managers / camera

        internal static GameObject CreateGameManager(Transform parent, Transform spawn)
        {
            var go = new GameObject("GameManager");
            go.transform.SetParent(parent, false);
            var gm = go.AddComponent<GameManager>();
            SetObject(gm, "defaultSpawnPoint", spawn);
            return go;
        }

        internal static Camera BuildCamera(Transform root, UnityEngine.SceneManagement.Scene scene)
        {
            var group = Group("--- CAMERA ---", root);

            var go = new GameObject("Main Camera");
            go.tag = "MainCamera";
            go.transform.SetParent(group, false);
            go.transform.position = new Vector3(-4f, 3f, -10f);

            var cam = go.AddComponent<Camera>();
            cam.orthographic = true;
            cam.orthographicSize = 6.5f;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = new Color(0.025f, 0.03f, 0.055f, 1f);
            cam.nearClipPlane = 0.1f;
            cam.farClipPlane = 100f;
            cam.depth = -1;

            go.AddComponent<AudioListener>();

            if (go.GetComponent<UniversalAdditionalCameraData>() == null)
                go.AddComponent<UniversalAdditionalCameraData>();

            return cam;
        }

        internal static void WireCamera(Camera cam, GameObject player)
        {
            var follow = cam.gameObject.AddComponent<CameraFollow>();
            SetObject(follow, "target", player.transform);
            SetVector2(follow, "offset", new Vector2(0f, 1.6f));
            SetFloat(follow, "smoothTime", 0.16f);
            SetFloat(follow, "maxSpeed", 60f);
            SetObject(follow, "facingSource", player.GetComponent<PlayerController>());
            SetFloat(follow, "lookAheadDistance", 1.8f);
            SetBool(follow, "useBounds", true);
            SetVector2(follow, "boundsMin", new Vector2(-10f, -4f));
            SetVector2(follow, "boundsMax", new Vector2(62f, 24f));
        }

        // ================================================================== lighting / backdrop

        internal static void BuildLighting(Transform parent)
        {
            var go = new GameObject("Global Light 2D");
            go.transform.SetParent(parent, false);

            var light = go.AddComponent<Light2D>();
            light.lightType = Light2D.LightType.Global;
            light.color = new Color(0.92f, 0.95f, 1f, 1f);
            light.intensity = 1.05f;
        }

        private static void BuildBackdrop(Transform parent, Camera camera)
        {
            var backdrop = Group("Backdrop", parent);

            // Sky is parented to the camera so it always fills the view.
            var sky = MakeSprite("Sky", camera.transform, Sprite_("Environments/sky_night"), "Sky", -100, new Vector3(0f, 0f, 30f), 1f);
            sky.transform.localScale = new Vector3(14f, 14f, 1f);

            // Distant ranges sit low so the ground layer occludes their mass and only the
            // peaks rise above the horizon. X is scaled far wider than Y so one sprite
            // spans the whole level without also filling the screen vertically.
            CreateParallax("Parallax_Far_Mountains", backdrop, "Environments/parallax_mountains_far", "ParallaxFar", -30,
                new Vector2(26f, -5f), new Vector2(5.5f, 2f), 0.06f);
            CreateParallax("Parallax_Far_Castle", backdrop, "Environments/parallax_castle_far", "ParallaxFar", -28,
                new Vector2(26f, -4f), new Vector2(5.5f, 2f), 0.10f);
            CreateParallax("Parallax_Mid_Forest", backdrop, "Environments/parallax_forest_mid", "ParallaxMid", -20,
                new Vector2(26f, -4f), new Vector2(5.5f, 2f), 0.18f);
            CreateParallax("Parallax_Near_Hills", backdrop, "Environments/parallax_hills_near", "ParallaxNear", -10,
                new Vector2(26f, -6f), new Vector2(5.5f, 2.2f), 0.30f);
            CreateParallax("Parallax_Near_RuinWall", backdrop, "Environments/ruin_wall", "ParallaxNear", -8,
                new Vector2(26f, -10f), new Vector2(6f, 1.6f), 0.42f);
        }

        private static void CreateParallax(string name, Transform parent, string spritePath, string sortingLayer, int order, Vector2 position, Vector2 scale, float factor)
        {
            var go = MakeSprite(name, parent, Sprite_(spritePath), sortingLayer, order, position, 1f);
            go.transform.localScale = new Vector3(scale.x, scale.y, 1f);

            var parallax = go.AddComponent<ParallaxLayer>();
            SetFloat(parallax, "parallaxFactor", factor);
            SetFloat(parallax, "verticalFactor", factor * 0.5f);
            SetBool(parallax, "followVertical", true);
        }

        // ================================================================== terrain

        private static void BuildTerrain(Transform parent)
        {
            var terrain = Group("Terrain", parent);

            foreach (var seg in GroundSegments)
                MakeGroundSegment($"Ground_{seg.x:F0}_{seg.y:F0}", terrain.transform, seg.x, seg.y, seg.z, seg.w);

            foreach (var plat in Platforms)
                MakePlatform($"Platform_{plat.x:F0}", terrain.transform, plat.x, plat.y, plat.z);

            // Kill plane beneath the pit so falling triggers death + respawn.
            var killZone = new GameObject("KillZone");
            killZone.transform.SetParent(terrain.transform, false);
            killZone.transform.position = new Vector3(26f, -14f, 0f);
            var collider = killZone.AddComponent<BoxCollider2D>();
            collider.size = new Vector2(90f, 4f);
            collider.isTrigger = true;
            killZone.AddComponent<HazardZone>();
        }

        internal static void MakeGroundSegment(string name, Transform parent, float xMin, float xMax, float topY, float depth)
        {
            float width = xMax - xMin;
            float centreX = (xMin + xMax) * 0.5f;

            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.position = new Vector3(centreX, topY, 0f);
            root.layer = ProjectBootstrap.LayerIndex("Ground");

            var collider = root.AddComponent<BoxCollider2D>();
            collider.size = new Vector2(width, depth + 2f);
            collider.offset = new Vector2(0f, -(depth + 2f) * 0.5f);

            var fill = MakeTiled("Fill", root.transform, Sprite_("Environments/ground_fill"), "Ground", 0,
                new Vector2(centreX, topY - 2f - depth * 0.5f), new Vector2(width, depth));
            fill.layer = ProjectBootstrap.LayerIndex("Ground");

            var top = MakeTiled("Top", root.transform, Sprite_("Environments/ground_top"), "Ground", 1,
                new Vector2(centreX, topY), new Vector2(width, 2f));
            top.layer = ProjectBootstrap.LayerIndex("Ground");
        }

        internal static void MakePlatform(string name, Transform parent, float centreX, float topY, float halfWidth)
        {
            var root = new GameObject(name);
            root.transform.SetParent(parent, false);
            root.transform.position = new Vector3(centreX, topY, 0f);
            root.layer = ProjectBootstrap.LayerIndex("Ground");

            var collider = root.AddComponent<BoxCollider2D>();
            collider.size = new Vector2(halfWidth * 2f, 0.5f);
            collider.offset = new Vector2(0f, -0.25f);

            var visual = MakeTiled("Slab", root.transform, Sprite_("Environments/platform_stone"), "Environment", 5,
                new Vector2(centreX, topY), new Vector2(halfWidth * 2f, 1.5f));
            visual.layer = ProjectBootstrap.LayerIndex("Ground");
        }

        private static void BuildProps(Transform parent)
        {
            var props = Group("Props", parent);

            // Dead trees along the ground line.
            float[] treeX = { -8f, -1f, 5f, 24f, 33f, 50f, 57f, 60f };
            foreach (float x in treeX)
            {
                string path = x > 20f ? "Props/dead_tree" : "Props/dead_tree_small";
                float scale = x > 20f ? 1f : 0.85f;
                MakeSprite($"DeadTree_{x:F0}", props.transform, Sprite_(path), "Environment", 0, new Vector2(x, 0f), scale);
            }

            MakeSprite("Arch", props.transform, Sprite_("Props/stone_arch"), "Environment", 1, new Vector2(11f, -0.4f), 1.2f);
            MakeSprite("RuinTower", props.transform, Sprite_("Props/ruin_tower"), "Environment", 0, new Vector2(59f, 0f), 1.4f);
            MakeSprite("Pillar_A", props.transform, Sprite_("Props/broken_pillar"), "Environment", 2, new Vector2(26f, 0f), 1f);
            MakeSprite("Pillar_B", props.transform, Sprite_("Props/broken_pillar"), "Environment", 2, new Vector2(46f, 3f), 1f);
            MakeSprite("Rubble_A", props.transform, Sprite_("Props/rubble_pile"), "Environment", 2, new Vector2(2f, 0f), 1f);
            MakeSprite("Rubble_B", props.transform, Sprite_("Props/rubble_pile"), "Environment", 2, new Vector2(37f, 0f), 1.1f);
            MakeSprite("Roots_A", props.transform, Sprite_("Props/twisted_roots"), "Environment", 2, new Vector2(-5f, 0f), 1.2f);
            MakeSprite("Roots_B", props.transform, Sprite_("Props/twisted_roots"), "Environment", 2, new Vector2(30f, 0f), 1.1f);

            MakeSprite("Bush_A", props.transform, Sprite_("Props/dead_bush"), "Environment", 3, new Vector2(-3f, 0f), 1f);
            MakeSprite("Bush_B", props.transform, Sprite_("Props/dead_bush"), "Environment", 3, new Vector2(7f, 0f), 0.9f);
            MakeSprite("Bush_C", props.transform, Sprite_("Props/dead_bush"), "Environment", 3, new Vector2(36f, 0f), 1.1f);
            MakeSprite("Bush_D", props.transform, Sprite_("Props/dead_bush"), "Environment", 3, new Vector2(55f, 3f), 1f);

            // Crates with collision.
            MakeSolidProp("Crate_A", props.transform, "Props/old_crate", new Vector2(8f, 0f), 1f, new Vector2(1.4f, 1.4f), new Vector2(0f, 0.7f));
            MakeSolidProp("Crate_B", props.transform, "Props/old_crate", new Vector2(53f, 3f), 1f, new Vector2(1.4f, 1.4f), new Vector2(0f, 0.7f));

            // Torches with warm point lights.
            float[] torchX = { 0f, 16f, 29f, 41f, 56f };
            foreach (float x in torchX)
            {
                float baseY = x > 43f ? 3f : 0f;
                var torch = MakeSprite($"Torch_{x:F0}", props.transform, Sprite_("Props/torch_post"), "Environment", 4, new Vector2(x, baseY), 1f);
                AddTorchLight(torch.transform, new Vector3(0f, 2.4f, 0f));
            }
        }

        private static void MakeSolidProp(string name, Transform parent, string spritePath, Vector2 position, float scale, Vector2 size, Vector2 offset)
        {
            var go = MakeSprite(name, parent, Sprite_(spritePath), "Environment", 3, position, scale);
            go.layer = ProjectBootstrap.LayerIndex("Ground");
            var collider = go.AddComponent<BoxCollider2D>();
            collider.size = size;
            collider.offset = offset;
        }

        private static void AddTorchLight(Transform parent, Vector3 localPosition)
        {
            var go = new GameObject("TorchLight");
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;

            var light = go.AddComponent<Light2D>();
            light.lightType = Light2D.LightType.Point;
            light.color = new Color(1f, 0.72f, 0.42f, 1f);
            light.intensity = 1.1f;
            light.pointLightInnerRadius = 0.5f;
            light.pointLightOuterRadius = 5f;

            var flicker = go.AddComponent<LightFlicker>();
            SetObject(flicker, "targetLight", light);
            SetFloat(flicker, "baseIntensity", 1.1f);
            SetFloat(flicker, "amplitude", 0.3f);
        }

        private static void BuildForeground(Transform parent)
        {
            var fg = Group("Foreground", parent);

            // Foreground rock band: wide but short so it frames the bottom of the view
            // instead of covering the playable area.
            var go = MakeSprite("Foreground_Rocks", fg.transform, Sprite_("Environments/foreground_rocks"), "Foreground", 100, new Vector2(26f, -6f), 1f);
            go.transform.localScale = new Vector3(5.5f, 0.9f, 1f);
            var parallax = go.AddComponent<ParallaxLayer>();
            SetFloat(parallax, "parallaxFactor", 1.15f);
            SetBool(parallax, "followVertical", false);

            // Soft atmospheric fog band floating in the mid layer.
            var fog = MakeSprite("Fog_Mid", fg.transform, Sprite_("Effects/fog_band"), "ParallaxMid", 5, new Vector2(26f, 2f), 1f);
            fog.transform.localScale = new Vector3(4f, 0.8f, 1f);
            var fogParallax = fog.AddComponent<ParallaxLayer>();
            SetFloat(fogParallax, "parallaxFactor", 0.35f);
        }

        // ================================================================== player

        internal static GameObject BuildPlayer(Transform parent)
        {
            var go = new GameObject("Player");
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(-4f, 0.2f, 0f);
            go.tag = "Player";
            go.layer = ProjectBootstrap.LayerIndex("Player");

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = Sprite_("Characters/player_idle_00");
            renderer.sortingLayerName = "Characters";
            renderer.sortingOrder = 10;

            var animator = go.AddComponent<FrameAnimator>();
            SetObject(animator, "target", renderer);
            SetString(animator, "defaultClip", "Idle");
            SetClips(animator, PlayerClips());

            var input = go.AddComponent<PlayerInputReader>();
            SetObject(input, "actions", InputActions_());
            SetString(input, "actionMapName", "Player");

            var body = go.AddComponent<Rigidbody2D>();
            body.gravityScale = 4f;
            body.freezeRotation = true;
            body.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            body.interpolation = RigidbodyInterpolation2D.Interpolate;

            var collider = go.AddComponent<CapsuleCollider2D>();
            collider.direction = CapsuleDirection2D.Vertical;
            collider.size = new Vector2(0.8f, 1.8f);
            collider.offset = new Vector2(0f, 0.9f);

            var groundCheck = CreateMarker("GroundCheck", go.transform, new Vector3(0f, 0.05f, 0f)).transform;

            var controller = go.AddComponent<PlayerController>();
            SetObject(controller, "input", input);
            SetObject(controller, "groundCheck", groundCheck);
            SetInt(controller, "groundMask", ProjectBootstrap.LayerMaskFor("Ground"));

            var combat = go.AddComponent<PlayerCombat>();
            SetObject(combat, "input", input);
            SetVector2(combat, "hitboxSize", new Vector2(1.4f, 1.1f));
            SetVector2(combat, "hitboxOffset", new Vector2(0.8f, 0.05f));
            SetInt(combat, "targetMask", ProjectBootstrap.LayerMaskFor("Enemy"));
            SetFloat(combat, "damage", 25f);
            SetFloat(combat, "knockbackForce", 6f);
            SetFloat(combat, "attackDuration", 0.34f);
            SetFloat(combat, "activeWindowStart", 0.08f);
            SetFloat(combat, "activeWindowEnd", 0.22f);
            SetFloat(combat, "cooldown", 0.12f);

            var health = go.AddComponent<PlayerHealth>();
            SetFloat(health, "maxHealth", 100f);
            SetFloat(health, "invulnerabilityTime", 0.7f);
            SetBool(health, "destroyOnDeath", false);
            SetObject(health, "controller", controller);
            SetFloat(health, "respawnDelay", 1.6f);

            var visuals = go.AddComponent<PlayerVisuals>();
            SetObject(visuals, "controller", controller);
            SetObject(visuals, "combat", combat);
            SetObject(visuals, "spriteRenderer", renderer);

            return go;
        }

        internal static Clip[] PlayerClips()
        {
            return new[]
            {
                C("Idle", "Characters/player_idle", 4, 7f),
                C("Walk", "Characters/player_walk", 6, 10f),
                C("Run", "Characters/player_run", 6, 13f),
                C("Jump", "Characters/player_jump", 1, 1f, false),
                C("Fall", "Characters/player_fall", 1, 1f, false),
                C("Land", "Characters/player_land", 1, 1f, false),
                C("Attack", "Characters/player_attack", 4, 13f, false),
                C("Hurt", "Characters/player_hurt", 1, 1f, false),
                C("Death", "Characters/player_death", 4, 6f, false),
            };
        }

        internal static Clip[] EnemyClips()
        {
            return new[]
            {
                C("Move", "Characters/enemy_move", 4, 7f),
                C("Idle", "Characters/enemy_idle", 4, 5f),
                C("Attack", "Characters/enemy_attack", 4, 10f, false),
                C("Hurt", "Characters/enemy_hurt", 1, 1f, false),
                C("Death", "Characters/enemy_death", 4, 6f, false),
            };
        }

        // ================================================================== enemies

        internal static GameObject BuildEnemies(Transform parent, Transform player)
        {
            var group = Group("Enemies", parent);
            var container = new GameObject("Enemies");
            container.transform.SetParent(group.transform, false);

            BuildEnemy("Wraith_A", container.transform, new Vector3(8f, 0.05f, 0f), player, 3.5f);
            BuildEnemy("Wraith_B", container.transform, new Vector3(30f, 0.05f, 0f), player, 2.5f);

            return container;
        }

        internal static void BuildEnemy(string name, Transform parent, Vector3 position, Transform player, float patrolRadius)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            go.layer = ProjectBootstrap.LayerIndex("Enemy");

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = Sprite_("Characters/enemy_idle_00");
            renderer.sortingLayerName = "Characters";
            renderer.sortingOrder = 8;

            var animator = go.AddComponent<FrameAnimator>();
            SetObject(animator, "target", renderer);
            SetString(animator, "defaultClip", "Move");
            SetClips(animator, EnemyClips());

            var health = go.AddComponent<Health>();
            SetFloat(health, "maxHealth", 60f);
            SetFloat(health, "invulnerabilityTime", 0f);
            SetBool(health, "destroyOnDeath", false);

            var body = go.AddComponent<Rigidbody2D>();
            body.gravityScale = 4f;
            body.freezeRotation = true;
            body.interpolation = RigidbodyInterpolation2D.Interpolate;

            var collider = go.AddComponent<BoxCollider2D>();
            collider.size = new Vector2(0.85f, 1.6f);
            collider.offset = new Vector2(0f, 0.8f);

            var controller = go.AddComponent<EnemyController>();
            SetObject(controller, "target", player);
            SetInt(controller, "targetMask", ProjectBootstrap.LayerMaskFor("Player"));
            SetInt(controller, "groundMask", ProjectBootstrap.LayerMaskFor("Ground"));
            SetObject(controller, "groundCheck", CreateMarker("GroundCheck", go.transform, new Vector3(0f, 0.05f, 0f)).transform);
            SetFloat(controller, "aggroRadius", 9f);
            SetFloat(controller, "attackRange", 1.5f);
            SetFloat(controller, "patrolSpeed", 1.8f);
            SetFloat(controller, "chaseSpeed", 3.8f);
            SetFloat(controller, "patrolRadius", patrolRadius);
            SetFloat(controller, "damage", 12f);
            SetFloat(controller, "attackCooldown", 1.2f);
            SetFloat(controller, "windup", 0.35f);
            SetFloat(controller, "reviveDelay", 3f);

            var visuals = go.AddComponent<EnemyVisuals>();
            SetObject(visuals, "controller", controller);
        }

        // ================================================================== combat effects

        private static void BuildHitEffectPrefab(PlayerCombat combat)
        {
            var temp = new GameObject("HitSpark");
            var renderer = temp.AddComponent<SpriteRenderer>();
            renderer.sprite = Sprite_("Effects/hit_spark");
            renderer.sortingLayerName = "Effects";
            renderer.sortingOrder = 100;
            temp.AddComponent<SelfDestruct>();

            string path = $"{PrefabRoot}/Combat/HitSpark.prefab";
            EnsureFolder(PrefabRoot + "/Combat");
            var prefab = PrefabUtility.SaveAsPrefabAsset(temp, path);
            Object.DestroyImmediate(temp);

            SetObject(combat, "hitEffectPrefab", prefab);
        }

        // ================================================================== HUD

        private static void WireHud(Transform parent, PlayerHealth health, PlayerController controller, PlayerInputReader input)
        {
            var canvasGo = new GameObject("HUD Canvas");
            canvasGo.transform.SetParent(parent, false);

            var canvas = canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = 100;

            var scaler = canvasGo.AddComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920f, 1080f);
            scaler.matchWidthOrHeight = 0.5f;

            canvasGo.AddComponent<GraphicRaycaster>();

            // Full-screen vignette.
            var vignette = CreateUiImage("Vignette", canvasGo.transform, Sprite_("UI/vignette"), Color.white);
            Stretch(vignette.rectTransform);

            // Damage flash overlay.
            var damage = CreateUiImage("DamageVignette", canvasGo.transform, Sprite_("Effects/light_glow"), new Color(0.65f, 0.06f, 0.08f, 0f));
            Stretch(damage.rectTransform);

            // Health bar background.
            var barBg = CreateUiImage("HealthBarBg", canvasGo.transform, null, new Color(0.05f, 0.05f, 0.07f, 0.85f));
            Place(barBg.rectTransform, new Vector2(0f, 0f), new Vector2(0f, 0f), new Vector2(48f, 56f), new Vector2(420f, 34f));

            var barBorder = CreateUiImage("HealthBarBorder", barBg.rectTransform, null, new Color(0.45f, 0.38f, 0.26f, 0.9f));
            StretchWithPadding(barBorder.rectTransform, 3f);

            var fill = CreateUiImage("HealthBarFill", barBg.rectTransform, null, new Color(0.62f, 0.1f, 0.13f, 1f));
            StretchWithPadding(fill.rectTransform, 6f);
            fill.type = Image.Type.Filled;
            fill.fillMethod = Image.FillMethod.Horizontal;
            fill.fillOrigin = 0;
            fill.fillAmount = 1f;

            var healthText = CreateUiText("HealthText", barBg.rectTransform, "100 / 100", TextAnchor.MiddleCenter, 22, Color.white);
            Stretch(healthText.rectTransform);

            var stateText = CreateUiText("StateText", canvasGo.transform, "State: Idle", TextAnchor.UpperLeft, 24, new Color(0.85f, 0.87f, 0.95f));
            Place(stateText.rectTransform, new Vector2(0f, 1f), new Vector2(0f, 1f), new Vector2(56f, -52f), new Vector2(560f, 40f));

            var title = CreateUiText("Title", canvasGo.transform, "DARK REALMS - prototype vertical slice", TextAnchor.UpperCenter, 26, new Color(0.7f, 0.72f, 0.85f, 0.85f));
            Place(title.rectTransform, new Vector2(0.5f, 1f), new Vector2(0.5f, 1f), new Vector2(0f, -44f), new Vector2(900f, 40f));

            var hint = CreateUiText("Hint", canvasGo.transform,
                "A / D or Arrows move    Shift run    Space jump    LMB / Enter attack    Esc pause",
                TextAnchor.LowerCenter, 20, new Color(0.68f, 0.7f, 0.8f, 0.75f));
            Place(hint.rectTransform, new Vector2(0.5f, 0f), new Vector2(0.5f, 0f), new Vector2(0f, 26f), new Vector2(1100f, 32f));

            // Pause panel.
            var pausePanel = CreateUiImage("PausePanel", canvasGo.transform, null, new Color(0.02f, 0.02f, 0.04f, 0.82f));
            Stretch(pausePanel.rectTransform);
            var pauseText = CreateUiText("PauseText", pausePanel.rectTransform, "PAUSED\n\nPress Esc to resume", TextAnchor.MiddleCenter, 48, Color.white);
            Stretch(pauseText.rectTransform);
            pausePanel.gameObject.SetActive(false);

            // HUD controller.
            var hud = canvasGo.AddComponent<HUDController>();
            SetObject(hud, "playerHealth", health);
            SetObject(hud, "playerController", controller);
            SetObject(hud, "healthFill", fill);
            SetObject(hud, "healthText", healthText);
            SetObject(hud, "stateText", stateText);
            SetObject(hud, "damageVignette", damage);

            var pause = canvasGo.AddComponent<PauseMenu>();
            SetObject(pause, "pausePanel", pausePanel.gameObject);
            SetObject(pause, "inputToDisable", input);
        }

        // ================================================================== helpers

        internal static Transform Group(string name, Transform parent)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go.transform;
        }

        internal static GameObject CreateMarker(string name, Transform parent, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = position;
            return go;
        }

        internal static GameObject MakeSprite(string name, Transform parent, Sprite sprite, string sortingLayer, int order, Vector2 position, float scale)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(position.x, position.y, 0f);
            go.transform.localScale = Vector3.one * scale;

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.sortingLayerName = sortingLayer;
            renderer.sortingOrder = order;

            return go;
        }

        internal static GameObject MakeTiled(string name, Transform parent, Sprite sprite, string sortingLayer, int order, Vector2 position, Vector2 size)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = new Vector3(position.x, position.y, 0f);

            var renderer = go.AddComponent<SpriteRenderer>();
            renderer.sprite = sprite;
            renderer.drawMode = SpriteDrawMode.Tiled;
            renderer.tileMode = SpriteTileMode.Continuous;
            renderer.size = size;
            renderer.sortingLayerName = sortingLayer;
            renderer.sortingOrder = order;

            return go;
        }

        private static Image CreateUiImage(string name, Transform parent, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var image = go.AddComponent<Image>();
            image.sprite = sprite;
            image.color = color;
            image.raycastTarget = false;

            return image;
        }

        private static Text CreateUiText(string name, Transform parent, string content, TextAnchor anchor, int fontSize, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform));
            go.transform.SetParent(parent, false);

            var text = go.AddComponent<Text>();
            text.text = content;
            text.alignment = anchor;
            text.fontSize = fontSize;
            text.color = color;
            text.raycastTarget = false;
            text.font = BuiltinFont();
            text.horizontalOverflow = HorizontalWrapMode.Overflow;
            text.verticalOverflow = VerticalWrapMode.Overflow;

            return text;
        }

        private static Font BuiltinFont()
        {
            var font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
            if (font == null)
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            return font;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static void StretchWithPadding(RectTransform rect, float padding)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(padding, padding);
            rect.offsetMax = new Vector2(-padding, -padding);
        }

        private static void Place(RectTransform rect, Vector2 anchorMin, Vector2 anchorMax, Vector2 anchoredPosition, Vector2 size)
        {
            rect.anchorMin = anchorMin;
            rect.anchorMax = anchorMax;
            rect.pivot = anchorMin;
            rect.sizeDelta = size;
            rect.anchoredPosition = anchoredPosition;
        }

        internal static Clip C(string name, string basePath, int frameCount, float fps, bool loop = true)
        {
            var clip = new Clip
            {
                name = name,
                fps = fps,
                loop = loop,
                frames = new Sprite[frameCount],
            };

            if (frameCount == 1)
            {
                clip.frames[0] = Sprite_(basePath);
            }
            else
            {
                for (int i = 0; i < frameCount; i++)
                    clip.frames[i] = Sprite_($"{basePath}_{i:00}");
            }

            return clip;
        }

        internal static Sprite Sprite_(string relativePath)
        {
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>($"{ArtRoot}/{relativePath}.png");
            if (sprite == null)
                Debug.LogError($"[SceneBuilder] Missing sprite '{ArtRoot}/{relativePath}.png'");
            return sprite;
        }

        internal static InputActionAsset InputActions_()
        {
            return AssetDatabase.LoadAssetAtPath<InputActionAsset>("Assets/Settings/InputSystem_Actions.inputactions");
        }

        internal static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;

            string parent = System.IO.Path.GetDirectoryName(folder).Replace('\\', '/');
            AssetDatabase.CreateFolder(parent, System.IO.Path.GetFileName(folder));
        }

        // ---------------------------------------------------------- serialized field helper

        internal static SerializedProperty Property(Object target, string field)
        {
            var so = new SerializedObject(target);
            var prop = so.FindProperty(field);

            if (prop == null)
            {
                Debug.LogError($"[SceneBuilder] Field '{field}' not found on {target.GetType().Name}");
                return null;
            }

            return prop;
        }

        internal static void SetFloat(Object target, string field, float value)
        {
            var prop = Property(target, field);
            if (prop == null) return;
            prop.floatValue = value;
            prop.serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        internal static void SetInt(Object target, string field, int value)
        {
            var prop = Property(target, field);
            if (prop == null) return;
            prop.intValue = value;
            prop.serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        internal static void SetBool(Object target, string field, bool value)
        {
            var prop = Property(target, field);
            if (prop == null) return;
            prop.boolValue = value;
            prop.serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        internal static void SetString(Object target, string field, string value)
        {
            var prop = Property(target, field);
            if (prop == null) return;
            prop.stringValue = value;
            prop.serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        internal static void SetObject(Object target, string field, Object value)
        {
            var prop = Property(target, field);
            if (prop == null) return;
            prop.objectReferenceValue = value;
            prop.serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        internal static void SetVector2(Object target, string field, Vector2 value)
        {
            var prop = Property(target, field);
            if (prop == null) return;
            prop.vector2Value = value;
            prop.serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        internal static void SetClips(FrameAnimator animator, Clip[] clips)
        {
            var so = new SerializedObject(animator);
            var array = so.FindProperty("clips");

            if (array == null)
            {
                Debug.LogError("[SceneBuilder] FrameAnimator.clips not found.");
                return;
            }

            array.arraySize = clips.Length;

            for (int i = 0; i < clips.Length; i++)
            {
                var element = array.GetArrayElementAtIndex(i);
                element.FindPropertyRelative("name").stringValue = clips[i].name;
                element.FindPropertyRelative("fps").floatValue = clips[i].fps;
                element.FindPropertyRelative("loop").boolValue = clips[i].loop;

                var frames = element.FindPropertyRelative("frames");
                frames.arraySize = clips[i].frames.Length;

                for (int f = 0; f < clips[i].frames.Length; f++)
                    frames.GetArrayElementAtIndex(f).objectReferenceValue = clips[i].frames[f];
            }

            so.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}

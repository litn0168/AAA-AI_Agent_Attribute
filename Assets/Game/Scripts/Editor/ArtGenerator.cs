using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace DarkRealms.EditorTools
{
    /// <summary>
    /// Deterministic procedural sprite generator for the Dark Realms prototype.
    ///
    /// AI asset generation was unavailable in this environment (the MCP model list was empty),
    /// so all artwork here is drawn programmatically into RGBA32 textures and saved as real
    /// PNG sprite assets with correct import settings. The output is intentionally stylised
    /// placeholder-grade pixel art with a cohesive dark-medieval palette, not final art.
    /// </summary>
    public static class ArtGenerator
    {
        private const int Ppu = 32;
        private const string ArtRoot = "Assets/Game/Art";

        // ---------------------------------------------------------------- palette
        private static readonly Color32 Opaque = new(255, 255, 255, 255);

        private static readonly Color32 DeepNight = new(13, 16, 25, 255);
        private static readonly Color32 NightBlue = new(26, 33, 48, 255);
        private static readonly Color32 HorizonGlow = new(74, 82, 112, 255);
        private static readonly Color32 MoonPale = new(206, 212, 230, 255);
        private static readonly Color32 FarRock = new(38, 45, 64, 255);
        private static readonly Color32 FarRockRim = new(88, 100, 132, 255);
        private static readonly Color32 MidRock = new(50, 58, 80, 255);
        private static readonly Color32 ForestDark = new(30, 37, 52, 255);

        private static readonly Color32 StoneDark = new(52, 57, 70, 255);
        private static readonly Color32 StoneMid = new(76, 83, 100, 255);
        private static readonly Color32 StoneLight = new(108, 117, 136, 255);
        private static readonly Color32 StoneHi = new(146, 157, 178, 255);
        private static readonly Color32 MossDark = new(38, 52, 38, 255);
        private static readonly Color32 MossMid = new(58, 76, 50, 255);
        private static readonly Color32 MossLight = new(84, 104, 62, 255);
        private static readonly Color32 DirtDark = new(38, 33, 29, 255);
        private static readonly Color32 DirtMid = new(58, 49, 40, 255);
        private static readonly Color32 BarkDark = new(38, 33, 32, 255);
        private static readonly Color32 BarkMid = new(64, 54, 48, 255);
        private static readonly Color32 BarkLight = new(94, 80, 68, 255);

        private static readonly Color32 SteelDark = new(56, 62, 74, 255);
        private static readonly Color32 SteelMid = new(104, 114, 130, 255);
        private static readonly Color32 SteelLight = new(166, 178, 194, 255);
        private static readonly Color32 ClothDark = new(72, 24, 30, 255);
        private static readonly Color32 ClothMid = new(122, 40, 48, 255);
        private static readonly Color32 ClothLight = new(168, 66, 70, 255);
        private static readonly Color32 Skin = new(198, 168, 142, 255);

        private static readonly Color32 WraithDark = new(32, 29, 44, 255);
        private static readonly Color32 WraithMid = new(58, 54, 80, 255);
        private static readonly Color32 WraithGlow = new(126, 118, 186, 255);
        private static readonly Color32 WraithGlowSoft = new(126, 118, 186, 90);
        private static readonly Color32 TorchFlame = new(236, 156, 74, 255);
        private static readonly Color32 TorchCore = new(255, 226, 158, 255);

        // ---------------------------------------------------------------- entry point

        public static void Run()
        {
            EnsureFolders();

            int count = 0;
            count += GenerateSkyAndBackdrops();
            count += GenerateEnvironment();
            count += GenerateCharacters();
            count += GenerateEffects();

            AssetDatabase.Refresh();
            Debug.Log($"[DarkRealms] Art generation complete. {count} sprite textures written to {ArtRoot}.");
        }

        private static void EnsureFolders()
        {
            string[] folders =
            {
                ArtRoot,
                ArtRoot + "/Characters",
                ArtRoot + "/Environments",
                ArtRoot + "/Props",
                ArtRoot + "/Effects",
            };

            foreach (string folder in folders)
            {
                if (AssetDatabase.IsValidFolder(folder))
                    continue;

                string parent = Path.GetDirectoryName(folder).Replace('\\', '/');
                AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
            }
        }

        // ---------------------------------------------------------------- generation groups

        private static int GenerateSkyAndBackdrops()
        {
            int n = 0;

            SaveSprite(Sky(512, 256), "Environments/sky_night", new Vector2(0.5f, 0.5f), FilterMode.Bilinear); n++;
            SaveSprite(MountainRidge(768, 256, 1337, FarRock, FarRockRim, 0.34f), "Environments/parallax_mountains_far", new Vector2(0.5f, 0f)); n++;
            SaveSprite(CastleSilhouette(768, 256, 4242), "Environments/parallax_castle_far", new Vector2(0.5f, 0f)); n++;
            SaveSprite(ForestRidge(768, 256, 909, ForestDark), "Environments/parallax_forest_mid", new Vector2(0.5f, 0f)); n++;
            SaveSprite(MountainRidge(768, 224, 777, MidRock, new Color32(52, 60, 82, 255), 0.5f), "Environments/parallax_hills_near", new Vector2(0.5f, 0f)); n++;
            SaveSprite(RuinWall(512, 320, 2024), "Environments/ruin_wall", new Vector2(0.5f, 0f)); n++;
            SaveSprite(FogBand(512, 128), "Effects/fog_band", new Vector2(0.5f, 0.5f), FilterMode.Bilinear); n++;
            SaveSprite(ForegroundRocks(512, 192, 12), "Environments/foreground_rocks", new Vector2(0.5f, 0f)); n++;

            return n;
        }

        private static int GenerateEnvironment()
        {
            int n = 0;

            SaveSprite(GroundTop(64), "Environments/ground_top", new Vector2(0.5f, 1f)); n++;
            SaveSprite(GroundFill(64), "Environments/ground_fill", new Vector2(0.5f, 0.5f)); n++;
            SaveSprite(StoneBlock(64, 11), "Environments/stone_block", new Vector2(0.5f, 0.5f)); n++;
            SaveSprite(PlatformSlab(128, 48, 31), "Environments/platform_stone", new Vector2(0.5f, 1f)); n++;
            SaveSprite(RuinTower(192, 320, 55), "Props/ruin_tower", new Vector2(0.5f, 0f)); n++;
            SaveSprite(Arch(160, 160, 77), "Props/stone_arch", new Vector2(0.5f, 0f)); n++;
            SaveSprite(BrokenPillar(48, 144, 88), "Props/broken_pillar", new Vector2(0.5f, 0f)); n++;
            SaveSprite(Rubble(96, 48, 99), "Props/rubble_pile", new Vector2(0.5f, 0f)); n++;
            SaveSprite(DeadTree(128, 176, 101), "Props/dead_tree", new Vector2(0.5f, 0f)); n++;
            SaveSprite(DeadTree(80, 112, 202), "Props/dead_tree_small", new Vector2(0.5f, 0f)); n++;
            SaveSprite(DeadBush(64, 48, 303), "Props/dead_bush", new Vector2(0.5f, 0f)); n++;
            SaveSprite(Roots(96, 40, 404), "Props/twisted_roots", new Vector2(0.5f, 0f)); n++;
            SaveSprite(Torch(32, 96), "Props/torch_post", new Vector2(0.5f, 0f)); n++;
            SaveSprite(Crate(48, 48, 505), "Props/old_crate", new Vector2(0.5f, 0f)); n++;

            return n;
        }

        private static int GenerateCharacters()
        {
            int n = 0;

            // ---- Knight (player) --------------------------------------------------
            n += SaveFrames("Characters/player_idle", IdleFrames());
            n += SaveFrames("Characters/player_walk", WalkFrames());
            n += SaveFrames("Characters/player_run", RunFrames());
            n += SaveFrames("Characters/player_jump", Single(KnightFrame(new KnightPose { legPhase = 0.7f, armPhase = -0.6f, bob = 1f, swordAngle = -35f, swordVisible = true })));
            n += SaveFrames("Characters/player_fall", Single(KnightFrame(new KnightPose { legPhase = -0.4f, armPhase = 0.5f, bob = -1f, swordAngle = 20f, swordVisible = true })));
            n += SaveFrames("Characters/player_land", Single(KnightFrame(new KnightPose { crouch = 0.85f, legPhase = 0.25f, armPhase = -0.3f, swordAngle = -10f, swordVisible = true })));
            n += SaveFrames("Characters/player_attack", AttackFrames());
            n += SaveFrames("Characters/player_hurt", Single(KnightFrame(new KnightPose { crouch = 0.4f, lean = -12f, legPhase = -0.5f, bob = -1f, swordAngle = 10f })));
            n += SaveFrames("Characters/player_death", DeathFrames());

            // ---- Wraith (enemy) ---------------------------------------------------
            n += SaveFrames("Characters/enemy_idle", WraithFrames(0f, 0f, 0f, 4));
            n += SaveFrames("Characters/enemy_move", WraithFrames(1f, 0f, 0f, 4));
            n += SaveFrames("Characters/enemy_attack", WraithAttackFrames());
            n += SaveFrames("Characters/enemy_hurt", Single(WraithFrame(new WraithPose { bob = -1.5f, lean = -10f, glow = 1f, armRaise = -0.4f })));
            n += SaveFrames("Characters/enemy_death", WraithDeathFrames());

            return n;
        }

        private static int GenerateEffects()
        {
            int n = 0;

            SaveSprite(HitSpark(24, 12, 1), "Effects/hit_spark", new Vector2(0.5f, 0.5f)); n++;
            SaveSprite(DustPuff(32, 16, 3), "Effects/dust_puff", new Vector2(0.5f, 0.5f)); n++;
            SaveSprite(SlashArc(40, 40, 0.1f), "Effects/slash_arc", new Vector2(0.5f, 0.5f)); n++;
            SaveSprite(SoftGlow(128), "Effects/light_glow", new Vector2(0.5f, 0.5f), FilterMode.Bilinear); n++;
            SaveSprite(VignetteOverlay(128), "UI/vignette", new Vector2(0.5f, 0.5f), FilterMode.Bilinear); n++;

            return n;
        }

        // ================================================================ backdrop art

        private static Painter Sky(int w, int h)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(20260901);

            for (int y = 0; y < h; y++)
            {
                float t = 1f - (float)y / (h - 1);
                Color32 c = Lerp(HorizonGlow, DeepNight, Mathf.Pow(t, 0.75f));
                for (int x = 0; x < w; x++)
                    p.Set(x, y, c);
            }

            // Moon with a soft halo.
            float mx = w * 0.72f, my = h * 0.74f, mr = h * 0.075f;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float d = Mathf.Sqrt((x - mx) * (x - mx) + (y - my) * (y - my));
                if (d < mr * 3.2f)
                {
                    float a = Mathf.Clamp01(1f - d / (mr * 3.2f));
                    p.Blend(x, y, WithA(MoonPale, (byte)(a * a * 42f)));
                }
            }

            p.FillEllipse((int)mx, (int)my, (int)mr, (int)mr, MoonPale);
            // Crater shading
            p.BlendEllipse((int)(mx - mr * 0.35f), (int)(my + mr * 0.3f), (int)(mr * 0.3f), (int)(mr * 0.3f), WithA(DeepNight, 60));

            // Stars.
            for (int i = 0; i < 220; i++)
            {
                int x = rng.Next(w);
                int y = (int)(rng.NextDouble() * h * 0.82f);
                float distToMoon = Mathf.Sqrt((x - mx) * (x - mx) + (y - my) * (y - my));
                if (distToMoon < mr * 3.5f)
                    continue;

                byte a = (byte)(60 + rng.Next(150));
                p.Blend(x, y, WithA(MoonPale, a));
            }

            return p;
        }

        private static Painter MountainRidge(int w, int h, int seed, Color32 body, Color32 rim, float roughness)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);

            int[] ridge = new int[w];
            float value = h * 0.5f;
            for (int x = 0; x < w; x++)
            {
                value += (float)(rng.NextDouble() - 0.5) * h * 0.16f * roughness;
                value += Mathf.Sin(x * 0.013f) * 1.6f;
                value = Mathf.Clamp(value, h * 0.18f, h * 0.82f);
                ridge[x] = (int)value;
            }

            // Smooth the ridge so peaks look geological, not noisy.
            for (int pass = 0; pass < 3; pass++)
            {
                int[] smoothed = new int[w];
                for (int x = 0; x < w; x++)
                {
                    int sum = 0, count = 0;
                    for (int k = -3; k <= 3; k++)
                    {
                        int xi = Mathf.Clamp(x + k, 0, w - 1);
                        sum += ridge[xi];
                        count++;
                    }
                    smoothed[x] = sum / count;
                }
                ridge = smoothed;
            }

            for (int x = 0; x < w; x++)
            {
                int top = ridge[x];
                for (int y = 0; y < top; y++)
                {
                    float depth = (float)(top - y) / Mathf.Max(1, top);
                    Color32 c = Lerp(rim, body, Mathf.Clamp01(depth * 3.2f));
                    p.Set(x, y, c);
                }
            }

            // Snow/rim light along the crest.
            for (int x = 0; x < w; x++)
            {
                p.Blend(x, ridge[x], WithA(rim, 120));
                if (rng.NextDouble() > 0.6)
                    p.Blend(x, ridge[x] - 1, WithA(rim, 70));
            }

            return p;
        }

        private static Painter CastleSilhouette(int w, int h, int seed)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);
            Color32 body = new(34, 40, 58, 255);
            Color32 rim = new(78, 90, 120, 255);

            int groundY = (int)(h * 0.30f);

            // Distant curtain wall.
            int wallTop = groundY + 44;
            p.FillRect(0, groundY, w - 1, wallTop, body);

            // Crenellations along the wall.
            for (int x = 0; x < w; x += 22)
                p.FillRect(x, wallTop, x + 12, wallTop + 12, body);

            // Main keep.
            int keepX = w / 2 - 70;
            p.FillRect(keepX, groundY, keepX + 140, groundY + 120, body);
            for (int x = keepX; x < keepX + 140; x += 20)
                p.FillRect(x, groundY + 120, x + 11, groundY + 134, body);

            // Flanking towers.
            for (int i = 0; i < 2; i++)
            {
                int tx = i == 0 ? keepX - 62 : keepX + 156;
                int towerTop = groundY + 150;
                p.FillRect(tx, groundY, tx + 46, towerTop, body);
                for (int x = tx - 4; x < tx + 50; x += 16)
                    p.FillRect(x, towerTop, x + 9, towerTop + 12, body);
                // conical roof
                for (int y = 0; y < 40; y++)
                {
                    float half = Mathf.Lerp(26f, 0f, y / 40f);
                    p.FillRect((int)(tx + 23 - half), towerTop + 12 + y, (int)(tx + 23 + half), towerTop + 12 + y, body);
                }
            }

            // Rim light along the tops so the silhouette reads against the sky.
            for (int x = 0; x < w; x++)
            {
                for (int y = h - 1; y >= 0; y--)
                {
                    if (p.Get(x, y).a > 0)
                    {
                        p.Blend(x, y, WithA(rim, 90));
                        break;
                    }
                }
            }

            // Faint lit windows.
            for (int i = 0; i < 14; i++)
            {
                int x = keepX + 10 + rng.Next(120);
                int y = groundY + 20 + rng.Next(90);
                p.Blend(x, y, WithA(TorchFlame, 190));
                p.Blend(x, y + 1, WithA(TorchFlame, 110));
            }

            return p;
        }

        private static Painter ForestRidge(int w, int h, int seed, Color32 color)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);

            int groundLine = (int)(h * 0.16f);
            p.FillRect(0, 0, w - 1, groundLine, color);

            int x = 0;
            while (x < w)
            {
                int treeH = 70 + rng.Next(110);
                int treeW = 6 + rng.Next(6);
                int baseY = groundLine;

                // Trunk
                p.FillRect(x, baseY, x + 2, baseY + treeH, color);

                // Branches - sparse dead canopy
                int branches = 6 + rng.Next(6);
                for (int b = 0; b < branches; b++)
                {
                    int by = baseY + (int)(treeH * (0.35f + 0.6f * (float)rng.NextDouble()));
                    int dir = rng.Next(2) == 0 ? -1 : 1;
                    int len = 8 + rng.Next(22);
                    for (int i = 0; i < len; i++)
                    {
                        int bx = x + 1 + dir * i;
                        int yy = by + i / 3;
                        p.FillRect(bx, yy, bx + 1, yy + 1, color);
                    }
                }

                x += treeW + 4 + rng.Next(14);
            }

            return p;
        }

        private static Painter RuinWall(int w, int h, int seed)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);

            int top = (int)(h * 0.94f);
            int[] brokenTop = new int[w];
            float v = top;
            for (int i = 0; i < w; i++)
            {
                v += (float)(rng.NextDouble() - 0.5) * 12f;
                v = Mathf.Clamp(v, h * 0.55f, h * 0.97f);
                brokenTop[i] = (int)v;
            }

            for (int x = 0; x < w; x++)
            for (int y = 0; y < brokenTop[x]; y++)
            {
                float shade = 0.5f + 0.5f * Mathf.Sin((x * 0.08f) + (y * 0.13f));
                Color32 c = Lerp(StoneDark, StoneMid, shade * 0.6f);
                p.Set(x, y, c);
            }

            // Stone courses.
            for (int row = 0; row < h; row += 22)
            {
                for (int x = 0; x < w; x++)
                    if (row < brokenTop[x])
                        p.Blend(x, row, WithA(DeepNight, 70));

                int offset = (row / 22) % 2 == 0 ? 0 : 26;
                for (int x = offset; x < w; x += 52)
                for (int y = row; y < row + 22; y++)
                    if (y < brokenTop[Mathf.Clamp(x, 0, w - 1)])
                        p.Blend(x, y, WithA(DeepNight, 55));
            }

            // Arrow slits / broken windows.
            for (int i = 0; i < 4; i++)
            {
                int wx = 60 + i * 112 + rng.Next(30);
                int wy = h - 60 - rng.Next(60);
                int wh = 46 + rng.Next(30);
                p.FillRect(wx - 4, wy - wh, wx + 12, wy, StoneDark);
                p.FillRect(wx - 1, wy - wh + 3, wx + 9, wy - 3, DeepNight);
            }

            // Moss and weathering along the base and cracks.
            for (int i = 0; i < 900; i++)
            {
                int x = rng.Next(w);
                int y = rng.Next(40);
                if (y < brokenTop[x])
                    p.Blend(x, y, WithA(rng.Next(2) == 0 ? MossDark : MossMid, (byte)(120 + rng.Next(100))));
            }

            for (int i = 0; i < 26; i++)
            {
                int cx = rng.Next(w);
                int cy = rng.Next(h - 40);
                int len = 20 + rng.Next(70);
                for (int k = 0; k < len; k++)
                {
                    int x = cx + k / 3;
                    int y = cy + k;
                    p.Blend(x, y, WithA(DeepNight, 90));
                }
            }

            // Rim light on the broken crest.
            for (int x = 0; x < w; x++)
                p.Blend(x, brokenTop[x], WithA(StoneHi, 70));

            return p;
        }

        private static Painter GroundTop(int s)
        {
            var p = new Painter(s, s);
            var rng = new System.Random(7001);

            p.FillRect(0, 0, s - 1, s - 1, DirtDark);
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float n = (float)rng.NextDouble();
                p.Set(x, y, Lerp(DirtDark, DirtMid, n * 0.45f));
            }

            // Rock speckle.
            for (int i = 0; i < 90; i++)
            {
                int x = rng.Next(s), y = rng.Next(s);
                p.Blend(x, y, WithA(StoneDark, (byte)(80 + rng.Next(80))));
            }

            // Mossy grass cap - periodic so tiles match horizontally.
            int[] capHeight = new int[s];
            for (int x = 0; x < s; x++)
                capHeight[x] = 7 + rng.Next(4);
            capHeight[0] = capHeight[s - 1] = 8;

            for (int x = 0; x < s; x++)
            {
                int top = s - 1 - capHeight[x];
                for (int y = s - 1; y >= top; y--)
                {
                    float t = (float)(y - top) / Mathf.Max(1, s - 1 - top);
                    p.Set(x, y, Lerp(MossMid, MossLight, t * 0.7f));
                }
                p.Set(x, top, MossLight);
            }

            for (int i = 0; i < 60; i++)
            {
                int x = rng.Next(s);
                int y = s - 6 - rng.Next(4);
                p.Blend(x, y, WithA(MossDark, 120));
            }

            return p;
        }

        private static Painter GroundFill(int s)
        {
            var p = new Painter(s, s);
            var rng = new System.Random(7002);

            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float n = (float)rng.NextDouble();
                float strata = Mathf.Sin(y * 0.22f) * 0.5f + 0.5f;
                p.Set(x, y, Lerp(DirtDark, DirtMid, n * 0.35f + strata * 0.25f));
            }

            for (int i = 0; i < 70; i++)
            {
                int x = rng.Next(s), y = rng.Next(s);
                int r = 1 + rng.Next(3);
                p.BlendEllipse(x, y, r, r, WithA(StoneDark, (byte)(90 + rng.Next(60))));
            }

            for (int i = 0; i < 22; i++)
            {
                int x = rng.Next(s), y = rng.Next(s);
                p.BlendEllipse(x, y, 1 + rng.Next(2), 1, WithA(DeepNight, 110));
            }

            return p;
        }

        private static Painter StoneBlock(int s, int seed)
        {
            var p = new Painter(s, s);
            var rng = new System.Random(seed);

            p.FillRoundedRect(1, 1, s - 2, s - 2, 4, StoneMid);

            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                if (p.Get(x, y).a == 0)
                    continue;
                float n = (float)rng.NextDouble();
                p.Blend(x, y, WithA(n > 0.5f ? StoneLight : StoneDark, (byte)(n * 60f)));
            }

            // Bevel: light top-left, dark bottom-right.
            for (int x = 1; x < s - 1; x++)
            {
                p.Set(x, 1, StoneLight);
                p.Set(x, s - 2, StoneDark);
            }
            for (int y = 1; y < s - 1; y++)
            {
                p.Set(1, y, StoneLight);
                p.Set(s - 2, y, StoneDark);
            }

            // Mortar cross.
            for (int x = 0; x < s; x++)
                p.Blend(x, s / 2, WithA(DeepNight, 90));
            for (int y = 0; y < s; y++)
                p.Blend(s / 2, y, WithA(DeepNight, 60));

            for (int i = 0; i < 8; i++)
            {
                int x = rng.Next(s), y = rng.Next(s);
                int len = 4 + rng.Next(10);
                for (int k = 0; k < len; k++)
                    p.Blend(x + k / 2, y + k, WithA(MossDark, 150));
            }

            return p;
        }

        private static Painter PlatformSlab(int w, int h, int seed)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);

            p.FillRoundedRect(0, 0, w - 1, h - 1, 5, StoneMid);

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (p.Get(x, y).a == 0) continue;
                float n = (float)rng.NextDouble();
                p.Blend(x, y, WithA(n > 0.5f ? StoneLight : StoneDark, (byte)(n * 55f)));
            }

            // Mossy top edge.
            for (int x = 0; x < w; x++)
            {
                int cap = 3 + rng.Next(3);
                for (int y = h - 1; y >= h - cap; y--)
                    p.Set(x, y, Lerp(MossMid, MossLight, (y - (h - cap)) / (float)cap));
            }

            // Bevel + block seams.
            for (int x = 0; x < w; x++)
                p.Set(x, h - 6, WithA(StoneLight, 140));
            for (int x = 0; x < w; x += 32)
            for (int y = 0; y < h - 6; y++)
                p.Blend(x, y, WithA(DeepNight, 70));

            for (int x = 0; x < w; x++)
                p.Blend(x, 0, WithA(DeepNight, 120));

            return p;
        }

        private static Painter RuinTower(int w, int h, int seed)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);

            int bodyW = (int)(w * 0.62f);
            int bx = (w - bodyW) / 2;

            p.FillRect(bx, 0, bx + bodyW, h - 40, StoneDark);
            for (int y = 0; y < h - 40; y++)
            for (int x = bx; x <= bx + bodyW; x++)
            {
                float n = (float)rng.NextDouble();
                float taper = 1f - 0.15f * (y / (float)h);
                p.Set(x, y, Lerp(StoneDark, StoneMid, n * 0.5f * taper));
            }

            // Crenellations.
            for (int x = bx - 6; x < bx + bodyW + 8; x += 18)
                p.FillRect(x, h - 40, x + 11, h - 22, StoneMid);

            // Machicolation band.
            p.FillRect(bx - 8, h - 58, bx + bodyW + 8, h - 44, StoneMid);
            for (int x = bx - 8; x < bx + bodyW + 8; x += 12)
                for (int y = h - 56; y < h - 46; y++)
                    p.Blend(x, y, WithA(DeepNight, 120));

            // Window + faint inner light.
            int wx = bx + bodyW / 2;
            p.FillRoundedRect(wx - 8, h - 150, 16, 30, 7, DeepNight);
            for (int y = h - 148; y < h - 122; y++)
                for (int x = wx - 5; x <= wx + 5; x++)
                    p.Blend(x, y, WithA(TorchFlame, 70));

            // Weathering, cracks and moss.
            for (int i = 0; i < 700; i++)
            {
                int x = bx + rng.Next(bodyW + 1);
                int y = rng.Next(h - 40);
                p.Blend(x, y, WithA(rng.Next(3) == 0 ? MossDark : (rng.Next(2) == 0 ? StoneLight : StoneDark), (byte)(60 + rng.Next(90))));
            }

            for (int i = 0; i < 12; i++)
            {
                int cx = bx + rng.Next(bodyW);
                int cy = rng.Next(h - 80);
                int len = 30 + rng.Next(90);
                for (int k = 0; k < len; k++)
                    p.Blend(cx + k / 4, cy + k, WithA(DeepNight, 80));
            }

            return p;
        }

        private static Painter Arch(int w, int h, int seed)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);

            int thickness = (int)(w * 0.16f);
            int spanInner = w - thickness * 2;

            // Side pillars.
            p.FillRect(0, 0, thickness, h - thickness, StoneMid);
            p.FillRect(w - thickness, 0, w - 1, h - thickness, StoneMid);

            // Impressive arch band: sample a semicircle.
            int cx = w / 2;
            int cy = h - thickness;
            int outerR = w / 2;
            int innerR = outerR - thickness;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float d = Mathf.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                if (d <= outerR && d >= innerR && y >= cy - outerR)
                    p.Set(x, y, StoneMid);
            }

            // Voussoir joints.
            for (int a = 0; a <= 180; a += 18)
            {
                float rad = a * Mathf.Deg2Rad;
                for (int r = innerR; r <= outerR; r++)
                {
                    int x = (int)(cx + Mathf.Cos(rad) * r);
                    int y = (int)(cy + Mathf.Sin(rad) * r);
                    p.Blend(x, y, WithA(DeepNight, 110));
                }
            }

            // Texture, moss at the base, rim light.
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (p.Get(x, y).a == 0) continue;
                float n = (float)rng.NextDouble();
                p.Blend(x, y, WithA(n > 0.5f ? StoneDark : StoneLight, (byte)(n * 55f)));
            }

            for (int i = 0; i < 500; i++)
            {
                int x = rng.Next(w);
                int y = rng.Next(46);
                if (p.Get(x, y).a > 0)
                    p.Blend(x, y, WithA(rng.Next(2) == 0 ? MossDark : MossMid, (byte)(120 + rng.Next(90))));
            }

            // Broken chunk out of one pillar.
            for (int y = h / 2; y < h / 2 + 26; y++)
            for (int x = 0; x < thickness - 6; x++)
                if ((x * x + (y - h / 2) * (y - h / 2)) < 90)
                    p.Set(x, y, new Color32(0, 0, 0, 0));

            // Rim light along the outer arch.
            for (int a = 0; a <= 180; a += 2)
            {
                float rad = a * Mathf.Deg2Rad;
                int x = (int)(cx + Mathf.Cos(rad) * (outerR - 1));
                int y = (int)(cy + Mathf.Sin(rad) * (outerR - 1));
                p.Blend(x, y, WithA(StoneHi, 60));
            }

            return p;
        }

        private static Painter BrokenPillar(int w, int h, int seed)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);

            int bodyW = (int)(w * 0.7f);
            int bx = (w - bodyW) / 2;
            int top = h - 6 - rng.Next(20);

            for (int x = 0; x < w; x++)
            {
                int columnTop = top - (int)(Mathf.Abs(x - w / 2f) * 0.35f) - rng.Next(3);
                for (int y = 0; y < columnTop; y++)
                {
                    float n = (float)rng.NextDouble();
                    p.Set(x, y, Lerp(StoneDark, StoneMid, n * 0.55f));
                }
                if (columnTop < h)
                    p.Set(x, columnTop, StoneHi);
            }

            // Capital + base blocks.
            p.FillRect(bx - 6, 0, bx + bodyW + 6, 10, StoneMid);
            p.FillRect(bx - 5, top - 12, bx + bodyW + 5, top - 2, StoneMid);

            // Fluting.
            for (int x = bx + 4; x < bx + bodyW; x += 9)
            for (int y = 12; y < top - 14; y++)
                p.Blend(x, y, WithA(DeepNight, 70));

            for (int i = 0; i < 260; i++)
            {
                int x = rng.Next(w);
                int y = rng.Next(20);
                if (p.Get(x, y).a > 0)
                    p.Blend(x, y, WithA(MossDark, (byte)(110 + rng.Next(90))));
            }

            return p;
        }

        private static Painter Rubble(int w, int h, int seed)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);

            for (int i = 0; i < 26; i++)
            {
                float cx = (float)rng.NextDouble() * w;
                float cy = (float)rng.NextDouble() * h * 0.7f;
                int rx = 5 + rng.Next(11);
                int ry = 4 + rng.Next(9);
                Color32 c = rng.Next(2) == 0 ? StoneDark : StoneMid;

                p.FillEllipse((int)cx, (int)cy, rx, ry, c);
                p.BlendEllipse((int)(cx - rx * 0.3f), (int)(cy + ry * 0.4f), rx / 2, ry / 2, WithA(StoneHi, 60));
            }

            for (int i = 0; i < 90; i++)
            {
                int x = rng.Next(w), y = rng.Next(h);
                if (p.Get(x, y).a > 0)
                    p.Blend(x, y, WithA(rng.Next(2) == 0 ? MossDark : DeepNight, (byte)(70 + rng.Next(90))));
            }

            return p;
        }

        private static Painter DeadTree(int w, int h, int seed)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);

            int trunkX = w / 2;
            int groundY = 4;
            int trunkTop = (int)(h * 0.62f);

            // Tapered trunk.
            for (int y = groundY; y < trunkTop; y++)
            {
                float t = (float)(y - groundY) / Mathf.Max(1, trunkTop - groundY);
                int half = Mathf.RoundToInt(Mathf.Lerp(11f, 4f, t)) + (int)(Mathf.Sin(y * 0.12f) * 1.5f);
                int wobble = (int)(Mathf.Sin(y * 0.06f) * 4f * t);
                for (int x = trunkX - half + wobble; x <= trunkX + half + wobble; x++)
                {
                    float n = (float)rng.NextDouble();
                    p.Set(x, y, Lerp(BarkDark, BarkMid, n * 0.6f));
                }
                // Light edge
                p.Blend(trunkX + half + wobble, y, WithA(BarkLight, 80));
            }

            // Roots.
            for (int i = 0; i < 5; i++)
            {
                int dir = i % 2 == 0 ? 1 : -1;
                int len = 12 + rng.Next(24);
                for (int k = 0; k < len; k++)
                {
                    int x = trunkX + dir * (6 + k);
                    int y = groundY + Mathf.RoundToInt(Mathf.Sin(k * 0.2f) * 2f);
                    p.FillEllipse(x, y, 3, 2, BarkDark);
                }
            }

            // Recursive branches.
            GrowBranch(p, rng, trunkX, trunkTop, -Mathf.PI / 2f, 34 + rng.Next(16), 5, 0);

            // Moss on the bark.
            for (int i = 0; i < 200; i++)
            {
                int x = rng.Next(w), y = rng.Next(h);
                if (p.Get(x, y).a > 0 && rng.NextDouble() > 0.6)
                    p.Blend(x, y, WithA(MossDark, (byte)(80 + rng.Next(80))));
            }

            return p;
        }

        private static void GrowBranch(Painter p, System.Random rng, float x, float y, float angle, int length, int thickness, int depth)
        {
            if (depth > 4 || thickness <= 0 || length <= 2)
                return;

            float cx = x, cy = y;
            for (int i = 0; i < length; i++)
            {
                angle += (float)(rng.NextDouble() - 0.5) * 0.22f;
                cx += Mathf.Cos(angle);
                cy += Mathf.Sin(angle);
                p.FillEllipse((int)cx, (int)cy, thickness, thickness, BarkDark);
                if (i % 3 == 0)
                    p.Blend((int)cx, (int)cy, WithA(BarkLight, 70));
            }

            int branches = 2 + rng.Next(2);
            for (int b = 0; b < branches; b++)
            {
                float spread = 0.45f + (float)rng.NextDouble() * 0.5f;
                float childAngle = angle + (b % 2 == 0 ? spread : -spread);
                GrowBranch(p, rng, cx, cy, childAngle, Mathf.RoundToInt(length * 0.68f), thickness - 1, depth + 1);
            }
        }

        private static Painter DeadBush(int w, int h, int seed)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);

            for (int i = 0; i < 22; i++)
            {
                float x = w * 0.5f;
                float y = 3f;
                float angle = -Mathf.PI / 2f + (float)(rng.NextDouble() - 0.5) * 1.8f;
                int len = 10 + rng.Next(22);
                for (int k = 0; k < len; k++)
                {
                    x += Mathf.Cos(angle);
                    y += Mathf.Sin(angle);
                    angle += (float)(rng.NextDouble() - 0.5) * 0.18f;
                    p.FillEllipse((int)x, (int)y, 1, 1, BarkDark);
                }
            }

            for (int i = 0; i < 40; i++)
            {
                int x = rng.Next(w), y = rng.Next(h);
                if (p.Get(x, y).a > 0 && rng.NextDouble() > 0.7)
                    p.Blend(x, y, WithA(MossDark, 120));
            }

            return p;
        }

        private static Painter Roots(int w, int h, int seed)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);

            for (int r = 0; r < 7; r++)
            {
                float x = rng.Next(w);
                float y = 2;
                float angle = (float)(rng.NextDouble() - 0.5) * 1.2f;
                int len = 14 + rng.Next(30);
                for (int k = 0; k < len; k++)
                {
                    angle += (float)(rng.NextDouble() - 0.5) * 0.25f;
                    x += Mathf.Cos(angle);
                    y += Mathf.Sin(angle) * 0.7f;
                    int th = Mathf.Max(1, 3 - k / 12);
                    p.FillEllipse((int)x, (int)y, th, th, BarkDark);
                }
            }

            return p;
        }

        private static Painter Torch(int w, int h)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(606);

            int postX = w / 2;

            // Wooden post + iron bracket.
            for (int y = 0; y < h - 26; y++)
            {
                int half = 3;
                for (int x = postX - half; x <= postX + half; x++)
                {
                    float n = (float)rng.NextDouble();
                    p.Set(x, y, Lerp(BarkDark, BarkMid, n * 0.5f));
                }
            }

            p.FillRect(postX - 6, h - 34, postX + 6, h - 28, StoneDark);

            // Flame.
            int fx = postX, fy = h - 40, fr = 9;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float dx = (x - fx) / (float)fr;
                float dy = (y - fy) / (float)(fr * 1.8f);
                float d = dx * dx + dy * dy;
                if (d < 1f)
                {
                    float t = 1f - Mathf.Sqrt(d);
                    Color32 c = Lerp(TorchFlame, TorchCore, Mathf.Clamp01(t * 1.3f));
                    p.Blend(x, y, WithA(c, (byte)(200 * t)));
                }
            }

            // Halo.
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(fx, fy));
                if (d < fr * 3f)
                    p.Blend(x, y, WithA(TorchFlame, (byte)(50f * (1f - d / (fr * 3f)))));
            }

            return p;
        }

        private static Painter Crate(int w, int h, int seed)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);

            p.FillRoundedRect(1, 1, w - 2, h - 2, 2, BarkMid);

            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                if (p.Get(x, y).a == 0) continue;
                float n = (float)rng.NextDouble();
                p.Blend(x, y, WithA(n > 0.5f ? BarkLight : BarkDark, (byte)(n * 70f)));
            }

            // Plank + iron bands.
            for (int y = 0; y < h; y += 12)
            for (int x = 0; x < w; x++)
                p.Blend(x, y, WithA(DeepNight, 80));

            p.FillRect(0, h / 2 - 2, w - 1, h / 2 + 2, StoneDark);
            p.FillRect(0, 4, w - 1, 7, StoneDark);

            for (int i = 0; i < 40; i++)
            {
                int x = rng.Next(w), y = rng.Next(h);
                if (p.Get(x, y).a > 0)
                    p.Blend(x, y, WithA(MossDark, (byte)(80 + rng.Next(80))));
            }

            return p;
        }

        private static Painter ForegroundRocks(int w, int h, int seed)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(seed);
            Color32 body = new(6, 7, 11, 255);

            int[] top = new int[w];
            float v = h * 0.55f;
            for (int x = 0; x < w; x++)
            {
                v += (float)(rng.NextDouble() - 0.5) * h * 0.3f;
                v = Mathf.Clamp(v, h * 0.15f, h * 0.85f);
                top[x] = (int)v;
            }

            for (int pass = 0; pass < 2; pass++)
            {
                int[] s = new int[w];
                for (int x = 0; x < w; x++)
                {
                    int sum = 0;
                    for (int k = -4; k <= 4; k++)
                        sum += top[Mathf.Clamp(x + k, 0, w - 1)];
                    s[x] = sum / 9;
                }
                top = s;
            }

            for (int x = 0; x < w; x++)
            for (int y = 0; y < top[x]; y++)
                p.Set(x, y, body);

            for (int x = 0; x < w; x++)
                p.Blend(x, top[x], WithA(new Color32(24, 30, 44, 255), 120));

            return p;
        }

        private static Painter FogBand(int w, int h)
        {
            var p = new Painter(w, h);
            for (int y = 0; y < h; y++)
            {
                float t = (float)y / (h - 1);
                float a = Mathf.Sin(t * Mathf.PI) * 70f;
                Color32 c = new(96, 108, 132, (byte)a);
                for (int x = 0; x < w; x++)
                    p.Set(x, y, c);
            }
            return p;
        }

        // ================================================================ characters

        private struct KnightPose
        {
            public float legPhase;
            public float armPhase;
            public float bob;
            public float crouch;
            public float swordAngle;
            public float capeSway;
            public float lean;
            public bool swordVisible;
            public float collapse;
        }

        private const int KnightW = 44;
        private const int KnightH = 62;

        private static Raster KnightFrame(KnightPose pose) => new(KnightW, KnightH, p => DrawKnight(p, pose));

        private static void DrawKnight(Painter p, KnightPose pose)
        {
            float bob = pose.bob;
            float crouch = Mathf.Clamp01(pose.crouch);
            float collapse = Mathf.Clamp01(pose.collapse);
            float legPhase = pose.legPhase;
            float armPhase = pose.armPhase;

            // collapse pivots the whole body toward the ground.
            float sink = collapse * 16f;

            float hipY = 17f - crouch * 5f + bob - sink;
            float shoulderY = 35f - crouch * 6f + bob - sink;
            float headY = 44f - crouch * 6f + bob - sink;
            float leanX = pose.lean * 0.06f * shoulderY;

            // ---- cape (behind) ----
            float capeSway = pose.capeSway + legPhase * 1.2f;
            p.FillPolygon(new[]
            {
                new Vector2(17f, shoulderY + 3f),
                new Vector2(9f - capeSway, hipY - 6f),
                new Vector2(11f - capeSway * 1.6f, hipY - 20f),
                new Vector2(21f, shoulderY - 4f),
            }, ClothDark);
            p.FillPolygon(new[]
            {
                new Vector2(19f, shoulderY + 2f),
                new Vector2(12f - capeSway, hipY - 5f),
                new Vector2(14f - capeSway * 1.5f, hipY - 17f),
                new Vector2(22f, shoulderY - 3f),
            }, ClothMid);

            // ---- legs ----
            float legSwing = legPhase * 6f;
            DrawKnightLeg(p, 16f, hipY, 16f + legSwing, 4f, pose.crouch, collapse);
            DrawKnightLeg(p, 27f, hipY, 27f - legSwing, 4f, pose.crouch, collapse);

            // ---- torso ----
            p.FillRoundedRect((int)(13f + leanX * 0.5f), (int)hipY, 17, (int)(shoulderY - hipY) + 3, 4, SteelMid);
            p.FillRoundedRect((int)(14f + leanX * 0.5f), (int)hipY + 4, 15, (int)(shoulderY - hipY) - 4, 3, SteelDark);
            // chest plate highlight
            p.FillRoundedRect((int)(20f + leanX * 0.7f), (int)(hipY + (shoulderY - hipY) * 0.35f), 8, (int)((shoulderY - hipY) * 0.5f), 3, SteelLight);
            p.FillRect((int)(14f + leanX * 0.6f), (int)(hipY + 2f), (int)(29f + leanX * 0.6f), (int)(hipY + 5f), ClothDark);

            // ---- shoulders ----
            p.FillEllipse((int)(13f + leanX), (int)(shoulderY - 2f), 5, 4, SteelDark);
            p.FillEllipse((int)(30f + leanX), (int)(shoulderY - 2f), 5, 4, SteelMid);
            p.BlendEllipse((int)(30f + leanX), (int)(shoulderY), 4, 3, WithA(SteelLight, 150));

            // ---- head / helmet ----
            int hx = (int)(21f + leanX);
            int hy = (int)headY;
            p.FillEllipse(hx, hy, 8, 8, SteelMid);
            p.FillEllipse(hx - 1, hy + 1, 6, 6, SteelLight);
            // visor
            p.FillRect(hx - 6, hy - 2, hx + 7, hy + 1, DeepNight);
            p.FillRect(hx - 6, hy + 1, hx + 7, hy + 2, SteelDark);
            // plume / crest for silhouette readability
            p.FillPolygon(new[]
            {
                new Vector2(hx - 2f, hy + 8f),
                new Vector2(hx + 2f, hy + 8f),
                new Vector2(hx - 1f, hy + 17f),
            }, ClothMid);
            p.FillPolygon(new[]
            {
                new Vector2(hx - 2f, hy + 8f),
                new Vector2(hx - 6f, hy + 13f),
                new Vector2(hx - 1f, hy + 16f),
            }, ClothDark);

            // ---- arms ----
            float handX = 30f + armPhase * 4f;
            float handY = shoulderY - 9f + Mathf.Abs(armPhase) * 2f;

            if (!pose.swordVisible)
            {
                // back arm hanging
                p.ThickLine(new Vector2(14f, shoulderY - 3f), new Vector2(11f - armPhase * 2f, hipY + 4f), 4f, SteelDark);
            }

            p.ThickLine(new Vector2(29f + leanX, shoulderY - 3f), new Vector2(handX, handY), 4f, SteelMid);
            p.FillEllipse((int)handX, (int)handY, 3, 3, SteelDark);

            if (!pose.swordVisible)
                return;

            // ---- sword ----
            float angle = pose.swordAngle * Mathf.Deg2Rad;
            Vector2 dir = new(Mathf.Cos(angle), Mathf.Sin(angle));
            Vector2 guardStart = new(handX, handY);
            Vector2 tip = guardStart + dir * 24f;

            p.ThickLine(guardStart, tip, 2.4f, SteelLight);
            p.ThickLine(guardStart, guardStart + dir * 3f, 3.4f, BarkMid);

            // crossguard
            Vector2 perp = new(-dir.y, dir.x);
            p.ThickLine(guardStart - perp * 4f, guardStart + perp * 4f, 2f, SteelMid);

            // blade glint
            p.ThickLine(guardStart + dir * 4f, tip - dir * 3f, 0.9f, Opaque);
        }

        private static void DrawKnightLeg(Painter p, float hipX, float hipY, float footX, float footY, float crouch, float collapse)
        {
            // Knee bends forward when crouching.
            float kneeX = Mathf.Lerp(hipX, footX, 0.5f) + 3f * crouch;
            float kneeY = Mathf.Lerp(hipY, footY, 0.5f) - 1f * crouch;

            p.ThickLine(new Vector2(hipX, hipY), new Vector2(kneeX, kneeY), 5f, SteelDark);
            p.ThickLine(new Vector2(kneeX, kneeY), new Vector2(footX, footY), 4.5f, SteelMid);

            // boot
            p.FillRoundedRect((int)footX - 4, (int)footY - 1, 10, 5, 2, StoneDark);
            p.BlendEllipse((int)footX + 3, (int)footY + 2, 3, 2, WithA(SteelLight, 90));
        }

        private static List<Raster> IdleFrames()
        {
            var frames = new List<Raster>();
            for (int i = 0; i < 4; i++)
            {
                float t = i / 4f * Mathf.PI * 2f;
                frames.Add(KnightFrame(new KnightPose
                {
                    bob = Mathf.Sin(t) * 0.9f,
                    capeSway = Mathf.Sin(t * 0.8f) * 1.6f,
                    legPhase = 0f,
                    armPhase = Mathf.Sin(t + 1f) * 0.12f,
                    swordAngle = -58f,
                    swordVisible = true,
                }));
            }
            return frames;
        }

        private static List<Raster> WalkFrames()
        {
            var frames = new List<Raster>();
            for (int i = 0; i < 6; i++)
            {
                float t = i / 6f * Mathf.PI * 2f;
                frames.Add(KnightFrame(new KnightPose
                {
                    legPhase = Mathf.Sin(t),
                    armPhase = Mathf.Sin(t) * 0.7f,
                    bob = Mathf.Abs(Mathf.Sin(t)) * 1.4f,
                    capeSway = Mathf.Sin(t) * 2.4f,
                    swordAngle = -52f,
                    swordVisible = true,
                }));
            }
            return frames;
        }

        private static List<Raster> RunFrames()
        {
            var frames = new List<Raster>();
            for (int i = 0; i < 6; i++)
            {
                float t = i / 6f * Mathf.PI * 2f;
                frames.Add(KnightFrame(new KnightPose
                {
                    legPhase = Mathf.Sin(t) * 1.35f,
                    armPhase = Mathf.Sin(t) * 1.1f,
                    bob = Mathf.Abs(Mathf.Sin(t)) * 2.2f,
                    lean = 8f,
                    capeSway = 2f + Mathf.Sin(t) * 3.4f,
                    swordAngle = -40f,
                    swordVisible = true,
                }));
            }
            return frames;
        }

        private static List<Raster> AttackFrames()
        {
            return new List<Raster>
            {
                // wind up
                KnightFrame(new KnightPose { swordAngle = 130f, armPhase = -0.6f, lean = -6f, capeSway = -2f, swordVisible = true }),
                KnightFrame(new KnightPose { swordAngle = 70f, armPhase = -0.2f, legPhase = 0.3f, lean = 2f, swordVisible = true }),
                // strike
                KnightFrame(new KnightPose { swordAngle = -10f, armPhase = 0.6f, legPhase = 0.7f, bob = 1f, lean = 10f, capeSway = 3f, swordVisible = true }),
                // follow through
                KnightFrame(new KnightPose { swordAngle = 40f, armPhase = 0.4f, legPhase = 0.4f, bob = 0.4f, lean = 6f, capeSway = 2f, swordVisible = true }),
            };
        }

        private static List<Raster> DeathFrames()
        {
            var frames = new List<Raster>();
            for (int i = 0; i < 4; i++)
            {
                float t = i / 3f;
                frames.Add(KnightFrame(new KnightPose
                {
                    collapse = t,
                    crouch = t * 0.7f,
                    lean = -18f * t,
                    legPhase = -0.6f * t,
                    capeSway = -4f * t,
                    swordAngle = 20f,
                    swordVisible = i < 2,
                }));
            }
            return frames;
        }

        private struct WraithPose
        {
            public float sway;
            public float armRaise;
            public float bob;
            public float lean;
            public float glow;
        }

        private const int WraithW = 40;
        private const int WraithH = 56;

        private static Raster WraithFrame(WraithPose pose) => new(WraithW, WraithH, p => DrawWraith(p, pose));

        private static void DrawWraith(Painter p, WraithPose pose)
        {
            float bob = pose.bob;
            float sway = pose.sway;
            float lean = pose.lean;
            float floatY = 6f + bob;

            // Tattered robe - jagged hem.
            var rng = new System.Random(4242);
            int hemY = (int)(4f + bob + 2f);
            for (int x = 0; x < WraithW; x++)
            {
                int jag = (int)(Mathf.Abs(Mathf.Sin(x * 0.7f)) * 6f) + rng.Next(3);
                int topY = (int)(38f + bob);
                for (int y = hemY + jag; y < topY; y++)
                {
                    float t = (float)(y - hemY) / Mathf.Max(1, topY - hemY);
                    Color32 c = Lerp(WraithDark, WraithMid, t * 0.85f);
                    int xo = x + (int)(sway * t * 3f) + (int)(lean * 0.3f * t);
                    p.Set(xo, y, c);
                }
            }

            int cx = 20 + (int)(lean * 0.4f);

            // Shoulders / hood mantle.
            p.FillEllipse(cx, (int)(38f + bob), 13, 7, WraithMid);
            p.FillEllipse(cx, (int)(42f + bob), 9, 8, WraithDark);

            // Hood cavity with glowing eyes.
            p.FillEllipse(cx, (int)(44f + bob), 6, 6, new Color32(8, 7, 12, 255));

            float glow = 0.6f + Mathf.Clamp01(pose.glow) * 0.6f;
            p.FillEllipse(cx - 3, (int)(45f + bob), 1, 1, WraithGlow);
            p.FillEllipse(cx + 3, (int)(45f + bob), 1, 1, WraithGlow);
            for (int i = 0; i < 2; i++)
            {
                p.BlendEllipse(cx - 3, (int)(45f + bob), 3, 3, WithA(WraithGlow, (byte)(40 * glow)));
                p.BlendEllipse(cx + 3, (int)(45f + bob), 3, 3, WithA(WraithGlow, (byte)(40 * glow)));
            }

            // Skeletal arms.
            float armAngle = -0.6f - pose.armRaise;
            Vector2 shoulderL = new(cx - 10f, 36f + bob);
            Vector2 shoulderR = new(cx + 10f, 36f + bob);
            Vector2 handL = shoulderL + new Vector2(Mathf.Cos(armAngle + Mathf.PI) * 15f, Mathf.Sin(-armAngle) * 14f);
            Vector2 handR = shoulderR + new Vector2(Mathf.Cos(-armAngle) * 15f, Mathf.Sin(-armAngle) * 14f);

            p.ThickLine(shoulderL, handL, 3f, WraithDark);
            p.ThickLine(shoulderR, handR, 3f, WraithDark);
            p.FillEllipse((int)handL.x, (int)handL.y, 3, 3, WraithMid);
            p.FillEllipse((int)handR.x, (int)handR.y, 3, 3, WraithMid);

            // Claws.
            for (int i = 0; i < 3; i++)
            {
                Vector2 clawDir = (handR - shoulderR).normalized;
                Vector2 side = new(-clawDir.y, clawDir.x);
                Vector2 basePt = handR + side * (i - 1) * 2.5f;
                p.ThickLine(basePt, basePt + clawDir * 5f, 1.2f, WraithGlow);
            }

            // Ghostly core glow.
            for (int y = 0; y < WraithH; y++)
            for (int x = 0; x < WraithW; x++)
            {
                if (p.Get(x, y).a == 0) continue;
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(cx, 30f + bob));
                if (d < 12f)
                    p.Blend(x, y, WithA(WraithGlowSoft, (byte)(30f * (1f - d / 12f) * glow)));
            }
        }

        private static List<Raster> WraithFrames(float swayAmount, float armRaise, float glow, int count)
        {
            var frames = new List<Raster>();
            for (int i = 0; i < count; i++)
            {
                float t = i / (float)count * Mathf.PI * 2f;
                frames.Add(WraithFrame(new WraithPose
                {
                    sway = Mathf.Sin(t) * (0.6f + swayAmount),
                    bob = Mathf.Sin(t) * 1.6f,
                    armRaise = armRaise + Mathf.Sin(t) * 0.12f,
                    glow = glow + Mathf.Sin(t) * 0.15f,
                }));
            }
            return frames;
        }

        private static List<Raster> WraithAttackFrames()
        {
            var frames = new List<Raster>();
            for (int i = 0; i < 4; i++)
            {
                float t = i / 3f;
                frames.Add(WraithFrame(new WraithPose
                {
                    sway = -0.8f * Mathf.Sin(t * Mathf.PI),
                    bob = 1.4f * Mathf.Sin(t * Mathf.PI),
                    armRaise = Mathf.Lerp(-0.1f, 1.6f, t),
                    lean = 10f * t,
                    glow = 0.4f + t,
                }));
            }
            return frames;
        }

        private static List<Raster> WraithDeathFrames()
        {
            var frames = new List<Raster>();
            for (int i = 0; i < 4; i++)
            {
                float t = i / 3f;
                frames.Add(WraithFrame(new WraithPose
                {
                    bob = -t * 10f,
                    sway = Mathf.Sin(t * 6f) * 1.5f,
                    glow = 1f - t * 0.6f,
                    armRaise = 1.2f * t,
                }));
            }
            return frames;
        }

        // ================================================================ effects

        private static Painter HitSpark(int w, int h, int frame)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(1000 + frame);
            int cx = w / 2, cy = h / 2;
            int spikes = 8 + frame * 2;
            float reach = 4f + frame * 2.2f;

            for (int i = 0; i < spikes; i++)
            {
                float a = (float)i / spikes * Mathf.PI * 2f + frame * 0.3f;
                Vector2 dir = new(Mathf.Cos(a), Mathf.Sin(a));
                p.ThickLine(new Vector2(cx, cy), new Vector2(cx, cy) + dir * reach, 1.6f,
                    frame < 2 ? TorchCore : TorchFlame);
            }

            p.FillEllipse(cx, cy, 3 - frame / 2, 3 - frame / 2, frame < 2 ? TorchCore : TorchFlame);

            for (int i = 0; i < 12; i++)
                p.Blend(rng.Next(w), rng.Next(h), WithA(TorchFlame, (byte)(120 + rng.Next(90))));

            return p;
        }

        private static Painter DustPuff(int w, int h, int frame)
        {
            var p = new Painter(w, h);
            var rng = new System.Random(2000 + frame);

            for (int i = 0; i < 7; i++)
            {
                float cx = w * 0.5f + (float)(rng.NextDouble() - 0.5) * (10 + frame * 6);
                float cy = h * 0.5f + (float)(rng.NextDouble() - 0.5) * (4 + frame * 3);
                int r = 3 + rng.Next(3);
                byte a = (byte)Mathf.Lerp(150, 0, frame / 4f);
                p.BlendEllipse((int)cx, (int)cy, r, r, new Color32(120, 116, 104, a));
            }

            return p;
        }

        private static Painter SlashArc(int w, int h, float t)
        {
            var p = new Painter(w, h);
            int cx = w / 2, cy = h / 2;

            for (int a = -50; a <= 50; a++)
            {
                float rad = a * Mathf.Deg2Rad;
                for (int r = 8; r < 18; r++)
                {
                    int x = (int)(cx + Mathf.Cos(rad) * r);
                    int y = (int)(cy + Mathf.Sin(rad) * r);
                    float edge = 1f - Mathf.Abs(a) / 50f;
                    byte alpha = (byte)(200 * edge);
                    p.Blend(x, y, WithA(Opaque, alpha));
                }
            }

            return p;
        }

        private static Painter SoftGlow(int s)
        {
            var p = new Painter(s, s);
            float c = (s - 1) * 0.5f;
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float d = Vector2.Distance(new Vector2(x, y), new Vector2(c, c)) / c;
                float a = Mathf.Clamp01(1f - d);
                p.Set(x, y, new Color32(255, 214, 170, (byte)(a * a * 200f)));
            }
            return p;
        }

        private static Painter VignetteOverlay(int s)
        {
            var p = new Painter(s, s);
            float c = (s - 1) * 0.5f;
            for (int y = 0; y < s; y++)
            for (int x = 0; x < s; x++)
            {
                float dx = (x - c) / c;
                float dy = (y - c) / c;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float a = Mathf.Clamp01((d - 0.55f) / 0.45f);
                p.Set(x, y, new Color32(6, 4, 8, (byte)(a * a * 210f)));
            }
            return p;
        }

        // ================================================================ helpers

        private static List<Raster> Single(Raster sprite) => new() { sprite };

        private static int SaveFrames(string basePath, List<Raster> frames)
        {
            if (frames.Count == 1)
            {
                SaveSprite(frames[0], basePath, new Vector2(0.5f, 0.08f));
                return 1;
            }

            for (int i = 0; i < frames.Count; i++)
                SaveSprite(frames[i], $"{basePath}_{i:00}", new Vector2(0.5f, 0.08f));

            return frames.Count;
        }

        private static void SaveSprite(Painter p, string relativePath, Vector2 pivot, FilterMode filter = FilterMode.Point)
        {
            string assetPath = $"{ArtRoot}/{relativePath}.png";
            string directory = Path.GetDirectoryName(assetPath).Replace('\\', '/');
            if (!AssetDatabase.IsValidFolder(directory))
            {
                string parent = Path.GetDirectoryName(directory).Replace('\\', '/');
                AssetDatabase.CreateFolder(parent, Path.GetFileName(directory));
            }

            var texture = new Texture2D(p.W, p.H, TextureFormat.RGBA32, false);
            texture.SetPixels32(p.Px);
            texture.Apply();

            File.WriteAllBytes(Path.GetFullPath(assetPath), texture.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(texture);

            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceUpdate);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogError($"[DarkRealms] Failed to get TextureImporter for {assetPath}");
                return;
            }

            importer.textureType = TextureImporterType.Sprite;
            importer.spriteImportMode = SpriteImportMode.Single;
            importer.spritePixelsPerUnit = Ppu;
            importer.filterMode = filter;
            importer.textureCompression = TextureImporterCompression.Uncompressed;
            importer.mipmapEnabled = false;
            importer.wrapMode = TextureWrapMode.Clamp;
            importer.alphaIsTransparency = true;

            var settings = new TextureImporterSettings();
            importer.ReadTextureSettings(settings);
            settings.spriteAlignment = (int)SpriteAlignment.Custom;
            settings.spritePivot = pivot;
            settings.spriteMeshType = SpriteMeshType.FullRect;
            settings.spriteExtrude = 1;
            importer.SetTextureSettings(settings);

            importer.SaveAndReimport();
        }

        private static Color32 WithA(Color32 c, byte a) => new(c.r, c.g, c.b, a);

        private static Color32 Lerp(Color32 a, Color32 b, float t)
        {
            t = Mathf.Clamp01(t);
            return new Color32(
                (byte)(a.r + (b.r - a.r) * t),
                (byte)(a.g + (b.g - a.g) * t),
                (byte)(a.b + (b.b - a.b) * t),
                (byte)(a.a + (b.a - a.a) * t));
        }

        /// <summary>Minimal RGBA32 raster painter with alpha compositing and basic primitives.</summary>
        private class Painter
        {
            public readonly int W;
            public readonly int H;
            public readonly Color32[] Px;

            public Painter(int w, int h)
            {
                W = w;
                H = h;
                Px = new Color32[w * h];
            }

            public void Set(int x, int y, Color32 c)
            {
                if ((uint)x >= (uint)W || (uint)y >= (uint)H)
                    return;
                Px[y * W + x] = c;
            }

            public Color32 Get(int x, int y)
            {
                if ((uint)x >= (uint)W || (uint)y >= (uint)H)
                    return new Color32(0, 0, 0, 0);
                return Px[y * W + x];
            }

            public void Blend(int x, int y, Color32 c)
            {
                if ((uint)x >= (uint)W || (uint)y >= (uint)H || c.a == 0)
                    return;

                Color32 d = Px[y * W + x];
                float a = c.a / 255f;
                float outA = Mathf.Max(d.a / 255f, a);
                float r = d.r * (1f - a) + c.r * a;
                float g = d.g * (1f - a) + c.g * a;
                float b = d.b * (1f - a) + c.b * a;
                Px[y * W + x] = new Color32((byte)r, (byte)g, (byte)b, (byte)(outA * 255f));
            }

            public void FillRect(int x0, int y0, int x1, int y1, Color32 c)
            {
                if (x1 < x0) (x0, x1) = (x1, x0);
                if (y1 < y0) (y0, y1) = (y1, y0);

                for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    Set(x, y, c);
            }

            public void FillEllipse(int cx, int cy, int rx, int ry, Color32 c)
            {
                if (rx < 0) rx = 0;
                if (ry < 0) ry = 0;

                for (int y = cy - ry; y <= cy + ry; y++)
                for (int x = cx - rx; x <= cx + rx; x++)
                {
                    float dx = rx == 0 ? 0f : (x - cx) / (float)rx;
                    float dy = ry == 0 ? 0f : (y - cy) / (float)ry;
                    if (dx * dx + dy * dy <= 1.0001f)
                        Set(x, y, c);
                }
            }

            public void BlendEllipse(int cx, int cy, int rx, int ry, Color32 c)
            {
                for (int y = cy - ry; y <= cy + ry; y++)
                for (int x = cx - rx; x <= cx + rx; x++)
                {
                    float dx = rx == 0 ? 0f : (x - cx) / (float)rx;
                    float dy = ry == 0 ? 0f : (y - cy) / (float)ry;
                    if (dx * dx + dy * dy <= 1.0001f)
                        Blend(x, y, c);
                }
            }

            public void FillRoundedRect(int x, int y, int w, int h, int radius, Color32 c)
            {
                radius = Mathf.Min(radius, Mathf.Min(w, h) / 2);

                for (int yy = 0; yy < h; yy++)
                for (int xx = 0; xx < w; xx++)
                {
                    int dx = 0, dy = 0;

                    if (xx < radius) dx = radius - xx;
                    else if (xx >= w - radius) dx = xx - (w - radius - 1);

                    if (yy < radius) dy = radius - yy;
                    else if (yy >= h - radius) dy = yy - (h - radius - 1);

                    if (dx * dx + dy * dy <= radius * radius)
                        Set(x + xx, y + yy, c);
                }
            }

            public void ThickLine(Vector2 a, Vector2 b, float thickness, Color32 c)
            {
                float length = Vector2.Distance(a, b);
                int steps = Mathf.Max(1, Mathf.CeilToInt(length * 2f));
                float radius = thickness * 0.5f;

                for (int i = 0; i <= steps; i++)
                {
                    float t = i / (float)steps;
                    Vector2 point = Vector2.Lerp(a, b, t);
                    int r = Mathf.Max(1, Mathf.RoundToInt(radius));
                    FillEllipse(Mathf.RoundToInt(point.x), Mathf.RoundToInt(point.y), r, r, c);
                }
            }

            public void FillPolygon(Vector2[] points, Color32 c)
            {
                if (points == null || points.Length < 3)
                    return;

                float minY = float.MaxValue, maxY = float.MinValue;
                foreach (var pt in points)
                {
                    minY = Mathf.Min(minY, pt.y);
                    maxY = Mathf.Max(maxY, pt.y);
                }

                int y0 = Mathf.Max(0, Mathf.FloorToInt(minY));
                int y1 = Mathf.Min(H - 1, Mathf.CeilToInt(maxY));

                var intersections = new List<float>();

                for (int y = y0; y <= y1; y++)
                {
                    intersections.Clear();
                    float scanY = y + 0.5f;

                    for (int i = 0; i < points.Length; i++)
                    {
                        Vector2 p1 = points[i];
                        Vector2 p2 = points[(i + 1) % points.Length];

                        if ((p1.y <= scanY && p2.y > scanY) || (p2.y <= scanY && p1.y > scanY))
                        {
                            float t = (scanY - p1.y) / (p2.y - p1.y);
                            intersections.Add(p1.x + t * (p2.x - p1.x));
                        }
                    }

                    intersections.Sort();

                    for (int i = 0; i + 1 < intersections.Count; i += 2)
                    {
                        int xStart = Mathf.CeilToInt(intersections[i]);
                        int xEnd = Mathf.FloorToInt(intersections[i + 1]);

                        for (int x = xStart; x <= xEnd; x++)
                            Set(x, y, c);
                    }
                }
            }
        }

        /// <summary>A deferred drawing that rasterises into a Painter when converted.</summary>
        private class Raster
        {
            private readonly int width;
            private readonly int height;
            private readonly Action<Painter> draw;

            public Raster(int width, int height, Action<Painter> draw)
            {
                this.width = width;
                this.height = height;
                this.draw = draw;
            }

            public static implicit operator Painter(Raster raster)
            {
                var painter = new Painter(raster.width, raster.height);
                raster.draw(painter);
                return painter;
            }
        }
    }
}

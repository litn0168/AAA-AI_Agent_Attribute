using System;
using System.Collections.Generic;
using UnityEngine;

namespace DarkRealms.World
{
    /// <summary>Shapes used by the world map. Shape and colour are both used, so a marker is still readable
    /// without colour vision.</summary>
    public enum MapMarkerShape
    {
        Circle,
        Ring,
        Square,
        Diamond,
        Cross,
        Bar
    }

    /// <summary>
    /// Small procedural sprites for the world map, generated once and cached.
    ///
    /// The atlas ships without final artwork on purpose, and the project has no UI sprite set to borrow from, so
    /// shapes are generated rather than referenced: replacing a map thumbnail with real art therefore needs no
    /// code change, and a missing thumbnail still renders as a placeholder instead of an invisible marker.
    /// </summary>
    public static class WorldMapIcons
    {
        private const int Size = 64;
        private static readonly Dictionary<MapMarkerShape, Sprite> Cache = new Dictionary<MapMarkerShape, Sprite>();

        public static Sprite Shape(MapMarkerShape shape)
        {
            if (Cache.TryGetValue(shape, out Sprite cached) && cached != null)
                return cached;

            Sprite sprite = Build(shape);
            Cache[shape] = sprite;
            return sprite;
        }

        private static Sprite Build(MapMarkerShape shape)
        {
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false)
            {
                name = "DarkRealmsWorldMap_" + shape,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };

            var pixels = new Color32[Size * Size];
            float centre = (Size - 1) * 0.5f;
            float radius = Size * 0.5f - 2f;

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dx = (x - centre) / radius;
                    float dy = (y - centre) / radius;
                    bool inside;

                    switch (shape)
                    {
                        case MapMarkerShape.Circle:
                            inside = dx * dx + dy * dy <= 1f;
                            break;
                        case MapMarkerShape.Ring:
                            float r2 = dx * dx + dy * dy;
                            inside = r2 <= 1f && r2 >= 0.3f;
                            break;
                        case MapMarkerShape.Diamond:
                            inside = Mathf.Abs(dx) + Mathf.Abs(dy) <= 1f;
                            break;
                        case MapMarkerShape.Cross:
                            inside = (Mathf.Abs(dx) <= 0.22f && Mathf.Abs(dy) <= 1f) ||
                                     (Mathf.Abs(dy) <= 0.22f && Mathf.Abs(dx) <= 1f);
                            break;
                        case MapMarkerShape.Bar:
                            inside = Mathf.Abs(dy) <= 0.28f && Mathf.Abs(dx) <= 1f;
                            break;
                        default:
                            inside = Mathf.Abs(dx) <= 0.85f && Mathf.Abs(dy) <= 0.85f;
                            break;
                    }

                    pixels[y * Size + x] = inside
                        ? new Color32(255, 255, 255, 255)
                        : new Color32(255, 255, 255, 0);
                }
            }

            texture.SetPixels32(pixels);
            texture.Apply();

            Sprite sprite = Sprite.Create(texture, new Rect(0f, 0f, Size, Size), new Vector2(0.5f, 0.5f), Size);
            sprite.name = "DarkRealmsWorldMap_" + shape;
            sprite.hideFlags = HideFlags.HideAndDontSave;
            return sprite;
        }

        /// <summary>Shape for a map card. Two maps never share a shape purely by accident: type drives it.</summary>
        public static MapMarkerShape ShapeForMap(MapType type)
        {
            switch (type)
            {
                case MapType.Settlement: return MapMarkerShape.Square;
                case MapType.Dungeon: return MapMarkerShape.Diamond;
                case MapType.Interior: return MapMarkerShape.Bar;
                case MapType.BossArena: return MapMarkerShape.Diamond;
                case MapType.Instanced: return MapMarkerShape.Cross;
                default: return MapMarkerShape.Circle;
            }
        }

        /// <summary>Shape for a marker. Chest tiers differ by shape as well as by colour, so a purple chest is
        /// distinguishable from a purple story marker.</summary>
        public static MapMarkerShape ShapeForPoi(PoiKind kind)
        {
            switch (kind)
            {
                case PoiKind.ChestCommon:
                case PoiKind.ChestPurple:
                case PoiKind.ChestSpecial:
                case PoiKind.Collectible:
                    return MapMarkerShape.Square;
                case PoiKind.Boss:
                case PoiKind.MainStory:
                    return MapMarkerShape.Diamond;
                case PoiKind.SideStory:
                case PoiKind.CrossedPath:
                case PoiKind.QuestStart:
                case PoiKind.QuestDestination:
                    return MapMarkerShape.Bar;
                case PoiKind.Guild:
                case PoiKind.Altar:
                    return MapMarkerShape.Ring;
                case PoiKind.Shop:
                case PoiKind.Inn:
                case PoiKind.Town:
                    return MapMarkerShape.Circle;
                case PoiKind.EnemySpecies:
                case PoiKind.EncounterGroup:
                case PoiKind.DangerZone:
                    return MapMarkerShape.Cross;
                default:
                    return MapMarkerShape.Circle;
            }
        }

        public static Color ColorForPoi(PoiKind kind)
        {
            switch (kind)
            {
                case PoiKind.ChestCommon: return new Color(0.78f, 0.72f, 0.52f, 1f);
                case PoiKind.ChestPurple: return new Color(0.62f, 0.46f, 0.86f, 1f);
                case PoiKind.ChestSpecial: return new Color(0.95f, 0.62f, 0.24f, 1f);
                case PoiKind.Boss: return new Color(0.86f, 0.30f, 0.32f, 1f);
                case PoiKind.MainStory: return new Color(0.95f, 0.84f, 0.42f, 1f);
                case PoiKind.SideStory:
                case PoiKind.CrossedPath: return new Color(0.55f, 0.78f, 0.92f, 1f);
                case PoiKind.Guild: return new Color(0.44f, 0.66f, 0.90f, 1f);
                case PoiKind.Altar: return new Color(0.72f, 0.90f, 0.72f, 1f);
                case PoiKind.Shop:
                case PoiKind.Inn:
                case PoiKind.Town: return new Color(0.92f, 0.88f, 0.72f, 1f);
                case PoiKind.EnemySpecies:
                case PoiKind.EncounterGroup:
                case PoiKind.DangerZone: return new Color(0.80f, 0.52f, 0.44f, 1f);
                case PoiKind.NpcLocation: return new Color(0.70f, 0.80f, 0.70f, 1f);
                case PoiKind.LockedGate: return new Color(0.90f, 0.45f, 0.35f, 1f);
                case PoiKind.MapExit:
                case PoiKind.Bridge:
                case PoiKind.Pass:
                case PoiKind.Port:
                case PoiKind.Route: return new Color(0.70f, 0.74f, 0.82f, 1f);
                default: return new Color(0.80f, 0.82f, 0.88f, 1f);
            }
        }

        /// <summary>Colour for a map card: current, route destination, visited and undiscovered are all
        /// different, and none of them relies on colour alone (the shape and the label carry it too).</summary>
        public static Color ColorForMap(bool isCurrent, bool isSelected, bool isDestination, bool isVisited)
        {
            if (isCurrent) return new Color(0.99f, 0.86f, 0.42f, 1f);
            if (isDestination) return new Color(0.98f, 0.55f, 0.45f, 1f);
            if (isSelected) return new Color(0.62f, 0.86f, 0.98f, 1f);
            if (isVisited) return new Color(0.72f, 0.78f, 0.86f, 1f);
            return new Color(0.42f, 0.46f, 0.55f, 1f);
        }
    }
}

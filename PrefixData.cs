using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.Localization;
using TerrariaModder.Core.UI;

namespace AutoReforge
{
    public enum PrefixTier
    {
        Best,    // Highest value multiplier for this item type (rainbow)
        Good,    // Value multiplier > 1.0 (green)
        Neutral, // Value multiplier ~= 1.0 (white)
        Bad,     // Value multiplier < 1.0 (red)
    }

    public readonly struct PrefixInfo
    {
        public readonly int Id;
        public readonly string Name;
        public readonly PrefixTier Tier;

        public PrefixInfo(int id, string name, PrefixTier tier)
        {
            Id = id;
            Name = name;
            Tier = tier;
        }
    }

    /// <summary>
    /// Helpers for building a classified list of prefixes for a given item.
    /// </summary>
    public static class PrefixData
    {
        // Cached reflection for TryGetPrefixStatMultipliersForItem — has many out params
        private static MethodInfo? _tryGetStatsMI;
        private static readonly object[] _statsArgs = new object[11]; // id + 10 out params

        // Rainbow hue offset that advances each frame for "Best" tier labels
        private static float _rainbowHue;

        /// <summary>
        /// Call once per frame to advance the rainbow animation.
        /// </summary>
        public static void Tick()
        {
            _rainbowHue = (_rainbowHue + 1.5f) % 360f;
        }

        /// <summary>
        /// Build the classified prefix list for the item currently in the reforge slot.
        /// Returns null if the slot is empty or the item cannot be reforged.
        /// </summary>
        public static List<PrefixInfo>? BuildForCurrentItem()
        {
            var item = Main.reforgeItem;
            if (item == null || item.type <= 0) return null;

            int[]? rollable = item.GetRollablePrefixes();
            if (rollable == null || rollable.Length == 0) return null;

            float bestValue = item.BestPrefixValue();

            var list = new List<PrefixInfo>(rollable.Length);
            foreach (int id in rollable)
            {
                if (id <= 0 || id >= PrefixID.Count) continue;

                string name = GetPrefixName(id);
                PrefixTier tier = ClassifyPrefix(item, id, bestValue);
                list.Add(new PrefixInfo(id, name, tier));
            }

            // Sort: Best → Good → Neutral → Bad, then alphabetically within tier
            list.Sort((a, b) =>
            {
                int tierCmp = a.Tier.CompareTo(b.Tier);
                return tierCmp != 0 ? tierCmp : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });

            return list;
        }

        /// <summary>
        /// Get the Color4 for a prefix tier.
        /// For Best tier, returns an animated rainbow color; call Tick() each frame.
        /// </summary>
        public static Color4 GetTierColor(PrefixTier tier, int itemIndex = 0)
        {
            switch (tier)
            {
                case PrefixTier.Best:
                    // Each row gets a phase offset so the rainbow "slides" across the list
                    float hue = (_rainbowHue + itemIndex * 25f) % 360f;
                    return HsvToColor4(hue, 1f, 1f);

                case PrefixTier.Good:
                    return new Color4(120, 255, 120);

                case PrefixTier.Neutral:
                    return new Color4(210, 210, 210);

                case PrefixTier.Bad:
                    return new Color4(255, 100, 100);

                default:
                    return new Color4(255, 255, 255);
            }
        }

        /// <summary>
        /// Get a dimmer version of the tier color for row backgrounds.
        /// </summary>
        public static Color4 GetTierBgColor(PrefixTier tier)
        {
            switch (tier)
            {
                case PrefixTier.Best:    return new Color4(50, 30, 70, 180);
                case PrefixTier.Good:    return new Color4(20, 50, 20, 180);
                case PrefixTier.Neutral: return new Color4(35, 35, 55, 180);
                case PrefixTier.Bad:     return new Color4(60, 20, 20, 180);
                default:                 return new Color4(35, 35, 55, 180);
            }
        }

        /// <summary>
        /// Return the section header label for a tier.
        /// </summary>
        public static string GetTierLabel(PrefixTier tier)
        {
            switch (tier)
            {
                case PrefixTier.Best:    return "Best";
                case PrefixTier.Good:    return "Good";
                case PrefixTier.Neutral: return "Neutral";
                case PrefixTier.Bad:     return "Bad";
                default:                 return "";
            }
        }

        // ------------------------------------------------------------------
        // Private helpers
        // ------------------------------------------------------------------

        private static string GetPrefixName(int id)
        {
            try
            {
                var text = Lang.prefix[id];
                return text?.Value ?? $"Prefix {id}";
            }
            catch
            {
                return $"Prefix {id}";
            }
        }

        private static PrefixTier ClassifyPrefix(Item item, int prefixId, float bestValue)
        {
            float value = GetPrefixValue(item, prefixId);

            // Use a small epsilon to handle floating-point imprecision
            if (Math.Abs(value - bestValue) < 0.0001f)
                return PrefixTier.Best;

            if (value > 1.0001f)
                return PrefixTier.Good;

            if (value < 0.9999f)
                return PrefixTier.Bad;

            return PrefixTier.Neutral;
        }

        private static float GetPrefixValue(Item item, int prefixId)
        {
            try
            {
                // TryGetPrefixStatMultipliersForItem has 11 params (id + 10 outs)
                // Cache the MethodInfo since this is called many times
                if (_tryGetStatsMI == null)
                {
                    _tryGetStatsMI = typeof(Item).GetMethod(
                        "TryGetPrefixStatMultipliersForItem",
                        BindingFlags.Public | BindingFlags.Instance);
                }

                if (_tryGetStatsMI == null) return 1f;

                _statsArgs[0] = prefixId;
                // args 1-10 are out params, pre-fill with defaults
                for (int i = 1; i <= 10; i++) _statsArgs[i] = null!;

                object? result = _tryGetStatsMI.Invoke(item, _statsArgs);
                bool success = result is bool b && b;
                if (!success) return 0f; // prefix not valid for this item

                // Out param index 10 is the "value" float (last out param)
                return _statsArgs[10] is float f ? f : 1f;
            }
            catch
            {
                return 1f;
            }
        }

        /// <summary>
        /// Convert HSV (hue 0-360, sat/val 0-1) to Color4.
        /// </summary>
        public static Color4 HsvToColor4(float h, float s, float v)
        {
            float c = v * s;
            float x = c * (1f - Math.Abs((h / 60f) % 2f - 1f));
            float m = v - c;

            float r, g, b;
            int sector = (int)(h / 60f) % 6;
            switch (sector)
            {
                case 0: r = c; g = x; b = 0; break;
                case 1: r = x; g = c; b = 0; break;
                case 2: r = 0; g = c; b = x; break;
                case 3: r = 0; g = x; b = c; break;
                case 4: r = x; g = 0; b = c; break;
                default: r = c; g = 0; b = x; break;
            }

            return new Color4(
                (byte)((r + m) * 255),
                (byte)((g + m) * 255),
                (byte)((b + m) * 255));
        }
    }
}

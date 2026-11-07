#nullable enable
using System;
using System.Collections.Generic;
using ClassicUO.Assets;
using ClassicUO.Renderer;
using ClassicUO.Utility;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// SeriousFun: art for dyed house pieces where only the "brick" takes the dye. A plain UO hue recolors every
    /// pixel of a sprite, so wooden frames, mortar and stained glass would turn the dye color too; here only the dull,
    /// grey or grey-brown (never bluish) pixels are hued and everything else keeps its own color. Each (graphic, hue) pair is baked
    /// once on the CPU into a private atlas the first time it is drawn. Main (render) thread only.
    /// </summary>
    internal static class BrickDyeArt
    {
        // A pixel counts as brick when it is dull (low saturation), not near-black and not bluish.
        private const float MAX_SATURATION = 0.38f;
        private const int MIN_BRIGHTNESS = 5; // of 31

        private const int ATLAS_SIZE = 2048;

        private static readonly Dictionary<uint, SpriteInfo> _cache = new();
        private static TextureAtlas? _atlas;

        /// <summary>Returns the brick-dyed sprite for a graphic, baking it on first use.</summary>
        /// <param name="graphic">Static art ID.</param>
        /// <param name="hue">Dye hue (non-zero).</param>
        /// <returns>The baked sprite, or <see cref="SpriteInfo.Empty"/> when the art can't be loaded.</returns>
        public static ref readonly SpriteInfo Get(ushort graphic, ushort hue)
        {
            uint key = ((uint)hue << 16) | graphic;

            if (!_cache.ContainsKey(key))
            {
                _cache[key] = Bake(graphic, hue);
            }

            return ref System.Runtime.InteropServices.CollectionsMarshal.GetValueRefOrNullRef(_cache, key);
        }

        private static SpriteInfo Bake(ushort graphic, ushort hue)
        {
            ArtInfo art = Client.Game.UO.Arts.GetArtPixels(graphic);

            if (art.Pixels.IsEmpty || art.Width <= 0 || art.Height <= 0)
            {
                return SpriteInfo.Empty;
            }

            HuesLoader hues = Client.Game.UO.FileManager.Hues;
            uint[] pixels = new uint[art.Width * art.Height];

            for (int i = 0; i < pixels.Length; i++)
            {
                uint pixel = art.Pixels[i];
                pixels[i] = pixel != 0 && IsBrick(pixel) ? hues.ApplyHueRgba8888(HuesHelper.Color32To16(pixel), hue) | 0xFF_00_00_00 : pixel;
            }

            _atlas ??= new TextureAtlas(Client.Game.GraphicsDevice, ATLAS_SIZE, ATLAS_SIZE, SurfaceFormat.Color);

            var sprite = new SpriteInfo();
            sprite.Texture = _atlas.AddSprite(pixels, art.Width, art.Height, out sprite.UV);

            return sprite;
        }

        private static bool IsBrick(uint pixel)
        {
            ushort c16 = HuesHelper.Color32To16(pixel);
            int r = (c16 >> 10) & 0x1F;
            int g = (c16 >> 5) & 0x1F;
            int b = c16 & 0x1F;

            int max = Math.Max(r, Math.Max(g, b));
            int min = Math.Min(r, Math.Min(g, b));

            return max >= MIN_BRIGHTNESS && r >= b && (max - min) <= MAX_SATURATION * max;
        }
    }
}

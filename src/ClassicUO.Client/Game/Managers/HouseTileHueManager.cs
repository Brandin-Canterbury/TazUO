#nullable enable
using System.Collections.Generic;
using ClassicUO.Game.GameObjects;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// SeriousFun: per-piece hues for house components (walls, floors, stairs...) that players dyed with a dye tub.
    /// The UO protocol carries no hue per multi component, so the server sends them separately with 0xBF sub-command
    /// 0x5348 and they are re-applied every time a house's components are rebuilt. Main thread only.
    /// </summary>
    internal static class HouseTileHueManager
    {
        public const byte OpReset = 0, OpReplace = 1, OpSet = 2;

        private static readonly Dictionary<uint, Dictionary<long, ushort>> _hues = new();

        /// <summary>One dyed piece: offset from the house's location, art ID and hue (0 clears it).</summary>
        public struct Entry
        {
            public sbyte X, Y, Z;
            public ushort Graphic, Hue;
        }

        private static long Key(int x, int y, int z, ushort graphic)
        {
            return ((long)(x & 0xFFFF) << 40) | ((long)(y & 0xFFFF) << 24) | ((long)(z & 0xFF) << 16) | graphic;
        }

        /// <summary>Stores a server update and recolors the house if it is loaded.</summary>
        /// <param name="world">Current world; may be null before login completes.</param>
        /// <param name="op">0 forget every house, 1 replace this house's hues, 2 change these pieces.</param>
        /// <param name="serial">House serial (ignored for reset).</param>
        /// <param name="entries">Pieces and their hues.</param>
        public static void Receive(World? world, byte op, uint serial, List<Entry> entries)
        {
            if (op == OpReset)
            {
                var serials = new List<uint>(_hues.Keys);
                _hues.Clear();

                foreach (uint s in serials)
                {
                    Recolor(world, s);
                }

                return;
            }

            if (!_hues.TryGetValue(serial, out Dictionary<long, ushort>? hues))
            {
                _hues[serial] = hues = new Dictionary<long, ushort>();
            }

            if (op == OpReplace)
            {
                hues.Clear();
            }

            foreach (Entry entry in entries)
            {
                long key = Key(entry.X, entry.Y, entry.Z, entry.Graphic);

                if (entry.Hue == 0)
                {
                    hues.Remove(key);
                }
                else
                {
                    hues[key] = entry.Hue;
                }
            }

            Recolor(world, serial);

            if (hues.Count == 0)
            {
                _hues.Remove(serial);
            }
        }

        /// <summary>
        /// Applies dyed hues to a house's components. Call after its components are (re)built; pieces without a dye
        /// get the house item's own hue, as the client otherwise would.
        /// </summary>
        /// <param name="world">Current world.</param>
        /// <param name="house">The house whose components were just built.</param>
        public static void Apply(World world, House house)
        {
            if (_hues.TryGetValue(house.Serial, out Dictionary<long, ushort>? hues) && hues.Count > 0)
            {
                Paint(world, house, hues);
            }
        }

        private static void Recolor(World? world, uint serial)
        {
            if (world != null && world.HouseManager.TryGetHouse(serial, out House house))
            {
                _hues.TryGetValue(serial, out Dictionary<long, ushort>? hues);
                Paint(world, house, hues);
            }
        }

        private static void Paint(World world, House house, Dictionary<long, ushort>? hues)
        {
            Item? item = world.Items.Get(house.Serial);

            if (item == null)
            {
                return;
            }

            foreach (Multi component in house.Components)
            {
                ushort hue = item.Hue;

                if (hues != null)
                {
                    long key = Key(component.X - item.X, component.Y - item.Y, component.Z - item.Z, component.Graphic);

                    if (hues.TryGetValue(key, out ushort dyed))
                    {
                        hue = dyed;
                    }
                }

                component.Hue = hue;
            }
        }
    }
}

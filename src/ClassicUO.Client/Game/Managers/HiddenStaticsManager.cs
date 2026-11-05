#nullable enable
using System.Collections.Generic;
using ClassicUO.Game.GameObjects;
using ClassicUO.Game.Map;

namespace ClassicUO.Game.Managers
{
    /// <summary>
    /// SeriousFun: map statics the server has removed (trees cleared by player outposts). The server sends them with
    /// 0xBF sub-command 0x5346; hidden statics are skipped when chunks load and removed from chunks already loaded,
    /// so they are neither drawn nor block movement.
    /// </summary>
    internal static class HiddenStaticsManager
    {
        public const byte OpShow = 0, OpHide = 1, OpReset = 2;

        private static readonly object _sync = new object();
        private static readonly Dictionary<int, HashSet<long>> _hidden = new Dictionary<int, HashSet<long>>();
        private static int _total;

        /// <summary>One removed static: tile position, altitude and original art ID.</summary>
        public struct Entry
        {
            public ushort X, Y, Graphic;
            public sbyte Z;
        }

        private static long Key(int x, int y, sbyte z, ushort graphic)
        {
            return ((long)(x & 0xFFFF) << 40) | ((long)(y & 0xFFFF) << 24) | ((long)(byte)z << 16) | graphic;
        }

        /// <summary>Whether a map static should be skipped. Safe to call from the async chunk loader.</summary>
        /// <param name="map">Map index.</param>
        /// <param name="x">World X.</param>
        /// <param name="y">World Y.</param>
        /// <param name="z">Static altitude.</param>
        /// <param name="graphic">Original static art ID (before season swaps).</param>
        /// <returns>True when the server removed this static.</returns>
        public static bool IsHidden(int map, int x, int y, sbyte z, ushort graphic)
        {
            if (_total == 0)
            {
                return false;
            }

            lock (_sync)
            {
                return _hidden.TryGetValue(map, out HashSet<long> set) && set.Contains(Key(x, y, z, graphic));
            }
        }

        /// <summary>Applies a server hide/show/reset list and updates chunks already loaded for the current map.</summary>
        /// <param name="world">Current world; may be null before login completes.</param>
        /// <param name="op">0 show, 1 hide, 2 reset everything.</param>
        /// <param name="map">Map index the entries belong to.</param>
        /// <param name="entries">Statics to hide or show.</param>
        public static void Apply(World? world, byte op, int map, List<Entry> entries)
        {
            if (op == OpReset)
            {
                lock (_sync)
                {
                    _hidden.Clear();
                    _total = 0;
                }

                return;
            }

            lock (_sync)
            {
                if (!_hidden.TryGetValue(map, out HashSet<long> set))
                {
                    _hidden[map] = set = new HashSet<long>();
                }

                foreach (Entry entry in entries)
                {
                    if (op == OpHide)
                    {
                        set.Add(Key(entry.X, entry.Y, entry.Z, entry.Graphic));
                    }
                    else
                    {
                        set.Remove(Key(entry.X, entry.Y, entry.Z, entry.Graphic));
                    }
                }

                _total = 0;

                foreach (HashSet<long> s in _hidden.Values)
                {
                    _total += s.Count;
                }
            }

            Map.Map? current = world?.Map;

            if (current == null || current.Index != map)
            {
                return; // Applied when that map's chunks load.
            }

            foreach (Entry entry in entries)
            {
                Chunk chunk = current.GetChunk(entry.X, entry.Y, false);

                if (chunk == null || chunk.IsDestroyed)
                {
                    continue;
                }

                if (op == OpHide)
                {
                    RemoveFromChunk(chunk, entry);
                }
                else
                {
                    AddToChunk(world!, chunk, entry);
                }
            }
        }

        private static void RemoveFromChunk(Chunk chunk, Entry entry)
        {
            GameObject? obj = chunk.GetHeadObject(entry.X & 7, entry.Y & 7);

            while (obj != null)
            {
                GameObject? next = obj.TNext;

                if (obj is Static staticObj && staticObj.OriginalGraphic == entry.Graphic && staticObj.Z == entry.Z)
                {
                    staticObj.Destroy();
                }

                obj = next;
            }
        }

        private static void AddToChunk(World world, Chunk chunk, Entry entry)
        {
            GameObject? obj = chunk.GetHeadObject(entry.X & 7, entry.Y & 7);

            for (; obj != null; obj = obj.TNext)
            {
                if (obj is Static staticObj && staticObj.OriginalGraphic == entry.Graphic && staticObj.Z == entry.Z)
                {
                    return; // Already there.
                }
            }

            int pos = ((entry.Y & 7) << 3) + (entry.X & 7);
            Static restored = Static.Create(world, entry.Graphic, 0, pos);
            restored.X = entry.X;
            restored.Y = entry.Y;
            restored.Z = entry.Z;
            restored.UpdateScreenPosition();

            chunk.AddGameObject(restored, entry.X & 7, entry.Y & 7);
        }
    }
}

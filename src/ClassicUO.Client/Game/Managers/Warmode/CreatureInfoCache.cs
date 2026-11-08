#nullable enable

using System;
using System.Collections.Generic;
using ClassicUO.IO;
using ClassicUO.Network;

namespace ClassicUO.Game.Managers.Warmode;

/// <summary>
/// Creature type and owner, asked of SeriousFun servers (0xBF / 0x534A) so the Warmode agent can rank creatures by
/// type ("orc") even when they carry random names ("Grukk"), and leave summons and pets of friendly players alone.
/// </summary>
/// <remarks>
/// Main thread only. <see cref="Request"/> queues serials and <see cref="Flush"/> sends them in batches; the answer
/// lands through <see cref="Receive"/>. Servers that never answer leave <see cref="ServerSupports"/> false, and
/// callers then behave as if this cache did not exist.
/// </remarks>
internal sealed class CreatureInfoCache
{
    #region Public accessors

    public static CreatureInfoCache Instance { get; } = new();

    /// <summary>True once the server has answered a request.</summary>
    public bool ServerSupports { get; private set; }

    #endregion

    #region Private members

    private const int MAX_PER_REQUEST = 64;
    private const int MAX_ENTRIES = 4096;
    private const long SEND_INTERVAL_MS = 250;
    private const long RETRY_MS = 5_000;

    private readonly Dictionary<uint, CreatureInfo> _info = new();
    private readonly Dictionary<uint, long> _pending = new();
    private readonly List<uint> _queue = new();
    private long _nextSend;

    #endregion

    #region Public methods

    /// <summary>The cached answer for a creature.</summary>
    /// <param name="serial">The creature.</param>
    /// <param name="info">Its type and owner, when known.</param>
    /// <returns>False until the server has answered for it.</returns>
    public bool TryGet(uint serial, out CreatureInfo info) => _info.TryGetValue(serial, out info);

    /// <summary>Queues a creature for the next request unless it is known or recently asked.</summary>
    /// <param name="serial">The creature.</param>
    public void Request(uint serial)
    {
        if (_info.ContainsKey(serial))
            return;

        long now = Environment.TickCount64;

        if (_pending.TryGetValue(serial, out long asked) && now - asked < RETRY_MS)
            return;

        _pending[serial] = now;
        _queue.Add(serial);
    }

    /// <summary>Sends queued serials, at most every 250 ms; call once per tick.</summary>
    public void Flush()
    {
        long now = Environment.TickCount64;

        if (_queue.Count == 0 || now < _nextSend)
            return;

        _nextSend = now + SEND_INTERVAL_MS;

        int count = Math.Min(MAX_PER_REQUEST, _queue.Count);
        AsyncNetClient.Socket.Send_CreatureInfoRequest(_queue.GetRange(0, count));
        _queue.RemoveRange(0, count);
    }

    /// <summary>Reads a 0xBF / 0x534A answer.</summary>
    /// <param name="p">Reader positioned after the sub-command.</param>
    public void Receive(ref StackDataReader p)
    {
        ServerSupports = true;

        if (_info.Count > MAX_ENTRIES)
            _info.Clear();

        ushort count = p.ReadUInt16BE();

        for (int i = 0; i < count; i++)
        {
            uint serial = p.ReadUInt32BE();
            uint master = p.ReadUInt32BE();
            bool properName = p.ReadUInt8() != 0;
            string kind = p.ReadASCII(p.ReadUInt8());

            _info[serial] = new CreatureInfo(master, properName, kind);
            _pending.Remove(serial);
        }
    }

    #endregion
}

/// <summary>What the server said about a creature.</summary>
/// <param name="Master">Its owner or summoner, 0 when wild.</param>
/// <param name="ProperName">True when its name is personal ("Grukk") rather than a description ("an orc").</param>
/// <param name="Kind">Its type in words, e.g. "orcish mage".</param>
internal readonly record struct CreatureInfo(uint Master, bool ProperName, string Kind);

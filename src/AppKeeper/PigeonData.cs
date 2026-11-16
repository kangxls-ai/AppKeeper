using System;
using System.Collections.Generic;

namespace AppKeeper;

internal sealed class PigeonData
{
    public int Port { get; set; } = 48620;
    public string Share { get; set; } = "share";
    public long AutoLimit { get; set; } = 1_000_000_000;
    public List<PigeonPeer> Peers { get; set; } = [];
    public List<PigeonItem> Items { get; set; } = [];
}

internal sealed record PigeonPeer(string Key, string Name, string Address, int Port, string State, bool Auto);

internal sealed record PigeonItem(Guid Id, string Peer, bool Outgoing, string Kind, string Value,
    DateTimeOffset At, string State, long Size = 0, string? Path = null, string? Digest = null,
    long Progress = 0, bool Archived = false, DateTime? SourceModified = null);

internal sealed record Discovery(string Key, string Name, int Port);
internal sealed record DiscoveredPeer(Discovery Computer, string Address, DateTimeOffset LastSeen);

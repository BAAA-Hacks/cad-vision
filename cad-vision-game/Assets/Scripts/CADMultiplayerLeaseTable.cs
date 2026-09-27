using System;
using System.Collections.Generic;
using System.Linq;

/// <summary>Host-side, ID-based manipulation leases. The network sender is the owner.</summary>
public sealed class CADMultiplayerLeaseTable
{
    public const string ModelId = "$model";
    private readonly Func<string, bool> known;
    private readonly Func<string, string> parentOf;
    private readonly Dictionary<ulong, Lease> leases = new();
    private ulong nextToken = 1;
    private const double TimeoutSeconds = 3;

    private sealed class Lease
    {
        public ulong Owner;
        public ulong Token;
        public HashSet<string> Targets;
        public double LastUsed;
    }

    public CADMultiplayerLeaseTable(Func<string, bool> isKnown, Func<string, string> getParent)
    {
        known = isKnown;
        parentOf = getParent;
    }

    public bool TryAcquire(ulong owner, IEnumerable<string> ids, double now,
        out ulong token, out string reason)
    {
        token = 0;
        reason = null;
        if (ids == null) { reason = "Nothing selected"; return false; }
        var requested = new HashSet<string>(ids.Where(id => !string.IsNullOrEmpty(id)));
        if (requested.Count == 0 || requested.Count > 32 ||
            requested.Any(id => id != ModelId && !known(id)))
        { reason = "Unknown selection"; return false; }
        if (requested.Contains(ModelId) && requested.Count != 1)
        { reason = "Model and parts cannot be held together"; return false; }
        foreach (string a in requested)
        foreach (string b in requested)
            if (a != b && IsAncestor(a, b))
            { reason = "Select transform roots only"; return false; }

        Expire(now);
        foreach (Lease existing in leases.Values)
        {
            if (existing.Owner == owner && existing.Targets.SetEquals(requested))
            {
                existing.LastUsed = now;
                token = existing.Token;
                return true;
            }
            if (existing.Targets.Any(a => requested.Any(b => Conflicts(a, b))))
            { reason = "Another person is moving this part"; return false; }
        }
        if (leases.Values.Any(l => l.Owner == owner))
        { reason = "Finish your current grab first"; return false; }
        token = nextToken++;
        leases[token] = new Lease { Owner = owner, Token = token,
            Targets = requested, LastUsed = now };
        return true;
    }

    public bool Renew(ulong owner, ulong token, string id, double now)
    {
        if (!leases.TryGetValue(token, out Lease lease) || lease.Owner != owner ||
            !lease.Targets.Contains(id) || now - lease.LastUsed > TimeoutSeconds)
            return false;
        lease.LastUsed = now;
        return true;
    }

    public bool Owns(ulong owner, ulong token, string id, double now) =>
        leases.TryGetValue(token, out Lease lease) && lease.Owner == owner &&
        lease.Targets.Contains(id) && now - lease.LastUsed <= TimeoutSeconds;

    public void Release(ulong owner, ulong token)
    {
        if (leases.TryGetValue(token, out Lease lease) && lease.Owner == owner)
            leases.Remove(token);
    }

    public void ReleaseOwner(ulong owner)
    {
        foreach (ulong token in leases.Where(p => p.Value.Owner == owner)
            .Select(p => p.Key).ToArray())
            leases.Remove(token);
    }

    public void Clear() => leases.Clear();

    public void Expire(double now)
    {
        foreach (ulong token in leases.Where(p => now - p.Value.LastUsed > TimeoutSeconds)
            .Select(p => p.Key).ToArray())
            leases.Remove(token);
    }

    private bool Conflicts(string a, string b) => a == b || IsAncestor(a, b) || IsAncestor(b, a);

    private bool IsAncestor(string ancestor, string descendant)
    {
        if (ancestor == ModelId) return true;
        if (descendant == ModelId) return false;
        var visited = new HashSet<string>();
        for (string parent = parentOf(descendant); parent != null && visited.Add(parent);
            parent = parentOf(parent))
            if (parent == ancestor) return true;
        return false;
    }
}

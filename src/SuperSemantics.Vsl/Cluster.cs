namespace SuperSemantics.Vsl;

/// <summary>
/// A Cluster-level Inadmissible State. <see cref="Note"/> is a field, not a comment: the specification's
/// warning ("Individual system Gamma &gt; 1 is insufficient. Cluster-level governance is required.")
/// should be readable by anything built on this package.
/// </summary>
public sealed record ClusterInadmissibleState(string Name, string Description, string Signature, string Note = "");

/// <summary>A Cluster-level PreNode: fires on cross-constituent signatures no single constituent's
/// PreNode would catch.</summary>
public sealed record ClusterPreNode
{
    /// <summary>Creates a Cluster PreNode.</summary>
    public ClusterPreNode(string name, string triggerDescription, string authority, FormationLayerIntervention? intervention = null, Fallback? fallback = null)
    {
        Name = name;
        TriggerDescription = triggerDescription;
        Authority = authority;
        Intervention = intervention;
        Fallback = fallback ?? new Fallback();
    }

    /// <summary>Name.</summary>
    public string Name { get; }
    /// <summary>What triggers it.</summary>
    public string TriggerDescription { get; }
    /// <summary>The authority responsible.</summary>
    public string Authority { get; }
    /// <summary>Optional intervention.</summary>
    public FormationLayerIntervention? Intervention { get; }
    /// <summary>The Fallback.</summary>
    public Fallback Fallback { get; }
}

/// <summary>
/// A higher-order identity formed by several coordinating systems. The constructor checks that every
/// declared constituent is covered by <see cref="ClusterKey"/>, so a Cluster's governance can never
/// silently miss one of its members (the Flash Crash shape: individual Gamma above 1, Cluster Gamma 0).
/// </summary>
public sealed class Cluster
{
    /// <summary>Creates a Cluster.</summary>
    public Cluster(
        string name,
        ClusterKey clusterKey,
        IEnumerable<IdentityKey> constituents,
        IEnumerable<ClusterInadmissibleState>? clusterInadmissibleStates = null,
        IEnumerable<ClusterPreNode>? clusterPreNodes = null,
        IEnumerable<Invariant>? invariants = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(clusterKey);
        ArgumentNullException.ThrowIfNull(constituents);
        var members = constituents.ToArray();
        var covered = clusterKey.ConstituentKeys.ToHashSet();
        var missing = members.Where(c => !covered.Contains(c)).ToArray();
        if (missing.Length > 0)
        {
            throw new ArgumentException(
                $"Cluster '{name}' declares constituents not covered by its own cluster key: "
                + $"[{string.Join(", ", missing.Select(m => $"'{m.Value}'"))}]. A Cluster's ClusterKey must be "
                + "derived from (at least) all of its declared constituents.",
                nameof(constituents));
        }

        Name = name;
        ClusterKey = clusterKey;
        Constituents = members;
        ClusterInadmissibleStates = clusterInadmissibleStates?.ToArray() ?? [];
        ClusterPreNodes = clusterPreNodes?.ToArray() ?? [];
        Invariants = invariants?.ToArray() ?? [];
    }

    /// <summary>Name.</summary>
    public string Name { get; }
    /// <summary>The composite key.</summary>
    public ClusterKey ClusterKey { get; }
    /// <summary>The constituents.</summary>
    public IReadOnlyList<IdentityKey> Constituents { get; }
    /// <summary>Cluster-level Inadmissible States.</summary>
    public IReadOnlyList<ClusterInadmissibleState> ClusterInadmissibleStates { get; }
    /// <summary>Cluster-level PreNodes.</summary>
    public IReadOnlyList<ClusterPreNode> ClusterPreNodes { get; }
    /// <summary>Cluster-level Invariants.</summary>
    public IReadOnlyList<Invariant> Invariants { get; }

    /// <summary>A new Cluster with <paramref name="identityKey"/> added and the key recomputed.</summary>
    public Cluster AddConstituent(IdentityKey identityKey)
    {
        ArgumentNullException.ThrowIfNull(identityKey);
        var newConstituents = Constituents.Append(identityKey).ToArray();
        return new Cluster(Name, ClusterKey.FromConstituents(newConstituents), newConstituents, ClusterInadmissibleStates, ClusterPreNodes, Invariants);
    }
}

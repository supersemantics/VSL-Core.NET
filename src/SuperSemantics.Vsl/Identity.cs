using System.Security.Cryptography;
using System.Text;

namespace SuperSemantics.Vsl;

/// <summary>A stable identifier scoping counters, limits and process instances.</summary>
public sealed record IdentityKey(string Value)
{
    /// <summary>A new key, <c>{prefix}-{uuid}</c>.</summary>
    public static IdentityKey Generate(string prefix = "vsl") => new($"{prefix}-{Guid.NewGuid()}");

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// The composite identity shared by every constituent of a Cluster. Deliberately not an
/// <see cref="IdentityKey"/>: a Cluster's sufficiency is a different question from any one
/// constituent's (individual Gamma above 1 can still mean Cluster Gamma of 0), so a ClusterKey is never
/// accepted where a single-system key belongs.
/// </summary>
public sealed class ClusterKey : IEquatable<ClusterKey>
{
    /// <summary>Creates a key from an explicit value and constituents.</summary>
    public ClusterKey(string value, IEnumerable<IdentityKey> constituentKeys)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentNullException.ThrowIfNull(constituentKeys);
        Value = value;
        ConstituentKeys = constituentKeys.ToArray();
    }

    /// <summary>The composite value.</summary>
    public string Value { get; }

    /// <summary>The constituents, in the order given.</summary>
    public IReadOnlyList<IdentityKey> ConstituentKeys { get; }

    /// <summary>
    /// Derives the value from a SHA-256 of the sorted constituent values, so the same set always gives
    /// the same key in any order. Same value as vsl-core's <c>ClusterKey.from_constituents</c>.
    /// </summary>
    public static ClusterKey FromConstituents(IEnumerable<IdentityKey> constituentKeys)
    {
        ArgumentNullException.ThrowIfNull(constituentKeys);
        var keys = constituentKeys.ToArray();
        var sorted = keys.Select(k => k.Value).OrderBy(v => v, CodePointComparer.Instance);
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", sorted))));
        return new ClusterKey($"cluster-{digest[..16]}", keys);
    }

    /// <inheritdoc />
    public bool Equals(ClusterKey? other) =>
        other is not null && Value == other.Value && ConstituentKeys.SequenceEqual(other.ConstituentKeys);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as ClusterKey);

    /// <inheritdoc />
    public override int GetHashCode() => Value.GetHashCode(StringComparison.Ordinal);

    /// <inheritdoc />
    public override string ToString() => Value;
}

/// <summary>
/// The evidence a Re-Enablement must carry: a root-cause analysis and a specification-update proposal,
/// both non-empty.
/// </summary>
public sealed record Evidence
{
    /// <summary>Creates evidence; both strings must be non-blank.</summary>
    public Evidence(string rootCauseAnalysis, string specificationUpdateProposal)
    {
        if (string.IsNullOrWhiteSpace(rootCauseAnalysis))
        {
            throw new ArgumentException("rootCauseAnalysis must be a non-empty evidence string.", nameof(rootCauseAnalysis));
        }

        if (string.IsNullOrWhiteSpace(specificationUpdateProposal))
        {
            throw new ArgumentException("specificationUpdateProposal must be a non-empty evidence string.", nameof(specificationUpdateProposal));
        }

        RootCauseAnalysis = rootCauseAnalysis;
        SpecificationUpdateProposal = specificationUpdateProposal;
    }

    /// <summary>What went wrong and why.</summary>
    public string RootCauseAnalysis { get; }

    /// <summary>The proposed change to the specification.</summary>
    public string SpecificationUpdateProposal { get; }
}

/// <summary>
/// One execution of a process, with its own identifier and history, scoped by an
/// <see cref="IdentityKey"/>. Immutable: Re-Enablement produces a new instance.
/// </summary>
public sealed record Instance
{
    /// <summary>Creates an instance from explicit values (for example when loading a record).</summary>
    public Instance(string instanceId, IdentityKey identityKey, double? createdAt = null, string? predecessorInstanceId = null, int generation = 0)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(identityKey);
        InstanceId = instanceId;
        IdentityKey = identityKey;
        CreatedAt = createdAt ?? VslClock.Now();
        PredecessorInstanceId = predecessorInstanceId;
        Generation = generation;
    }

    /// <summary>This instance's id.</summary>
    public string InstanceId { get; }

    /// <summary>The identity this instance runs under.</summary>
    public IdentityKey IdentityKey { get; }

    /// <summary>Creation time, in seconds since the Unix epoch.</summary>
    public double CreatedAt { get; }

    /// <summary>The instance this one replaced through Re-Enablement, if any.</summary>
    public string? PredecessorInstanceId { get; }

    /// <summary>0 for a fresh instance, one more for each Re-Enablement.</summary>
    public int Generation { get; }

    /// <summary>A fresh instance; a new <see cref="IdentityKey"/> is generated if none is given.</summary>
    public static Instance New(IdentityKey? identityKey = null) =>
        new(Guid.NewGuid().ToString(), identityKey ?? IdentityKey.Generate());

    /// <summary>
    /// The bare Re-Enablement mechanism: a brand-new instance with a new identity key. Internal on
    /// purpose. In vsl-core it is <c>_re_enable</c>, a naming convention Python cannot enforce; here
    /// the compiler enforces it, so the only way out of a Terminal State from outside this package is
    /// <see cref="Governance.RequestReEnablement"/>, which also produces the
    /// <see cref="HumanAuthorisedTransition"/> that authorises it.
    /// </summary>
    internal Instance ReEnable(Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new Instance(
            Guid.NewGuid().ToString(),
            IdentityKey.Generate(),
            predecessorInstanceId: InstanceId,
            generation: Generation + 1);
    }
}

/// <summary>Orders strings by Unicode code point, the order Python's <c>sorted()</c> uses.</summary>
internal sealed class CodePointComparer : IComparer<string>
{
    public static readonly CodePointComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var a = x.EnumerateRunes().GetEnumerator();
        var b = y.EnumerateRunes().GetEnumerator();
        while (true)
        {
            var hasA = a.MoveNext();
            var hasB = b.MoveNext();
            if (!hasA || !hasB) return hasA.CompareTo(hasB);
            var c = a.Current.Value.CompareTo(b.Current.Value);
            if (c != 0) return c;
        }
    }
}

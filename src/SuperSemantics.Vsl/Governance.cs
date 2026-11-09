using System.Security.Cryptography;
using System.Text;

namespace SuperSemantics.Vsl;

/// <summary>A named role or body authorised to approve rule changes, exceptions, Human-Authorised
/// Transitions and Specification Updates.</summary>
public sealed record GovernanceAuthority
{
    /// <summary>Creates an authority.</summary>
    public GovernanceAuthority(string name, string escalationContact, IEnumerable<string>? secondaryAuthorities = null, double? responseTimeSlaSeconds = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        EscalationContact = escalationContact ?? "";
        SecondaryAuthorities = secondaryAuthorities?.ToArray() ?? [];
        ResponseTimeSlaSeconds = responseTimeSlaSeconds;
    }

    /// <summary>Name.</summary>
    public string Name { get; }
    /// <summary>Escalation contact.</summary>
    public string EscalationContact { get; }
    /// <summary>Secondary authorities.</summary>
    public IReadOnlyList<string> SecondaryAuthorities { get; }
    /// <summary>Response-time commitment, in seconds.</summary>
    public double? ResponseTimeSlaSeconds { get; }
}

/// <summary>
/// A transition that needs explicit, auditable human approval: the only sanctioned way out of a
/// Terminal State. No timeout, no default-approve, no automatic retry. Prefer
/// <see cref="Authorise"/> over the constructor.
/// </summary>
public sealed record HumanAuthorisedTransition
{
    /// <summary>Creates the record. <paramref name="authorisedBy"/> must name a real approver. The
    /// evidence hash is SHA-256 of the evidence joined by "|", the same value vsl-core computes.</summary>
    public HumanAuthorisedTransition(
        GovernanceAuthority authority,
        string authorisedBy,
        IEnumerable<string> evidence,
        double? timestamp = null,
        string authorisationEvidenceHash = "")
    {
        ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.IsNullOrWhiteSpace(authorisedBy))
        {
            throw new ArgumentException("authorisedBy must name a real, non-empty human approver.", nameof(authorisedBy));
        }

        Authority = authority;
        AuthorisedBy = authorisedBy;
        Evidence = evidence.ToArray();
        Timestamp = timestamp ?? VslClock.Now();
        AuthorisationEvidenceHash = string.IsNullOrEmpty(authorisationEvidenceHash)
            ? Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|", Evidence))))
            : authorisationEvidenceHash;
    }

    /// <summary>The approving authority.</summary>
    public GovernanceAuthority Authority { get; }
    /// <summary>The named human approver.</summary>
    public string AuthorisedBy { get; }
    /// <summary>The evidence strings.</summary>
    public IReadOnlyList<string> Evidence { get; }
    /// <summary>When it was authorised, in seconds since the Unix epoch.</summary>
    public double Timestamp { get; }
    /// <summary>SHA-256 hex of the evidence.</summary>
    public string AuthorisationEvidenceHash { get; }

    /// <summary>The sanctioned way to create one: a real authority, a named approver and validated
    /// <see cref="Vsl.Evidence"/>.</summary>
    public static HumanAuthorisedTransition Authorise(GovernanceAuthority authority, string authorisedBy, Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        return new HumanAuthorisedTransition(authority, authorisedBy, [evidence.RootCauseAnalysis, evidence.SpecificationUpdateProposal]);
    }
}

/// <summary>Governance operations.</summary>
public static class Governance
{
    /// <summary>
    /// The sanctioned path out of a Terminal State: returns the new, re-enabled instance together with
    /// the <see cref="HumanAuthorisedTransition"/> that authorises it, so neither exists without the
    /// other. Write a RE_ENABLEMENT entry for the instance and a HUMAN_AUTHORISED_TRANSITION entry for
    /// the transition.
    /// </summary>
    public static (Instance NewInstance, HumanAuthorisedTransition Transition) RequestReEnablement(
        Instance instance,
        GovernanceAuthority authority,
        string authorisedBy,
        Evidence evidence)
    {
        ArgumentNullException.ThrowIfNull(instance);
        var transition = HumanAuthorisedTransition.Authorise(authority, authorisedBy, evidence);
        return (instance.ReEnable(evidence), transition);
    }
}

/// <summary>
/// The result of checking a claimed attribute against an authoritative record. A plain result type:
/// the caller performs the domain-specific comparison.
/// </summary>
public sealed record Proofing(bool Verified, string VerifiedAgainst)
{
    /// <summary>When the check ran, in seconds since the Unix epoch.</summary>
    public double VerifiedAt { get; init; } = VslClock.Now();
}

/// <summary>A link between a verified factor and an approved escalation path. Cannot be created from
/// unverified <see cref="Proofing"/>.</summary>
public sealed record Binding
{
    /// <summary>Creates a binding; throws if <paramref name="proofing"/> is not verified.</summary>
    public Binding(Proofing proofing, string escalationPath, double? boundAt = null)
    {
        ArgumentNullException.ThrowIfNull(proofing);
        if (!proofing.Verified)
        {
            throw new ArgumentException(
                "Cannot create a Binding from unverified Proofing: Binding requires prior successful Proofing to have already occurred.",
                nameof(proofing));
        }

        Proofing = proofing;
        EscalationPath = escalationPath;
        BoundAt = boundAt ?? VslClock.Now();
    }

    /// <summary>The verified proofing.</summary>
    public Proofing Proofing { get; }
    /// <summary>The escalation path.</summary>
    public string EscalationPath { get; }
    /// <summary>When it was bound.</summary>
    public double BoundAt { get; }
}

/// <summary>
/// Policy-as-Code: a versioned bundle of PreNodes, Invariants and Terminal States. A new version needs
/// a named approving authority (required at construction) and a <see cref="Binding"/> (which needs
/// verified Proofing) before <see cref="Activate"/> succeeds.
/// </summary>
public sealed record Specification
{
    /// <summary>Creates a draft specification.</summary>
    public Specification(
        string name,
        string version,
        GovernanceAuthority approvedBy,
        IEnumerable<PreNode>? preNodes = null,
        IEnumerable<Invariant>? invariants = null,
        IEnumerable<TerminalState>? terminalStates = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(approvedBy);
        Name = name;
        Version = version;
        ApprovedBy = approvedBy;
        PreNodes = preNodes?.ToArray() ?? [];
        Invariants = invariants?.ToArray() ?? [];
        TerminalStates = terminalStates?.ToArray() ?? [];
    }

    /// <summary>Name.</summary>
    public string Name { get; }
    /// <summary>Version.</summary>
    public string Version { get; }
    /// <summary>The approving authority.</summary>
    public GovernanceAuthority ApprovedBy { get; }
    /// <summary>PreNodes.</summary>
    public IReadOnlyList<PreNode> PreNodes { get; }
    /// <summary>Invariants.</summary>
    public IReadOnlyList<Invariant> Invariants { get; }
    /// <summary>Terminal States.</summary>
    public IReadOnlyList<TerminalState> TerminalStates { get; }
    /// <summary>The binding, once proofed.</summary>
    public Binding? Binding { get; private init; }
    /// <summary>When it was activated; null while a draft.</summary>
    public double? ActivatedAt { get; private init; }

    /// <summary>A copy with <paramref name="binding"/> attached.</summary>
    public Specification WithBinding(Binding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        return this with { Binding = binding };
    }

    /// <summary>An activated copy. Throws if already active or if no Binding is attached.</summary>
    public Specification Activate()
    {
        if (ActivatedAt is not null)
        {
            throw new InvalidOperationException($"Specification '{Name}' v{Version} is already active as of {ActivatedAt}.");
        }

        if (Binding is null)
        {
            throw new InvalidOperationException(
                $"Cannot activate Specification '{Name}' v{Version} without a Binding (which itself requires prior successful Proofing).");
        }

        return this with { ActivatedAt = VslClock.Now() };
    }
}

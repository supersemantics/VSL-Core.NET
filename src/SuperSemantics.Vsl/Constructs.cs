namespace SuperSemantics.Vsl;

// Framework-agnostic on purpose: nothing in this package references an agent framework or a model
// provider SDK. Compiling a PreNode or Invariant into a framework's own hook belongs in a separate
// adapter package, never here.

/// <summary>
/// A monitor: computes a Gamma estimate for the candidate input. An adapter decides what
/// <c>candidateInput</c> means in its framework before calling it.
/// </summary>
public delegate Task<GammaEstimate> DriftMonitor(object? candidateInput);

/// <summary>An Invariant's rule: true if the constraint holds for the candidate input.</summary>
public delegate Task<bool> InvariantRule(object? candidateInput);

/// <summary>A formation-layer intervention, given the Fallback's delta factor.</summary>
public delegate Task<bool> FormationLayerIntervention(double deltaFactor);

/// <summary>
/// A state meeting every required prerequisite and validity condition. Its assurance level is derived
/// from <see cref="AssuranceBasis"/>, never set directly.
/// </summary>
public sealed record AllowedState
{
    /// <summary>Creates an Allowed State.</summary>
    public AllowedState(string name, string description, AssuranceBasis assuranceBasis, string measurement, IEnumerable<string>? validityConditions = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(assuranceBasis);
        Name = name;
        Description = description ?? "";
        AssuranceBasis = assuranceBasis;
        Measurement = measurement ?? "";
        ValidityConditions = validityConditions?.ToArray() ?? [];
    }

    /// <summary>Name.</summary>
    public string Name { get; }
    /// <summary>Description.</summary>
    public string Description { get; }
    /// <summary>The F1/F2 facts.</summary>
    public AssuranceBasis AssuranceBasis { get; }
    /// <summary>How the state is measured.</summary>
    public string Measurement { get; }
    /// <summary>Validity conditions.</summary>
    public IReadOnlyList<string> ValidityConditions { get; }
    /// <summary>Derived from <see cref="AssuranceBasis"/>.</summary>
    public AssuranceLevel AssuranceLevel => AssuranceBasis.DerivedLevel;
}

/// <summary>A state that must not occur; corresponds to a Drift Node.</summary>
public sealed record InadmissibleState(string Name, string Description, string Signature, string? EnergyGapProxy = null);

/// <summary>A deterministic recovery or termination action when a PreNode cannot be satisfied.</summary>
public sealed record Fallback(
    string OnFailure = "INCREASE_DELTA",
    double DeltaFactor = 1.5,
    int MaxRetries = 3,
    string OnMaxRetries = "AUTOMATION_DENIED");

/// <summary>A state from which no further automated transitions are permitted.</summary>
public sealed record TerminalState
{
    /// <summary>Creates a Terminal State.</summary>
    public TerminalState(string name, string description, IEnumerable<string>? entryConditions = null, bool requiresHumanAuthorisedTransition = true)
    {
        ArgumentNullException.ThrowIfNull(name);
        Name = name;
        Description = description ?? "";
        EntryConditions = entryConditions?.ToArray() ?? [];
        RequiresHumanAuthorisedTransition = requiresHumanAuthorisedTransition;
    }

    /// <summary>Name.</summary>
    public string Name { get; }
    /// <summary>Description.</summary>
    public string Description { get; }
    /// <summary>Entry conditions.</summary>
    public IReadOnlyList<string> EntryConditions { get; }
    /// <summary>Whether leaving it needs a Human-Authorised Transition (always true in the specification).</summary>
    public bool RequiresHumanAuthorisedTransition { get; }
}

/// <summary>
/// A persistent constraint that cannot be bypassed. Violation goes straight to a Terminal State, not
/// to a Fallback. <see cref="OnViolation"/> optionally names which one.
/// </summary>
public sealed class Invariant
{
    /// <summary>Creates an Invariant. <paramref name="assuranceBasis"/> is required, as for a PreNode.</summary>
    public Invariant(
        string name,
        string description,
        InvariantRule rule,
        AssuranceBasis assuranceBasis,
        string scope = "ALL_STATES",
        bool cannotBeBypassed = true,
        TerminalState? onViolation = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(assuranceBasis);
        Name = name;
        Description = description ?? "";
        Rule = rule;
        AssuranceBasis = assuranceBasis;
        Scope = scope;
        CannotBeBypassed = cannotBeBypassed;
        OnViolation = onViolation;
    }

    /// <summary>The Invariant's declared name; the name gate-decision records use.</summary>
    public string Name { get; }
    /// <summary>Description.</summary>
    public string Description { get; }
    /// <summary>The rule.</summary>
    public InvariantRule Rule { get; }
    /// <summary>The F1/F2 facts.</summary>
    public AssuranceBasis AssuranceBasis { get; }
    /// <summary>Scope (default "ALL_STATES").</summary>
    public string Scope { get; }
    /// <summary>Always true in the specification.</summary>
    public bool CannotBeBypassed { get; }
    /// <summary>The Terminal State a violation enters, if decided.</summary>
    public TerminalState? OnViolation { get; }
    /// <summary>Derived from <see cref="AssuranceBasis"/>.</summary>
    public AssuranceLevel AssuranceLevel => AssuranceBasis.DerivedLevel;
}

/// <summary>
/// A mandatory prerequisite that must be satisfied before a transition: the central VERBA construct.
/// A transition without a satisfied PreNode is, by definition, Drift.
/// </summary>
public sealed class PreNode
{
    /// <summary>Creates a PreNode. <paramref name="assuranceBasis"/> is required.</summary>
    public PreNode(
        string name,
        string description,
        DriftMonitor monitor,
        AssuranceBasis assuranceBasis,
        double gammaThreshold = Thresholds.RobustGammaDefault,
        FormationLayerIntervention? intervention = null,
        Fallback? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(assuranceBasis);
        fallback ??= new Fallback();
        if (fallback.MaxRetries < 0)
        {
            throw new ArgumentException("Fallback.MaxRetries must be >= 0.", nameof(fallback));
        }

        if (fallback.DeltaFactor <= 0)
        {
            throw new ArgumentException("Fallback.DeltaFactor must be > 0.", nameof(fallback));
        }

        Name = name;
        Description = description ?? "";
        Monitor = monitor;
        AssuranceBasis = assuranceBasis;
        GammaThreshold = gammaThreshold;
        Intervention = intervention;
        Fallback = fallback;
    }

    /// <summary>The PreNode's declared name; the name gate-decision records use.</summary>
    public string Name { get; }
    /// <summary>Description.</summary>
    public string Description { get; }
    /// <summary>The monitor.</summary>
    public DriftMonitor Monitor { get; }
    /// <summary>The F1/F2 facts.</summary>
    public AssuranceBasis AssuranceBasis { get; }
    /// <summary>The Gamma threshold (default 1.1).</summary>
    public double GammaThreshold { get; }
    /// <summary>Optional formation-layer intervention.</summary>
    public FormationLayerIntervention? Intervention { get; }
    /// <summary>The Fallback.</summary>
    public Fallback Fallback { get; }
    /// <summary>Derived from <see cref="AssuranceBasis"/>.</summary>
    public AssuranceLevel AssuranceLevel => AssuranceBasis.DerivedLevel;
}

/// <summary>A process-wide registry of Invariants. Refuses duplicate names rather than overwriting.</summary>
public static class InvariantRegistry
{
    private static readonly List<Invariant> Registry = [];
    private static readonly Lock RegistryLock = new();

    /// <summary>Registers an Invariant. Throws if the name is already registered.</summary>
    public static void Register(Invariant invariant)
    {
        ArgumentNullException.ThrowIfNull(invariant);
        lock (RegistryLock)
        {
            if (Registry.Any(existing => existing.Name == invariant.Name))
            {
                throw new ArgumentException($"An Invariant named '{invariant.Name}' is already registered.", nameof(invariant));
            }

            Registry.Add(invariant);
        }
    }

    /// <summary>A snapshot of the registered Invariants.</summary>
    public static IReadOnlyList<Invariant> Registered()
    {
        lock (RegistryLock)
        {
            return Registry.ToArray();
        }
    }
}

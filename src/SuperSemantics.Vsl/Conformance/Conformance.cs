namespace SuperSemantics.Vsl.Conformance;

/// <summary>
/// A compiled gate for one PreNode or Invariant. It must throw <see cref="AutomationDeniedException"/>
/// (or <see cref="InvariantViolationException"/>) when the check fails and complete normally when it
/// passes, strictly before the guarded side effect can happen: the F1 property the conformance suite
/// checks mechanically.
/// </summary>
public delegate Task CompiledGate(object? candidateInput);

/// <summary>What a framework adapter implements to be VSL-conformant.</summary>
public interface IVslAdapter
{
    /// <summary>Compiles a PreNode into a gate.</summary>
    CompiledGate CompilePreNode(PreNode preNode);

    /// <summary>Compiles an Invariant into a gate.</summary>
    CompiledGate CompileInvariant(Invariant invariant);
}

/// <summary>
/// The simplest conformant adapter: a direct call, with no framework underneath. Proof that the
/// contract can be met at all. It writes nothing to a ledger: ledger writes are the application's
/// responsibility, never the adapter's.
/// </summary>
public sealed class PlainReferenceAdapter : IVslAdapter
{
    /// <inheritdoc />
    public CompiledGate CompilePreNode(PreNode preNode)
    {
        ArgumentNullException.ThrowIfNull(preNode);
        return async candidateInput =>
        {
            var estimate = await preNode.Monitor(candidateInput).ConfigureAwait(false);
            if (!estimate.Sufficient(preNode.GammaThreshold))
            {
                throw new AutomationDeniedException($"Pre-node '{preNode.Name}' insufficient Gamma", "conformance-test");
            }
        };
    }

    /// <inheritdoc />
    public CompiledGate CompileInvariant(Invariant invariant)
    {
        ArgumentNullException.ThrowIfNull(invariant);
        return async candidateInput =>
        {
            if (!await invariant.Rule(candidateInput).ConfigureAwait(false))
            {
                var terminal = invariant.OnViolation?.Name;
                var reason = $"Invariant '{invariant.Name}' violated";
                if (terminal is not null)
                {
                    reason += $" -- entering terminal state '{terminal}'";
                }

                throw new InvariantViolationException(reason, "conformance-test", invariant.Name, terminal);
            }
        };
    }
}

/// <summary>
/// Mechanical proof that an adapter is conformant. Each check builds a real PreNode or Invariant and
/// calls the compiled gate, using a side-effect counter to prove ordering, not just return values.
/// The same five checks as vsl-core's <c>run_conformance_suite</c>.
/// </summary>
public static class ConformanceSuite
{
    // Synthetic formation-layer, full-F2 checks: High is the justified claim for them.
    private static readonly AssuranceBasis HighAssurance = new(true, F2Modification.Full);

    /// <summary>
    /// Runs every check. An empty list means fully conformant. Throws <see cref="ConformanceException"/>
    /// only when the adapter is too broken for the suite to run (a compile method returning null).
    /// </summary>
    public static async Task<IReadOnlyList<string>> RunAsync(IVslAdapter adapter)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        var failures = new List<string>();
        failures.AddRange(await PreNodeBlocksBeforeSideEffect(adapter).ConfigureAwait(false));
        failures.AddRange(await PreNodeAllowsAfterPass(adapter).ConfigureAwait(false));
        failures.AddRange(await InvariantViolationIsSpecificType(adapter).ConfigureAwait(false));
        failures.AddRange(await InvariantPasses(adapter).ConfigureAwait(false));
        failures.AddRange(await GateReusableWithoutStateLeak(adapter).ConfigureAwait(false));
        return failures;
    }

    /// <summary>Synchronous form of <see cref="RunAsync"/>.</summary>
    public static IReadOnlyList<string> Run(IVslAdapter adapter) => RunAsync(adapter).GetAwaiter().GetResult();

    private static CompiledGate Require(IVslAdapter adapter, string method, CompiledGate? gate) =>
        gate ?? throw new ConformanceException(
            $"{adapter.GetType().Name}.{method} did not return a callable gate (got null); the conformance suite cannot run against it.");

    private static async Task<List<string>> PreNodeBlocksBeforeSideEffect(IVslAdapter adapter)
    {
        var failures = new List<string>();
        var counter = 0;
        var preNode = new PreNode(
            "conformance-blocking",
            "Conformance check: below-threshold Gamma must block.",
            _ => Task.FromResult(new GammaEstimate(0.1)),
            HighAssurance,
            gammaThreshold: 1.1);
        var gate = Require(adapter, nameof(IVslAdapter.CompilePreNode), adapter.CompilePreNode(preNode));

        var raised = false;
        try
        {
            await gate("candidate").ConfigureAwait(false);
            counter++;
        }
        catch (AutomationDeniedException)
        {
            raised = true;
        }

        if (!raised)
        {
            failures.Add("Check 1 (PreNode pre-commitment blocking): compiled gate did not raise AutomationDeniedException for an insufficient Gamma estimate.");
        }

        if (counter != 0)
        {
            failures.Add("Check 1 (PreNode pre-commitment blocking): the side-effect counter incremented despite the gate raising -- F1 pre-commitment violated.");
        }

        return failures;
    }

    private static async Task<List<string>> PreNodeAllowsAfterPass(IVslAdapter adapter)
    {
        var failures = new List<string>();
        var counter = 0;
        var preNode = new PreNode(
            "conformance-passing",
            "Conformance check: above-threshold Gamma must pass through.",
            _ => Task.FromResult(new GammaEstimate(5.0)),
            HighAssurance,
            gammaThreshold: 1.1);
        var gate = Require(adapter, nameof(IVslAdapter.CompilePreNode), adapter.CompilePreNode(preNode));

        try
        {
            await gate("candidate").ConfigureAwait(false);
            counter++;
        }
        catch (AutomationDeniedException ex)
        {
            failures.Add($"Check 2 (PreNode pass-through): gate raised unexpectedly for a sufficient Gamma estimate ({ex.Message}).");
            return failures;
        }

        if (counter != 1)
        {
            failures.Add("Check 2 (PreNode pass-through): the side-effect counter did not increment after a passing gate call.");
        }

        return failures;
    }

    private static async Task<List<string>> InvariantViolationIsSpecificType(IVslAdapter adapter)
    {
        var failures = new List<string>();
        var invariant = new Invariant(
            "conformance-invariant-fail",
            "Conformance check: a failing rule must raise InvariantViolation.",
            _ => Task.FromResult(false),
            HighAssurance);
        var gate = Require(adapter, nameof(IVslAdapter.CompileInvariant), adapter.CompileInvariant(invariant));

        try
        {
            await gate("candidate").ConfigureAwait(false);
            failures.Add("Check 3 (Invariant violation specificity): compiled gate did not raise for a failing Invariant rule.");
        }
        catch (InvariantViolationException)
        {
        }
        catch (AutomationDeniedException ex)
        {
            failures.Add($"Check 3 (Invariant violation specificity): gate raised {ex.GetType().Name}, expected the more specific InvariantViolationException.");
        }

        return failures;
    }

    private static async Task<List<string>> InvariantPasses(IVslAdapter adapter)
    {
        var failures = new List<string>();
        var invariant = new Invariant(
            "conformance-invariant-pass",
            "Conformance check: a passing rule must not raise.",
            _ => Task.FromResult(true),
            HighAssurance);
        var gate = Require(adapter, nameof(IVslAdapter.CompileInvariant), adapter.CompileInvariant(invariant));

        try
        {
            await gate("candidate").ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any exception here is itself the failure being reported.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            failures.Add($"Check 4 (Invariant pass-through): gate raised unexpectedly for a passing rule ({ex.GetType().Name}).");
        }

        return failures;
    }

    private static async Task<List<string>> GateReusableWithoutStateLeak(IVslAdapter adapter)
    {
        var failures = new List<string>();
        var preNode = new PreNode(
            "conformance-reusable",
            "Conformance check: a compiled gate must be reusable across calls.",
            input => Task.FromResult(new GammaEstimate(Equals(input, "good") ? 5.0 : 0.1)),
            HighAssurance,
            gammaThreshold: 1.1);
        var gate = Require(adapter, nameof(IVslAdapter.CompilePreNode), adapter.CompilePreNode(preNode));

        try
        {
            await gate("good").ConfigureAwait(false);
        }
        catch (AutomationDeniedException ex)
        {
            failures.Add($"Check 5 (gate reusability): first call with a passing input raised unexpectedly ({ex.Message}).");
        }

        var raised = false;
        try
        {
            await gate("bad").ConfigureAwait(false);
        }
        catch (AutomationDeniedException)
        {
            raised = true;
        }

        if (!raised)
        {
            failures.Add("Check 5 (gate reusability): second call with a failing input did not raise -- state may have leaked from the first (passing) call.");
        }

        try
        {
            await gate("good").ConfigureAwait(false);
        }
        catch (AutomationDeniedException ex)
        {
            failures.Add($"Check 5 (gate reusability): third call with a passing input raised unexpectedly after a prior failing call ({ex.Message}) -- state leaked from the second (failing) call.");
        }

        return failures;
    }
}

namespace SuperSemantics.Vsl;

/// <summary>
/// HIGH / MEDIUM / LOW rating per the F1/F2 test. It measures one thing: how deeply governance shapes
/// what the model produces. Whether a gate blocks the action before it happens is F1, reported on its
/// own: a gate with F1 = true and F2 = None is Low here, yet it still stops every action it denies.
/// Get it from <see cref="AssuranceBasis.DerivedLevel"/>; it cannot be set on a gate directly.
/// </summary>
public enum AssuranceLevel
{
    /// <summary>F1 satisfied and F2 = Full.</summary>
    High,
    /// <summary>F1 satisfied and F2 = Partial or Indirect.</summary>
    Medium,
    /// <summary>F1 not satisfied, or F2 = None.</summary>
    Low,
}

/// <summary>
/// How fully the application modifies what the model produces at the call the gate guards. F2 is never
/// earned by the gate itself: allowing or blocking an action changes nothing about a model's output.
/// When unsure, pick the lowest value the code honestly supports.
/// </summary>
public enum F2Modification
{
    /// <summary>Directly modifies the energy function: logit adjustment, activation steering,
    /// fine-tuning. Needs a model whose internals the application controls; a hosted model API rules
    /// it out.</summary>
    Full,
    /// <summary>Shapes output through what the model sees: the application's own system prompt, RAG
    /// context, redacting the input.</summary>
    Partial,
    /// <summary>Affects sampling without touching the energy function (temperature).</summary>
    Indirect,
    /// <summary>The gate only allows or blocks and nothing is shaped at the guarded call, or no model
    /// is involved (an amount cap before a refund, an allow-list before sending an email).</summary>
    None,
}

/// <summary>Wire spellings shared with vsl-core.</summary>
public static class MetricsWire
{
    /// <summary>"HIGH", "MEDIUM" or "LOW".</summary>
    public static string ToWire(this AssuranceLevel level) => level switch
    {
        AssuranceLevel.High => "HIGH",
        AssuranceLevel.Medium => "MEDIUM",
        AssuranceLevel.Low => "LOW",
        _ => throw new ArgumentOutOfRangeException(nameof(level)),
    };

    /// <summary>"FULL", "PARTIAL", "INDIRECT" or "NONE".</summary>
    public static string ToWire(this F2Modification modification) => modification switch
    {
        F2Modification.Full => "FULL",
        F2Modification.Partial => "PARTIAL",
        F2Modification.Indirect => "INDIRECT",
        F2Modification.None => "NONE",
        _ => throw new ArgumentOutOfRangeException(nameof(modification)),
    };
}

/// <summary>
/// The F1/F2 facts an <see cref="AssuranceLevel"/> is derived from, rather than asserted:
/// F1 = false gives Low for any F2; F1 = true with Full gives High; with Partial or Indirect gives
/// Medium; with None gives Low (an interpretive extension of the source table, not a spec fact).
/// </summary>
/// <remarks>
/// Stating a basis is enforced; the basis being true is not. Nothing here checks that a stated
/// <see cref="F2Modification.Full"/> reflects what the gated monitor or rule actually does. Treat a
/// derived level as what a developer asserted, not what was proven.
/// </remarks>
public sealed record AssuranceBasis
{
    /// <summary>Creates a basis. Rejects an <see cref="F2Modification"/> value that is not a defined
    /// member (for example <c>(F2Modification)42</c>): there is no safe default.</summary>
    public AssuranceBasis(bool f1PreCommitment, F2Modification f2Modification)
    {
        if (!Enum.IsDefined(f2Modification))
        {
            throw new ArgumentOutOfRangeException(
                nameof(f2Modification),
                f2Modification,
                "AssuranceBasis.F2Modification must be Full, Partial, Indirect or None. There is no safe default: "
                + "omitting or guessing this value is exactly the mistake this check exists to catch.");
        }

        F1PreCommitment = f1PreCommitment;
        F2Modification = f2Modification;
    }

    /// <summary>F1: the check runs before the system commits to the action.</summary>
    public bool F1PreCommitment { get; }

    /// <summary>F2: how fully the application shapes the model's output at the guarded call.</summary>
    public F2Modification F2Modification { get; }

    /// <summary>The level derived from F1 and F2.</summary>
    public AssuranceLevel DerivedLevel
    {
        get
        {
            if (!F1PreCommitment)
            {
                return AssuranceLevel.Low;
            }

            return F2Modification switch
            {
                F2Modification.Full => AssuranceLevel.High,
                F2Modification.Partial or F2Modification.Indirect => AssuranceLevel.Medium,
                _ => AssuranceLevel.Low,
            };
        }
    }
}

/// <summary>Named threshold values from the specification.</summary>
public static class Thresholds
{
    /// <summary>The default Robust Gamma threshold, 1.1: a 10% estimation-error allowance.</summary>
    public const double RobustGammaDefault = 1.1;

    /// <summary>The recommended threshold for payments, medical and legal actions, 1.2.</summary>
    public const double RobustGammaHighStakes = 1.2;

    /// <summary>Gamma monitoring threshold (1.1).</summary>
    public static readonly Threshold GammaMonitoring = new("Gamma monitoring threshold", RobustGammaDefault);

    /// <summary>Jacobian red flag, lambda_min(J_gov) &lt; 0.05. A reference value only.</summary>
    public static readonly Threshold JacobianRedFlag = new("Jacobian red flag (lambda_min)", 0.05);

    /// <summary>KL monitoring threshold, 0.15 nats. A reference value only.</summary>
    public static readonly Threshold KlMonitoring = new("KL monitoring threshold", 0.15, "nats");
}

/// <summary>A named quantitative limit.</summary>
public sealed record Threshold(string Name, double Value, string Unit = "");

/// <summary>
/// A point estimate of Gamma plus its estimation-error bound, judged against the Robust Gamma
/// threshold.
/// </summary>
/// <remarks>
/// <para><see cref="Sufficient"/> passes only when <see cref="GammaHat"/> is strictly greater than
/// <see cref="RequiredGammaHat"/> = max(threshold, 1 + delta). Exactly on the bar does not pass.</para>
/// <para>This package never measures Gamma: your monitor computes <see cref="GammaHat"/> and the gate
/// only compares it. Build it as a ratio where 1.0 means break-even (a policy limit divided by the
/// requested amount), return 0.0 when input is missing so the gate fails closed, never pass a raw 0-1
/// confidence, and use an <see cref="Invariant"/> for a rule that must never be broken.</para>
/// <para>Leaving <see cref="DeltaEstimationError"/> at 0 is itself a claim ("this estimate is exact").</para>
/// </remarks>
public sealed record GammaEstimate
{
    /// <summary>Creates an estimate. <paramref name="measuredAt"/> defaults to now.</summary>
    public GammaEstimate(double gammaHat, double deltaEstimationError = 0.0, double? energyGapEstimate = null, double? measuredAt = null)
    {
        GammaHat = gammaHat;
        DeltaEstimationError = deltaEstimationError;
        EnergyGapEstimate = energyGapEstimate;
        MeasuredAt = measuredAt ?? VslClock.Now();
    }

    /// <summary>The monitor's Gamma value (a gate score in Gamma units).</summary>
    public double GammaHat { get; }

    /// <summary>The monitor author's declared estimation error.</summary>
    public double DeltaEstimationError { get; }

    /// <summary>Optional energy-gap estimate.</summary>
    public double? EnergyGapEstimate { get; }

    /// <summary>When the estimate was made, in seconds since the Unix epoch.</summary>
    public double MeasuredAt { get; }

    /// <summary>Gamma* = GammaHat * (1 / (1 + delta)), the worst case after estimation error.</summary>
    public double RobustGamma() => GammaHat * (1.0 / (1.0 + DeltaEstimationError));

    /// <summary>The value <see cref="GammaHat"/> must strictly exceed: max(threshold, 1 + delta).</summary>
    public double RequiredGammaHat(double threshold = Thresholds.RobustGammaDefault)
    {
        // Same choice as Python's max(threshold, 1 + delta): the first argument unless the second is larger.
        var allowance = 1.0 + DeltaEstimationError;
        return allowance > threshold ? allowance : threshold;
    }

    /// <summary>True only if <see cref="GammaHat"/> is strictly greater than <see cref="RequiredGammaHat"/>.
    /// Whenever this is true, <see cref="RobustGamma"/> &gt; 1 also holds.</summary>
    public bool Sufficient(double threshold = Thresholds.RobustGammaDefault) => GammaHat > RequiredGammaHat(threshold);
}

/// <summary>Governance potential: the energy contribution of the governance mechanism.</summary>
public sealed record Delta(double Value)
{
    /// <summary>Delta = Gamma * (E_high - E_drift).</summary>
    public static Delta FromGamma(double gamma, double energyGap) => new(gamma * energyGap);
}

/// <summary>Governance Effectiveness Score: 0 = no effect, 1 = divergence eliminated, below 0 = worse.</summary>
public sealed record Ges(double Score)
{
    /// <summary>GES = 1 - (governed KL integral / ungoverned KL integral).</summary>
    public static Ges Compute(double governedKlIntegral, double ungovernedKlIntegral)
    {
        if (ungovernedKlIntegral == 0)
        {
            throw new ArgumentException(
                "ungovernedKlIntegral must be nonzero: a zero-divergence ungoverned baseline makes GES undefined, not zero.",
                nameof(ungovernedKlIntegral));
        }

        return new Ges(1.0 - (governedKlIntegral / ungovernedKlIntegral));
    }

    /// <summary>True if GES &lt; theta, the specification's trigger for a Specification Update.</summary>
    public bool TriggersSpecificationUpdate(double theta) => Score < theta;
}

/// <summary>The three zones of the Enforcement Effectiveness Factor.</summary>
public enum EefZone
{
    /// <summary>Gamma at or below 1: insufficient governance.</summary>
    Insufficient,
    /// <summary>Gamma above 1 and EEF at or below 1: the target state.</summary>
    Optimal,
    /// <summary>EEF above 1: over-governance.</summary>
    OverGovernance,
}

/// <summary>Enforcement Effectiveness Factor: governance cost over the maximum sustainable cost.</summary>
public sealed record Eef(double Value)
{
    /// <summary>EEF = C(U_PN) / C_max.</summary>
    public static Eef Compute(double costOfPreNode, double maxSustainableCost)
    {
        if (maxSustainableCost == 0)
        {
            throw new ArgumentException("maxSustainableCost must be nonzero.", nameof(maxSustainableCost));
        }

        return new Eef(costOfPreNode / maxSustainableCost);
    }

    /// <summary>Classifies into the specification's three zones.</summary>
    public EefZone Zone(GammaEstimate gammaEstimate)
    {
        ArgumentNullException.ThrowIfNull(gammaEstimate);
        if (gammaEstimate.RobustGamma() <= 1.0)
        {
            return EefZone.Insufficient;
        }

        return Value <= 1.0 ? EefZone.Optimal : EefZone.OverGovernance;
    }
}

/// <summary>
/// Inverse temperature. The specification describes it only qualitatively and gives no formula
/// relating it to Delta or Gamma, so this type holds the raw value only, for naming and logging.
/// </summary>
public sealed record Beta(double Value);

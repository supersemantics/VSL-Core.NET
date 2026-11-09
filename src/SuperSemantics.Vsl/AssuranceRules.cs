namespace SuperSemantics.Vsl;

/// <summary>
/// The version of the assurance rules this package applies: how <see cref="GammaEstimate.Sufficient"/>
/// judges an estimate against a threshold, and how <see cref="AssuranceBasis.DerivedLevel"/> turns F1/F2
/// into an <see cref="AssuranceLevel"/>. Written into every gate-decision ledger record, so a record can
/// always be read under the rules that produced it. Same value as vsl-core's
/// <c>ASSURANCE_RULES_VERSION</c>; the two packages apply the same rules.
/// </summary>
public static class AssuranceRules
{
    /// <summary>The current rules version.</summary>
    public const string Version = "2026.10";
}

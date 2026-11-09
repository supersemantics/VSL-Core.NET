using System.Collections;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace SuperSemantics.Vsl;

/// <summary>Which of the five audit checks passed or failed, and the entries behind each failure.</summary>
public sealed record LedgerAuditReport(
    IReadOnlyList<string> ChecksPassed,
    IReadOnlyList<string> ChecksFailed,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Violations)
{
    /// <summary>True when no check failed.</summary>
    public bool AllPassed => ChecksFailed.Count == 0;
}

/// <summary>
/// The chain's tip, for an external party to witness over time. <see cref="VerbaLedger.VerifyIntegrity"/>
/// proves only internal consistency; it cannot detect truncation or replacement with an older,
/// consistent chain. A later checkpoint with a lower sequence, or the same sequence with a different
/// hash, proves the chain was altered.
/// </summary>
public sealed record LedgerCheckpoint(int Sequence, string EntryHash, double CheckedAt);

/// <summary>
/// The outcome of a fully passing audit. It certifies the governance process, not the underlying
/// system: see <see cref="Note"/>, which <see cref="ToString"/> always includes.
/// </summary>
public sealed record VerbaCertificate
{
    /// <summary>What this certificate does not claim.</summary>
    public const string Note =
        "This certificate does not guarantee no Inadmissible State was ever reached. It certifies that every detected "
        + "Drift event was addressed, Terminal States were properly escalated, and governance was continuously "
        + "monitored -- it certifies the governance process, not the underlying system, model, or domain.";

    /// <summary>Creates a certificate; the hash is computed as vsl-core computes it.</summary>
    public VerbaCertificate(double issuedAt, double periodStart, double periodEnd, int entriesCovered, LedgerAuditReport auditReport)
    {
        ArgumentNullException.ThrowIfNull(auditReport);
        IssuedAt = issuedAt;
        PeriodStart = periodStart;
        PeriodEnd = periodEnd;
        EntriesCovered = entriesCovered;
        AuditReport = auditReport;
        var blob = PythonJson.SerializeDefault(new Dictionary<string, object?>
        {
            ["issued_at"] = issuedAt,
            ["period_start"] = periodStart,
            ["period_end"] = periodEnd,
            ["entries_covered"] = entriesCovered,
            ["checks_passed"] = auditReport.ChecksPassed,
            ["checks_failed"] = auditReport.ChecksFailed,
        });
        CertificateHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(blob)));
    }

    /// <summary>When issued.</summary>
    public double IssuedAt { get; }
    /// <summary>First covered entry's timestamp.</summary>
    public double PeriodStart { get; }
    /// <summary>Last covered entry's timestamp.</summary>
    public double PeriodEnd { get; }
    /// <summary>Entries covered.</summary>
    public int EntriesCovered { get; }
    /// <summary>The passing audit.</summary>
    public LedgerAuditReport AuditReport { get; }
    /// <summary>SHA-256 of the certificate's fields.</summary>
    public string CertificateHash { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"VerbaCertificate(entries_covered={EntriesCovered}, all_passed={(AuditReport.AllPassed ? "True" : "False")}) -- {Note}";
}

/// <summary>
/// The VERBA Ledger: an append-only, hash-chained audit log over an <see cref="ILedgerStore"/>.
/// </summary>
/// <remarks>
/// Nothing writes to the ledger automatically. A compiled gate denies on its own, every time; recording
/// that it ran is a separate act your code performs (for example with <see cref="WriteGateDecision"/>).
/// An agent that never writes has working gates and no audit trail, and this package cannot tell.
/// </remarks>
public sealed class VerbaLedger
{
    private static readonly string[] AuditChecks =
    [
        "no_monitoring_gaps",
        "drift_flagged_monitor_has_pre_node",
        "pre_node_has_verification",
        "insufficient_verification_has_specification_update",
        "terminal_has_human_authorised_transition",
    ];

    /// <summary>Creates a ledger over <paramref name="store"/> (in memory if null).</summary>
    public VerbaLedger(ILedgerStore? store = null)
    {
        Store = store ?? new InMemoryLedgerStore();
    }

    /// <summary>The backing store.</summary>
    public ILedgerStore Store { get; }

    /// <summary>
    /// Appends an entry. Refuses (with <see cref="LedgerIntegrityException"/>) when the current last
    /// entry fails its own hash check, so a tampered chain is never silently extended.
    /// </summary>
    public LedgerEntry Write(
        LedgerEntryType entryType,
        string identityKey,
        string? clusterKey = null,
        string? instanceId = null,
        string? decisionId = null,
        string? causedBy = null,
        IReadOnlyDictionary<string, object?>? payload = null)
    {
        var last = Store.LastEntry();
        if (last is not null && LedgerEntry.ComputeHash(last) != last.EntryHash)
        {
            throw new LedgerIntegrityException(
                "Refusing to append: the ledger's last entry fails its own hash check, indicating the existing chain has "
                + "already been tampered with. Diagnose with VerifyIntegrity()/Audit() before writing further entries.");
        }

        var draft = new LedgerEntry(
            entryType,
            identityKey,
            clusterKey,
            instanceId,
            decisionId,
            causedBy,
            LedgerWire.SchemaVersion,
            payload ?? new Dictionary<string, object?>());
        return Store.Append(draft);
    }

    /// <summary>Writes a MONITOR entry with the drift flag under its standard key; the flag is merged
    /// last so <paramref name="extraPayload"/> cannot override it.</summary>
    public LedgerEntry WriteMonitor(
        string identityKey,
        bool driftDetected,
        string? clusterKey = null,
        string? instanceId = null,
        string? decisionId = null,
        string? causedBy = null,
        IReadOnlyDictionary<string, object?>? extraPayload = null)
    {
        var payload = Merge(extraPayload);
        payload[LedgerWire.DriftDetectedKey] = driftDetected;
        return Write(LedgerEntryType.Monitor, identityKey, clusterKey, instanceId, decisionId, causedBy, payload);
    }

    /// <summary>Writes a VERIFICATION entry with the result under its standard key.</summary>
    public LedgerEntry WriteVerification(
        string identityKey,
        VerificationResult result,
        string? clusterKey = null,
        string? instanceId = null,
        string? decisionId = null,
        string? causedBy = null,
        IReadOnlyDictionary<string, object?>? extraPayload = null)
    {
        var payload = Merge(extraPayload);
        payload[LedgerWire.VerificationResultKey] = result.ToWire();
        return Write(LedgerEntryType.Verification, identityKey, clusterKey, instanceId, decisionId, causedBy, payload);
    }

    /// <summary>
    /// Records one gate's allow/deny decision as a MONITOR entry in the standard gate-decision shape,
    /// the same shape vsl-core writes, so records from Python and .NET agents read the same way.
    /// </summary>
    /// <remarks>
    /// <para><paramref name="primitiveName"/> must equal the PreNode's or Invariant's declared name: that is
    /// what links the runtime record to the gate in the code.</para>
    /// <para>For a PreNode, pass the monitor's <paramref name="estimate"/> and the PreNode's
    /// <paramref name="gammaThreshold"/>: <c>allowed</c> is computed with the same
    /// <see cref="GammaEstimate.Sufficient"/> check the gate makes, and an explicit
    /// <paramref name="allowed"/> that disagrees is refused. For an Invariant, pass
    /// <paramref name="allowed"/> alone.</para>
    /// <para>Every record carries <c>rules_version</c> and <c>drift_detected = !allowed</c>; the record's
    /// own keys are merged last.</para>
    /// </remarks>
    public LedgerEntry WriteGateDecision(
        string identityKey,
        string primitiveName,
        bool? allowed = null,
        GammaEstimate? estimate = null,
        double? gammaThreshold = null,
        string? clusterKey = null,
        string? instanceId = null,
        string? decisionId = null,
        string? causedBy = null,
        IReadOnlyDictionary<string, object?>? extraPayload = null)
    {
        if (string.IsNullOrWhiteSpace(primitiveName))
        {
            throw new ArgumentException(
                "WriteGateDecision needs the gate's declared name as primitiveName; without it the record cannot be linked to its gate.",
                nameof(primitiveName));
        }

        var payload = Merge(extraPayload);
        bool decided;
        if (estimate is not null)
        {
            if (gammaThreshold is not { } threshold)
            {
                throw new ArgumentException(
                    "WriteGateDecision with an estimate needs the PreNode's gammaThreshold, so the record shows the bar the gate actually applied.",
                    nameof(gammaThreshold));
            }

            decided = estimate.Sufficient(threshold);
            if (allowed is not null && allowed.Value != decided)
            {
                throw new ArgumentException(
                    $"allowed={(allowed.Value ? "True" : "False")} contradicts estimate.Sufficient({PythonJson.FormatFloat(threshold)}) = "
                    + $"{(decided ? "True" : "False")}; refusing to record a gate decision that disagrees with the gate's own check.",
                    nameof(allowed));
            }

            payload["gamma_hat"] = estimate.GammaHat;
            payload["delta_estimation_error"] = estimate.DeltaEstimationError;
            payload["robust_gamma"] = estimate.RobustGamma();
            payload["gamma_threshold"] = threshold;
            payload["required_gamma_hat"] = estimate.RequiredGammaHat(threshold);
        }
        else if (gammaThreshold is not null)
        {
            throw new ArgumentException(
                "gammaThreshold was given without an estimate; pass the monitor's GammaEstimate too, or neither for an Invariant.",
                nameof(gammaThreshold));
        }
        else if (allowed is null)
        {
            throw new ArgumentException("WriteGateDecision needs allowed = true/false when no estimate is given.", nameof(allowed));
        }
        else
        {
            decided = allowed.Value;
        }

        payload[LedgerWire.PrimitiveNameKey] = primitiveName;
        payload[LedgerWire.GateAllowedKey] = decided;
        payload[LedgerWire.RulesVersionKey] = AssuranceRules.Version;
        payload[LedgerWire.DriftDetectedKey] = !decided;
        return Write(LedgerEntryType.Monitor, identityKey, clusterKey, instanceId, decisionId, causedBy, payload);
    }

    /// <summary>
    /// Records that <paramref name="identityKey"/> hands part of a decision to another agent before that
    /// agent acts: a MONITOR entry (not drift) whose payload carries <c>handover</c> = {from_identity,
    /// to_identity, reason}. The receiver should pass this entry's id as <c>causedBy</c> on its own first
    /// gate check, which shows it checked before acting on what it was handed.
    /// </summary>
    public LedgerEntry WriteHandover(
        string identityKey,
        string toIdentityKey,
        string decisionId,
        string reason,
        string? clusterKey = null,
        string? instanceId = null,
        string? causedBy = null,
        IReadOnlyDictionary<string, object?>? extraPayload = null)
    {
        if (string.IsNullOrEmpty(decisionId))
        {
            throw new ArgumentException("WriteHandover needs the decisionId the two agents share.", nameof(decisionId));
        }

        if (string.IsNullOrEmpty(toIdentityKey) || toIdentityKey == identityKey)
        {
            throw new ArgumentException("WriteHandover needs a receiving agent different from the sender.", nameof(toIdentityKey));
        }

        var payload = Merge(extraPayload);
        payload[LedgerWire.HandoverKey] = new Dictionary<string, object?>
        {
            ["from_identity"] = identityKey,
            ["to_identity"] = toIdentityKey,
            ["reason"] = reason,
        };
        payload[LedgerWire.DriftDetectedKey] = false;
        return Write(LedgerEntryType.Monitor, identityKey, clusterKey, instanceId, decisionId, causedBy, payload);
    }

    /// <summary>The chain's tip, or null for an empty ledger.</summary>
    public LedgerCheckpoint? CurrentCheckpoint()
    {
        var last = Store.LastEntry();
        return last is null ? null : new LedgerCheckpoint(last.Sequence, last.EntryHash, VslClock.Now());
    }

    /// <summary>Recomputes every hash and chain link. Never throws: false for any mismatch or gap.</summary>
    public bool VerifyIntegrity()
    {
        var expectedPrev = LedgerWire.GenesisHash;
        var expectedSequence = 0;
        foreach (var entry in Store.AllEntries())
        {
            if (entry.Sequence != expectedSequence) return false;
            if (entry.PrevHash != expectedPrev) return false;
            if (LedgerEntry.ComputeHash(entry) != entry.EntryHash) return false;
            expectedPrev = entry.EntryHash;
            expectedSequence++;
        }

        return true;
    }

    /// <summary>
    /// The specification's five audit checks. <paramref name="maxMonitorGapSeconds"/> has no universal
    /// default; null skips check 1. Checks 2-5 match causally through <c>caused_by</c> when a candidate
    /// sets it, and otherwise fall back to "same identity (and instance), later timestamp", which can
    /// mismatch when one instance has several decisions in flight: set <c>causedBy</c> on every write
    /// once that can happen.
    /// </summary>
    public LedgerAuditReport Audit(double? maxMonitorGapSeconds = null)
    {
        var entries = Store.AllEntries().ToList();
        var violations = new Dictionary<string, IReadOnlyList<string>>();

        var monitors = entries.Where(e => e.EntryType == LedgerEntryType.Monitor).ToList();
        if (maxMonitorGapSeconds is { } maxGap)
        {
            var sorted = monitors.OrderBy(e => e.Timestamp).ToList();
            var gaps = new List<string>();
            for (var i = 1; i < sorted.Count; i++)
            {
                if (sorted[i].Timestamp - sorted[i - 1].Timestamp > maxGap)
                {
                    gaps.Add($"{sorted[i - 1].EntryId}->{sorted[i].EntryId}");
                }
            }

            if (gaps.Count > 0) violations["no_monitoring_gaps"] = gaps;
        }

        var preNodes = entries.Where(e => e.EntryType == LedgerEntryType.PreNode).ToList();
        AddIfAny(violations, "drift_flagged_monitor_has_pre_node", monitors
            .Where(m => IsTruthy(m.Payload.GetValueOrDefault(LedgerWire.DriftDetectedKey))
                        && !preNodes.Any(pn => CausallyResolves(pn, m, checkInstance: true)))
            .Select(m => m.EntryId));

        var verifications = entries.Where(e => e.EntryType == LedgerEntryType.Verification).ToList();
        AddIfAny(violations, "pre_node_has_verification", preNodes
            .Where(pn => !verifications.Any(v => CausallyResolves(v, pn, checkInstance: true)))
            .Select(pn => pn.EntryId));

        var specUpdates = entries.Where(e => e.EntryType == LedgerEntryType.SpecificationUpdate).ToList();
        AddIfAny(violations, "insufficient_verification_has_specification_update", verifications
            .Where(v => v.Payload.GetValueOrDefault(LedgerWire.VerificationResultKey) is string r
                        && r == VerificationResult.Insufficient.ToWire()
                        && !specUpdates.Any(s => CausallyResolves(s, v, checkInstance: false)))
            .Select(v => v.EntryId));

        var terminals = entries.Where(e => e.EntryType == LedgerEntryType.Terminal).ToList();
        var transitions = entries.Where(e => e.EntryType == LedgerEntryType.HumanAuthorisedTransition).ToList();
        AddIfAny(violations, "terminal_has_human_authorised_transition", terminals
            .Where(t => !transitions.Any(h => CausallyResolves(h, t, checkInstance: false)))
            .Select(t => t.EntryId));

        return new LedgerAuditReport(
            AuditChecks.Where(c => !violations.ContainsKey(c)).ToArray(),
            AuditChecks.Where(violations.ContainsKey).ToArray(),
            violations);
    }

    /// <summary>A certificate only if the audit fully passes; never one for a failed audit.</summary>
    public VerbaCertificate? IssueCertificate(double? maxMonitorGapSeconds = null)
    {
        var entries = Store.AllEntries().ToList();
        var report = Audit(maxMonitorGapSeconds);
        if (!report.AllPassed) return null;
        var now = VslClock.Now();
        return new VerbaCertificate(
            now,
            entries.Count > 0 ? entries[0].Timestamp : now,
            entries.Count > 0 ? entries[^1].Timestamp : now,
            entries.Count,
            report);
    }

    private static bool CausallyResolves(LedgerEntry candidate, LedgerEntry source, bool checkInstance)
    {
        if (candidate.CausedBy is not null) return candidate.CausedBy == source.EntryId;
        if (candidate.Timestamp < source.Timestamp) return false;
        if (candidate.IdentityKey != source.IdentityKey) return false;
        return !checkInstance || candidate.InstanceId == source.InstanceId;
    }

    private static Dictionary<string, object?> Merge(IReadOnlyDictionary<string, object?>? extra) =>
        extra is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(extra);

    private static void AddIfAny(Dictionary<string, IReadOnlyList<string>> violations, string check, IEnumerable<string> ids)
    {
        var list = ids.ToList();
        if (list.Count > 0) violations[check] = list;
    }

    // Python truthiness, which vsl-core's audit applies to the drift flag.
    private static bool IsTruthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        string s => s.Length > 0,
        double d => d != 0,
        float f => f != 0,
        long l => l != 0,
        int i => i != 0,
        BigInteger big => !big.IsZero,
        ICollection c => c.Count > 0,
        _ => true,
    };
}

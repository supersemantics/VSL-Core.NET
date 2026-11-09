using System.Security.Cryptography;
using System.Text;

namespace SuperSemantics.Vsl;

/// <summary>
/// The Ledger's seven entry types. The source specification's prose says "six" but lists seven; the
/// list is trusted, and the discrepancy is deliberately not resolved.
/// </summary>
public enum LedgerEntryType
{
    /// <summary>MONITOR</summary>
    Monitor,
    /// <summary>PRE_NODE</summary>
    PreNode,
    /// <summary>VERIFICATION</summary>
    Verification,
    /// <summary>TERMINAL</summary>
    Terminal,
    /// <summary>SPECIFICATION_UPDATE</summary>
    SpecificationUpdate,
    /// <summary>HUMAN_AUTHORISED_TRANSITION</summary>
    HumanAuthorisedTransition,
    /// <summary>RE_ENABLEMENT</summary>
    ReEnablement,
}

/// <summary>The two outcomes a VERIFICATION entry can carry.</summary>
public enum VerificationResult
{
    /// <summary>SUFFICIENT</summary>
    Sufficient,
    /// <summary>INSUFFICIENT</summary>
    Insufficient,
}

/// <summary>Ledger wire spellings and payload keys, shared with vsl-core.</summary>
public static class LedgerWire
{
    /// <summary>The previous hash of the first entry: 64 zeros.</summary>
    public const string GenesisHash = "0000000000000000000000000000000000000000000000000000000000000000";

    /// <summary>The shape version stamped on every entry <see cref="VerbaLedger.Write"/> creates.</summary>
    public const string SchemaVersion = "1.0";

    /// <summary>A MONITOR payload's drift flag.</summary>
    public const string DriftDetectedKey = "drift_detected";

    /// <summary>A VERIFICATION payload's result.</summary>
    public const string VerificationResultKey = "result";

    /// <summary>A gate-decision record's gate name.</summary>
    public const string PrimitiveNameKey = "primitive_name";

    /// <summary>A gate-decision record's outcome.</summary>
    public const string GateAllowedKey = "allowed";

    /// <summary>A gate-decision record's rules version.</summary>
    public const string RulesVersionKey = "rules_version";

    /// <summary>A handover record's payload key: {from_identity, to_identity, reason}.</summary>
    public const string HandoverKey = "handover";

    /// <summary>"MONITOR", "PRE_NODE", ...</summary>
    public static string ToWire(this LedgerEntryType type) => type switch
    {
        LedgerEntryType.Monitor => "MONITOR",
        LedgerEntryType.PreNode => "PRE_NODE",
        LedgerEntryType.Verification => "VERIFICATION",
        LedgerEntryType.Terminal => "TERMINAL",
        LedgerEntryType.SpecificationUpdate => "SPECIFICATION_UPDATE",
        LedgerEntryType.HumanAuthorisedTransition => "HUMAN_AUTHORISED_TRANSITION",
        LedgerEntryType.ReEnablement => "RE_ENABLEMENT",
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Parses a wire spelling.</summary>
    public static LedgerEntryType ParseEntryType(string wire) => wire switch
    {
        "MONITOR" => LedgerEntryType.Monitor,
        "PRE_NODE" => LedgerEntryType.PreNode,
        "VERIFICATION" => LedgerEntryType.Verification,
        "TERMINAL" => LedgerEntryType.Terminal,
        "SPECIFICATION_UPDATE" => LedgerEntryType.SpecificationUpdate,
        "HUMAN_AUTHORISED_TRANSITION" => LedgerEntryType.HumanAuthorisedTransition,
        "RE_ENABLEMENT" => LedgerEntryType.ReEnablement,
        _ => throw new ArgumentException($"Unknown ledger entry type '{wire}'.", nameof(wire)),
    };

    /// <summary>"SUFFICIENT" or "INSUFFICIENT".</summary>
    public static string ToWire(this VerificationResult result) => result switch
    {
        VerificationResult.Sufficient => "SUFFICIENT",
        VerificationResult.Insufficient => "INSUFFICIENT",
        _ => throw new ArgumentOutOfRangeException(nameof(result)),
    };
}

/// <summary>
/// One entry in the hash chain. <see cref="Sequence"/>, <see cref="PrevHash"/> and
/// <see cref="EntryHash"/> are set by the store when it appends, so a caller cannot choose an entry's
/// position. <see cref="CausedBy"/> names the earlier entry this one resolves, so audits match causally
/// instead of by timing; <see cref="DecisionId"/> groups a decision's entries.
/// </summary>
public sealed record LedgerEntry
{
    /// <summary>Creates an unsealed entry (sequence -1, empty hashes) unless sealed values are given.</summary>
    public LedgerEntry(
        LedgerEntryType entryType,
        string identityKey,
        string? clusterKey = null,
        string? instanceId = null,
        string? decisionId = null,
        string? causedBy = null,
        string? schemaVersion = null,
        IReadOnlyDictionary<string, object?>? payload = null,
        double? timestamp = null,
        string? entryId = null,
        int sequence = -1,
        string prevHash = "",
        string entryHash = "")
    {
        ArgumentNullException.ThrowIfNull(identityKey);
        EntryType = entryType;
        IdentityKey = identityKey;
        ClusterKey = clusterKey;
        InstanceId = instanceId;
        DecisionId = decisionId;
        CausedBy = causedBy;
        SchemaVersion = schemaVersion;
        Payload = payload is null ? new Dictionary<string, object?>() : new Dictionary<string, object?>(payload);
        Timestamp = timestamp ?? VslClock.Now();
        EntryId = string.IsNullOrEmpty(entryId) ? Guid.NewGuid().ToString() : entryId;
        Sequence = sequence;
        PrevHash = prevHash ?? "";
        EntryHash = entryHash ?? "";
    }

    /// <summary>Entry type.</summary>
    public LedgerEntryType EntryType { get; init; }
    /// <summary>The identity the entry belongs to.</summary>
    public string IdentityKey { get; init; }
    /// <summary>The Cluster key, if any.</summary>
    public string? ClusterKey { get; init; }
    /// <summary>The instance, if any.</summary>
    public string? InstanceId { get; init; }
    /// <summary>Groups a decision's entries.</summary>
    public string? DecisionId { get; init; }
    /// <summary>The entry id this entry resolves.</summary>
    public string? CausedBy { get; init; }
    /// <summary>Which entry shape wrote it; null for entries that predate the field.</summary>
    public string? SchemaVersion { get; init; }
    /// <summary>The payload.</summary>
    public IReadOnlyDictionary<string, object?> Payload { get; init; }
    /// <summary>Seconds since the Unix epoch.</summary>
    public double Timestamp { get; init; }
    /// <summary>Unique id.</summary>
    public string EntryId { get; init; }
    /// <summary>Position in the chain, from 0; -1 until sealed.</summary>
    public int Sequence { get; init; }
    /// <summary>The previous entry's hash, or <see cref="LedgerWire.GenesisHash"/>.</summary>
    public string PrevHash { get; init; }
    /// <summary>This entry's hash.</summary>
    public string EntryHash { get; init; }

    /// <summary>The serialized form, with vsl-core's field names.</summary>
    public Dictionary<string, object?> ToDictionary() => new(StringComparer.Ordinal)
    {
        ["entry_id"] = EntryId,
        ["sequence"] = Sequence,
        ["entry_type"] = EntryType.ToWire(),
        ["identity_key"] = IdentityKey,
        ["cluster_key"] = ClusterKey,
        ["instance_id"] = InstanceId,
        ["decision_id"] = DecisionId,
        ["caused_by"] = CausedBy,
        ["schema_version"] = SchemaVersion,
        ["payload"] = Payload,
        ["timestamp"] = Timestamp,
        ["prev_hash"] = PrevHash,
        ["entry_hash"] = EntryHash,
    };

    /// <summary>
    /// Rebuilds an entry from its serialized form (as written by this package or by vsl-core).
    /// Deliberately does not verify the hash, so a corrupted chain can still be loaded and diagnosed.
    /// </summary>
    public static LedgerEntry FromDictionary(IReadOnlyDictionary<string, object?> data)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new LedgerEntry(
            LedgerWire.ParseEntryType(Required<string>(data, "entry_type")),
            Required<string>(data, "identity_key"),
            clusterKey: Optional(data, "cluster_key"),
            instanceId: Optional(data, "instance_id"),
            decisionId: Optional(data, "decision_id"),
            causedBy: Optional(data, "caused_by"),
            schemaVersion: Optional(data, "schema_version"),
            payload: data.TryGetValue("payload", out var p) && p is IReadOnlyDictionary<string, object?> payload ? payload
                : data.TryGetValue("payload", out var p2) && p2 is Dictionary<string, object?> payload2 ? payload2
                : new Dictionary<string, object?>(),
            timestamp: Convert.ToDouble(data["timestamp"], System.Globalization.CultureInfo.InvariantCulture),
            entryId: Required<string>(data, "entry_id"),
            sequence: Convert.ToInt32(data["sequence"], System.Globalization.CultureInfo.InvariantCulture),
            prevHash: Required<string>(data, "prev_hash"),
            entryHash: Required<string>(data, "entry_hash"));
    }

    /// <summary>Parses one JSON object (a JSONL line).</summary>
    public static LedgerEntry FromJson(string json) =>
        FromDictionary(PythonJson.Parse(json) as Dictionary<string, object?>
            ?? throw new FormatException("A ledger entry must be a JSON object."));

    private static T Required<T>(IReadOnlyDictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var value) && value is T typed ? typed : throw new FormatException($"Ledger entry is missing '{key}'.");

    private static string? Optional(IReadOnlyDictionary<string, object?> data, string key) =>
        data.TryGetValue(key, out var value) ? value as string : null;

    /// <summary>
    /// SHA-256 over every field except the hash itself, as canonical JSON (sorted keys, compact
    /// separators). The same value vsl-core's <c>_compute_entry_hash</c> gives.
    /// </summary>
    public static string ComputeHash(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var canonical = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["entry_id"] = entry.EntryId,
            ["sequence"] = entry.Sequence,
            ["entry_type"] = entry.EntryType.ToWire(),
            ["identity_key"] = entry.IdentityKey,
            ["cluster_key"] = entry.ClusterKey,
            ["instance_id"] = entry.InstanceId,
            ["decision_id"] = entry.DecisionId,
            ["caused_by"] = entry.CausedBy,
            ["schema_version"] = entry.SchemaVersion,
            ["payload"] = entry.Payload,
            ["timestamp"] = entry.Timestamp,
            ["prev_hash"] = entry.PrevHash,
        };
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(PythonJson.SerializeCompact(canonical))));
    }

    /// <summary>Assigns the chain position and computes the hash.</summary>
    internal static LedgerEntry Seal(LedgerEntry entry, int sequence, string prevHash)
    {
        var positioned = entry with { Sequence = sequence, PrevHash = prevHash, EntryHash = "" };
        return positioned with { EntryHash = ComputeHash(positioned) };
    }
}

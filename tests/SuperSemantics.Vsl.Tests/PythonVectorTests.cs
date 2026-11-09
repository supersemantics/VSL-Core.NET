using System.Globalization;

namespace SuperSemantics.Vsl.Tests;

/// <summary>
/// Checks this package against output from the real Python vsl-core (tools/generate_vectors.py):
/// float formatting, canonical JSON, ledger hashes and the JSONL file itself, byte for byte.
/// </summary>
public class PythonVectorTests
{
    private static readonly Dictionary<string, object?> Vectors = (Dictionary<string, object?>)PythonJson.Parse(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Vectors", "python-vectors.json")))!;

    private static List<object?> List(string key) => (List<object?>)Vectors[key]!;

    private static Dictionary<string, object?> Obj(object? value) => (Dictionary<string, object?>)value!;

    private static double FromBits(string hex) =>
        BitConverter.Int64BitsToDouble(unchecked((long)ulong.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture)));

    [Fact]
    public void Floats_format_exactly_like_python_repr()
    {
        var cases = List("floats");
        Assert.True(cases.Count > 2500);
        var mismatches = cases
            .Select(Obj)
            .Select(c => (Value: FromBits((string)c["bits"]!), Expected: (string)c["json"]!))
            .Where(c => PythonJson.FormatFloat(c.Value) != c.Expected)
            .Select(c => $"{c.Value:R}: expected {c.Expected}, got {PythonJson.FormatFloat(c.Value)}")
            .Take(10)
            .ToList();
        Assert.Empty(mismatches);
    }

    [Fact]
    public void Canonical_json_matches_python_json_dumps()
    {
        foreach (var c in List("json").Select(Obj))
        {
            Assert.Equal((string)c["compact"]!, PythonJson.SerializeCompact(c["value"]));
            Assert.Equal((string)c["default"]!, PythonJson.SerializeDefault(c["value"]));
        }
    }

    private static List<LedgerEntry> PythonEntries() =>
        ((List<object?>)Obj(Vectors["ledger"])["entries"]!).Select(e => LedgerEntry.FromDictionary(Obj(e))).ToList();

    [Fact]
    public void Python_entry_hashes_recompute_identically()
    {
        foreach (var entry in PythonEntries())
        {
            Assert.Equal(entry.EntryHash, LedgerEntry.ComputeHash(entry));
        }
    }

    [Fact]
    public void Sealing_the_same_drafts_gives_the_same_chain()
    {
        var store = new InMemoryLedgerStore();
        foreach (var python in PythonEntries())
        {
            var draft = python with { Sequence = -1, PrevHash = "", EntryHash = "" };
            var sealedEntry = store.Append(draft);
            Assert.Equal(python.Sequence, sealedEntry.Sequence);
            Assert.Equal(python.PrevHash, sealedEntry.PrevHash);
            Assert.Equal(python.EntryHash, sealedEntry.EntryHash);
        }

        Assert.True(new VerbaLedger(store).VerifyIntegrity());
    }

    [Fact]
    public void A_python_written_jsonl_ledger_verifies_here()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vsl-python-{Guid.NewGuid():N}.jsonl");
        try
        {
            File.WriteAllText(path, (string)Obj(Vectors["ledger"])["jsonl"]!);
            var ledger = new VerbaLedger(new JsonlLedgerStore(path));
            Assert.True(ledger.VerifyIntegrity());
            Assert.Equal(4, ledger.Store.LastEntry()!.Sequence);

            // And appending continues the same chain.
            var next = ledger.WriteMonitor("refund-agent", driftDetected: false);
            Assert.Equal(5, next.Sequence);
            Assert.True(ledger.VerifyIntegrity());
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public void The_jsonl_file_this_package_writes_is_byte_identical_to_pythons()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vsl-dotnet-{Guid.NewGuid():N}.jsonl");
        try
        {
            var store = new JsonlLedgerStore(path);
            foreach (var python in PythonEntries())
            {
                store.Append(python with { Sequence = -1, PrevHash = "", EntryHash = "" });
            }

            Assert.Equal((string)Obj(Vectors["ledger"])["jsonl"]!, File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public void Cluster_key_matches()
    {
        var c = Obj(Vectors["cluster"]);
        var keys = ((List<object?>)c["constituents"]!).Select(v => new IdentityKey((string)v!));
        Assert.Equal((string)c["value"]!, ClusterKey.FromConstituents(keys).Value);
    }

    [Fact]
    public void Transition_evidence_hash_matches()
    {
        var t = Obj(Vectors["transition"]);
        var evidence = ((List<object?>)t["evidence"]!).Cast<string>().ToArray();
        var transition = HumanAuthorisedTransition.Authorise(
            new GovernanceAuthority("safety-board", "oncall@example.com"),
            "jane.doe",
            new Evidence(evidence[0], evidence[1]));
        Assert.Equal((string)t["hash"]!, transition.AuthorisationEvidenceHash);
    }

    [Fact]
    public void Certificate_hash_matches()
    {
        var c = Obj(Vectors["certificate"]);
        var report = new LedgerAuditReport(
            ((List<object?>)c["checks_passed"]!).Cast<string>().ToArray(),
            ((List<object?>)c["checks_failed"]!).Cast<string>().ToArray(),
            new Dictionary<string, IReadOnlyList<string>>());
        var certificate = new VerbaCertificate((double)c["issued_at"]!, (double)c["period_start"]!, (double)c["period_end"]!, (int)(long)c["entries_covered"]!, report);
        Assert.Equal((string)c["hash"]!, certificate.CertificateHash);
    }

    [Fact]
    public void Gamma_rules_give_the_same_bits_and_decisions()
    {
        foreach (var g in List("gamma").Select(Obj))
        {
            var estimate = new GammaEstimate((double)g["gamma_hat"]!, (double)g["delta"]!, measuredAt: 0.0);
            var threshold = (double)g["threshold"]!;
            Assert.Equal(FromBits((string)g["robust_gamma_bits"]!), estimate.RobustGamma());
            Assert.Equal(FromBits((string)g["required_bits"]!), estimate.RequiredGammaHat(threshold));
            Assert.Equal((bool)g["sufficient"]!, estimate.Sufficient(threshold));
        }
    }
}

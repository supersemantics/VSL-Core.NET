namespace SuperSemantics.Vsl.Tests;

public sealed class LedgerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vsl-ledger-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string NewPath() => Path.Combine(_dir, $"{Guid.NewGuid():N}.jsonl");

    [Fact]
    public void A_chain_verifies_and_a_single_changed_byte_on_disk_breaks_it()
    {
        var path = NewPath();
        var ledger = new VerbaLedger(new JsonlLedgerStore(path));
        ledger.WriteMonitor("agent", driftDetected: false);
        ledger.WriteGateDecision("agent", "refund-within-policy", estimate: new GammaEstimate(2.0), gammaThreshold: 1.1);
        ledger.WriteMonitor("agent", driftDetected: false);
        Assert.True(ledger.VerifyIntegrity());

        var text = File.ReadAllText(path);
        File.WriteAllText(path, text.Replace("\"gamma_hat\": 2.0", "\"gamma_hat\": 9.0", StringComparison.Ordinal));
        Assert.False(new VerbaLedger(new JsonlLedgerStore(path)).VerifyIntegrity());
    }

    [Fact]
    public void Writing_onto_a_tampered_tip_is_refused()
    {
        var path = NewPath();
        var ledger = new VerbaLedger(new JsonlLedgerStore(path));
        ledger.WriteMonitor("agent", driftDetected: false);
        File.WriteAllText(path, File.ReadAllText(path).Replace("\"drift_detected\": false", "\"drift_detected\": true", StringComparison.Ordinal));
        Assert.Throws<LedgerIntegrityException>(() => ledger.WriteMonitor("agent", driftDetected: false));
    }

    [Fact]
    public void Gate_decision_records_have_the_standard_shape()
    {
        var ledger = new VerbaLedger();
        var entry = ledger.WriteGateDecision("refund-agent", "refund-within-policy", estimate: new GammaEstimate(1.05), gammaThreshold: 1.1, decisionId: "d-1");
        Assert.Equal(LedgerEntryType.Monitor, entry.EntryType);
        Assert.Equal(false, entry.Payload["allowed"]);
        Assert.Equal(true, entry.Payload["drift_detected"]);
        Assert.Equal("2026.10", entry.Payload["rules_version"]);
        Assert.Equal(1.1, entry.Payload["required_gamma_hat"]);
        Assert.Equal("refund-within-policy", entry.Payload["primitive_name"]);
        Assert.Equal("1.0", entry.SchemaVersion);
    }

    [Fact]
    public void Gate_decision_refuses_contradictions_and_missing_facts()
    {
        var ledger = new VerbaLedger();
        Assert.Throws<ArgumentException>(() => ledger.WriteGateDecision("a", "", allowed: true));
        Assert.Throws<ArgumentException>(() => ledger.WriteGateDecision("a", "g", estimate: new GammaEstimate(2)));
        Assert.Throws<ArgumentException>(() => ledger.WriteGateDecision("a", "g", gammaThreshold: 1.1, allowed: true));
        Assert.Throws<ArgumentException>(() => ledger.WriteGateDecision("a", "g"));
        Assert.Throws<ArgumentException>(() => ledger.WriteGateDecision("a", "g", allowed: true, estimate: new GammaEstimate(0.5), gammaThreshold: 1.1));
        var extra = new Dictionary<string, object?> { ["allowed"] = true, ["note"] = "kept" };
        var entry = ledger.WriteGateDecision("a", "g", allowed: false, extraPayload: extra);
        Assert.Equal(false, entry.Payload["allowed"]);
        Assert.Equal("kept", entry.Payload["note"]);
    }

    [Fact]
    public void Handover_needs_a_decision_and_a_different_receiver()
    {
        var ledger = new VerbaLedger();
        Assert.Throws<ArgumentException>(() => ledger.WriteHandover("intake", "refund", "", "why"));
        Assert.Throws<ArgumentException>(() => ledger.WriteHandover("intake", "intake", "d", "why"));
        var handover = ledger.WriteHandover("intake", "refund", "d-1", "refund request");
        var receiverCheck = ledger.WriteGateDecision("refund", "refund-within-policy", allowed: true, decisionId: "d-1", causedBy: handover.EntryId);
        Assert.Equal(handover.EntryId, receiverCheck.CausedBy);
        Assert.Equal(false, handover.Payload["drift_detected"]);
    }

    [Fact]
    public void Audit_flags_unresolved_drift_and_unescalated_terminals()
    {
        var ledger = new VerbaLedger();
        var drift = ledger.WriteMonitor("agent", driftDetected: true, instanceId: "i-1");
        ledger.Write(LedgerEntryType.Terminal, "agent");
        var report = ledger.Audit();
        Assert.Contains("drift_flagged_monitor_has_pre_node", report.ChecksFailed);
        Assert.Contains("terminal_has_human_authorised_transition", report.ChecksFailed);
        Assert.Equal([drift.EntryId], report.Violations["drift_flagged_monitor_has_pre_node"]);
        Assert.Null(ledger.IssueCertificate());
    }

    [Fact]
    public void Causal_links_override_the_timing_heuristic()
    {
        var ledger = new VerbaLedger();
        var payment = ledger.WriteMonitor("agent", driftDetected: true, instanceId: "i-1");
        var email = ledger.WriteMonitor("agent", driftDetected: true, instanceId: "i-1");
        var preNode = ledger.Write(LedgerEntryType.PreNode, "agent", instanceId: "i-1", causedBy: email.EntryId);
        ledger.WriteVerification("agent", VerificationResult.Sufficient, instanceId: "i-1", causedBy: preNode.EntryId);
        var report = ledger.Audit();
        Assert.Equal([payment.EntryId], report.Violations["drift_flagged_monitor_has_pre_node"]);
    }

    [Fact]
    public void A_clean_ledger_gets_a_certificate_that_states_its_limits()
    {
        var ledger = new VerbaLedger();
        var drift = ledger.WriteMonitor("agent", driftDetected: true);
        var preNode = ledger.Write(LedgerEntryType.PreNode, "agent", causedBy: drift.EntryId);
        var verification = ledger.WriteVerification("agent", VerificationResult.Insufficient, causedBy: preNode.EntryId);
        ledger.Write(LedgerEntryType.SpecificationUpdate, "agent", causedBy: verification.EntryId);
        var certificate = ledger.IssueCertificate();
        Assert.NotNull(certificate);
        Assert.Equal(4, certificate.EntriesCovered);
        Assert.Contains("certifies the governance process", certificate.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Monitoring_gaps_are_checked_only_when_a_limit_is_given()
    {
        var store = new InMemoryLedgerStore();
        store.Append(new LedgerEntry(LedgerEntryType.Monitor, "a", payload: new Dictionary<string, object?> { ["drift_detected"] = false }, timestamp: 100));
        store.Append(new LedgerEntry(LedgerEntryType.Monitor, "a", payload: new Dictionary<string, object?> { ["drift_detected"] = false }, timestamp: 200));
        var ledger = new VerbaLedger(store);
        Assert.True(ledger.Audit().AllPassed);
        Assert.Contains("no_monitoring_gaps", ledger.Audit(maxMonitorGapSeconds: 50).ChecksFailed);
    }

    [Fact]
    public async Task Separate_stores_on_one_file_never_corrupt_the_chain()
    {
        var path = NewPath();
        const int writers = 8;
        const int perWriter = 40;
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(() =>
        {
            var ledger = new VerbaLedger(new JsonlLedgerStore(path));
            for (var i = 0; i < perWriter; i++)
            {
                ledger.WriteMonitor($"writer-{w}", driftDetected: false);
            }
        })));

        var check = new VerbaLedger(new JsonlLedgerStore(path));
        Assert.True(check.VerifyIntegrity());
        Assert.Equal(writers * perWriter - 1, check.Store.LastEntry()!.Sequence);
    }

    [Fact]
    public void Checkpoint_is_the_chain_tip()
    {
        var ledger = new VerbaLedger();
        Assert.Null(ledger.CurrentCheckpoint());
        var last = ledger.WriteMonitor("a", driftDetected: false);
        var checkpoint = ledger.CurrentCheckpoint()!;
        Assert.Equal(last.Sequence, checkpoint.Sequence);
        Assert.Equal(last.EntryHash, checkpoint.EntryHash);
    }

    [Fact]
    public void Decimal_payload_values_are_refused_rather_than_rounded()
    {
        var ledger = new VerbaLedger();
        Assert.Throws<NotSupportedException>(() =>
            ledger.WriteMonitor("a", driftDetected: false, extraPayload: new Dictionary<string, object?> { ["amount"] = 12.30m }));
    }
}

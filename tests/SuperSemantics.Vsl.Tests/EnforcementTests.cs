namespace SuperSemantics.Vsl.Tests;

/// <summary>Port of vsl-core's tests/test_enforcement.py.</summary>
public class EnforcementTests
{
    private static LedgerEntry Gate(VerbaLedger ledger, string decisionId, bool allowed, string name = "refund-cap") =>
        ledger.WriteGateDecision("refund-agent", name, allowed: allowed, decisionId: decisionId);

    private static LedgerEntry Action(VerbaLedger ledger, string decisionId, string name = "issue_refund") =>
        ledger.WriteAction("refund-agent", name, decisionId);

    [Fact]
    public void Allow_then_action_is_before_action()
    {
        var ledger = new VerbaLedger();
        Gate(ledger, "d1", true);
        Action(ledger, "d1");
        Assert.Equal(Enforcement.BeforeAction, ledger.Enforcement("d1"));
    }

    [Fact]
    public void An_action_with_no_gate_record_is_after_action()
    {
        var ledger = new VerbaLedger();
        Action(ledger, "d1");
        Assert.Equal(Enforcement.AfterAction, ledger.Enforcement("d1"));
    }

    [Fact]
    public void An_action_recorded_before_the_gate_is_after_action()
    {
        var ledger = new VerbaLedger();
        Action(ledger, "d1");
        Gate(ledger, "d1", true);
        Assert.Equal(Enforcement.AfterAction, ledger.Enforcement("d1"));
    }

    [Fact]
    public void An_action_after_a_deny_is_after_action()
    {
        var ledger = new VerbaLedger();
        Gate(ledger, "d1", false);
        Action(ledger, "d1");
        Assert.Equal(Enforcement.AfterAction, ledger.Enforcement("d1"));
    }

    [Fact]
    public void A_deny_with_no_action_held()
    {
        var ledger = new VerbaLedger();
        Gate(ledger, "d1", false);
        Assert.Equal(Enforcement.BeforeAction, ledger.Enforcement("d1"));
    }

    [Fact]
    public void An_allow_with_no_action_record_is_not_observed()
    {
        var ledger = new VerbaLedger();
        Gate(ledger, "d1", true);
        Assert.Equal(Enforcement.NotObserved, ledger.Enforcement("d1"));
    }

    [Fact]
    public void An_unknown_decision_is_not_observed() =>
        Assert.Equal(Enforcement.NotObserved, new VerbaLedger().Enforcement("missing"));

    [Fact]
    public void Deny_then_an_allowing_retry_then_the_action_is_before_action()
    {
        var ledger = new VerbaLedger();
        Gate(ledger, "d1", false);
        Gate(ledger, "d1", true);
        Action(ledger, "d1");
        Assert.Equal(Enforcement.BeforeAction, ledger.Enforcement("d1"));
    }

    [Fact]
    public void Every_gate_must_allow_before_the_action()
    {
        var ledger = new VerbaLedger();
        Gate(ledger, "d1", true, "refund-cap");
        Gate(ledger, "d1", false, "fraud-check");
        Action(ledger, "d1");
        Assert.Equal(Enforcement.AfterAction, ledger.Enforcement("d1"));
        Assert.Equal(Enforcement.BeforeAction, ledger.Enforcement("d1", primitiveName: "refund-cap"));
        Assert.Equal(Enforcement.AfterAction, ledger.Enforcement("d1", primitiveName: "fraud-check"));
    }

    [Fact]
    public void Each_action_is_judged_by_the_latest_record_of_each_gate_before_it()
    {
        var ledger = new VerbaLedger();
        Gate(ledger, "d1", true);
        Action(ledger, "d1");
        Gate(ledger, "d1", false);
        Action(ledger, "d1");
        Assert.Equal(Enforcement.AfterAction, ledger.Enforcement("d1"));
    }

    [Fact]
    public void Other_decisions_do_not_interfere()
    {
        var ledger = new VerbaLedger();
        Gate(ledger, "d1", true);
        Action(ledger, "d2");
        Action(ledger, "d1");
        Assert.Equal(Enforcement.BeforeAction, ledger.Enforcement("d1"));
        Assert.Equal(Enforcement.AfterAction, ledger.Enforcement("d2"));
    }

    [Fact]
    public void Enforcement_by_decision_covers_every_decision_with_a_gate_or_action_record()
    {
        var ledger = new VerbaLedger();
        Gate(ledger, "ok", true);
        Action(ledger, "ok");
        Action(ledger, "skipped");
        Gate(ledger, "pending", true);
        ledger.WriteMonitor("refund-agent", driftDetected: false, decisionId: "plain-monitor");
        Assert.Equal(
            new Dictionary<string, Enforcement>
            {
                ["ok"] = Enforcement.BeforeAction,
                ["skipped"] = Enforcement.AfterAction,
                ["pending"] = Enforcement.NotObserved,
            },
            ledger.EnforcementByDecision());
        Assert.Equal(["ok", "skipped", "pending"], ledger.EnforcementByDecision().Keys);
    }

    [Fact]
    public void The_action_record_has_the_python_shape()
    {
        var entry = Action(new VerbaLedger(), "d1");
        Assert.Equal(LedgerEntryType.Monitor, entry.EntryType);
        var action = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(entry.Payload[LedgerWire.ActionKey]);
        Assert.Equal("issue_refund", action["name"]);
        Assert.Equal(false, entry.Payload[LedgerWire.DriftDetectedKey]);
        Assert.Equal("d1", entry.DecisionId);
    }

    [Fact]
    public void Extra_payload_cannot_overwrite_the_record_keys()
    {
        var entry = new VerbaLedger().WriteAction("a", "issue_refund", "d1", extraPayload: new Dictionary<string, object?>
        {
            [LedgerWire.ActionKey] = "forged",
            [LedgerWire.DriftDetectedKey] = true,
            ["amount"] = 40,
        });
        var action = Assert.IsAssignableFrom<IReadOnlyDictionary<string, object?>>(entry.Payload[LedgerWire.ActionKey]);
        Assert.Equal("issue_refund", action["name"]);
        Assert.Equal(false, entry.Payload[LedgerWire.DriftDetectedKey]);
        Assert.Equal(40, entry.Payload["amount"]);
    }

    [Theory]
    [InlineData("issue_refund", "")]
    [InlineData(" ", "d1")]
    public void Write_action_rejects_missing_fields(string actionName, string decisionId) =>
        Assert.Throws<ArgumentException>(() => new VerbaLedger().WriteAction("a", actionName, decisionId));

    [Fact]
    public void Action_records_do_not_break_the_audit()
    {
        var ledger = new VerbaLedger();
        Gate(ledger, "d1", true);
        Action(ledger, "d1");
        var report = ledger.Audit();
        Assert.True(report.AllPassed);
        Assert.Equal(5, report.ChecksPassed.Count);
        Assert.True(ledger.VerifyIntegrity());
    }

    private static void AppendAt(VerbaLedger ledger, double timestamp, Dictionary<string, object?> payload) =>
        ledger.Store.Append(new LedgerEntry(
            LedgerEntryType.Monitor, "refund-agent", null, null, "d1", null, "1.0", payload, timestamp: timestamp));

    public static TheoryData<string> Fillers => new() { "action", "handover" };

    [Theory]
    [MemberData(nameof(Fillers))]
    public void Action_and_handover_records_do_not_hide_a_monitoring_gap(string filler)
    {
        var ledger = new VerbaLedger();
        AppendAt(ledger, 0, new() { [LedgerWire.DriftDetectedKey] = false });
        foreach (var t in new[] { 5.0, 10.0, 15.0 })
        {
            var record = filler == "action"
                ? new Dictionary<string, object?> { ["name"] = "issue_refund" }
                : new Dictionary<string, object?> { ["from_identity"] = "a", ["to_identity"] = "b", ["reason"] = "r" };
            AppendAt(ledger, t, new() { [filler] = record, [LedgerWire.DriftDetectedKey] = false });
        }

        AppendAt(ledger, 20, new() { [LedgerWire.DriftDetectedKey] = false });
        Assert.Contains("no_monitoring_gaps", ledger.Audit(maxMonitorGapSeconds: 8).ChecksFailed);
    }

    [Fact]
    public void Gate_decisions_still_count_as_monitoring()
    {
        var ledger = new VerbaLedger();
        foreach (var t in new[] { 0.0, 5.0, 10.0 })
        {
            AppendAt(ledger, t, new() { ["primitive_name"] = "refund-cap", ["allowed"] = true, [LedgerWire.DriftDetectedKey] = false });
        }

        Assert.Contains("no_monitoring_gaps", ledger.Audit(maxMonitorGapSeconds: 8).ChecksPassed);
    }

    [Fact]
    public void Enforcement_survives_a_jsonl_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"vsl-enforcement-{Guid.NewGuid():N}.jsonl");
        try
        {
            var ledger = new VerbaLedger(new JsonlLedgerStore(path));
            Gate(ledger, "d1", true);
            Action(ledger, "d1");
            var reloaded = new VerbaLedger(new JsonlLedgerStore(path));
            Assert.Equal(Enforcement.BeforeAction, reloaded.Enforcement("d1"));
        }
        finally
        {
            File.Delete(path);
            File.Delete(path + ".lock");
        }
    }

    [Fact]
    public void Wire_spellings_match_vsl_core()
    {
        Assert.Equal("BEFORE_ACTION", Enforcement.BeforeAction.ToWire());
        Assert.Equal("AFTER_ACTION", Enforcement.AfterAction.ToWire());
        Assert.Equal("NOT_OBSERVED", Enforcement.NotObserved.ToWire());
    }
}

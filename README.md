# VSL-Core.NET

The .NET port of [vsl-core](https://github.com/supersemantics/VSL-Core): framework-agnostic and model-agnostic **VSL** (VERBA Specification Language) governance primitives, for deciding whether an automated system's next action is allowed to happen and proving that the decision was made.

- NuGet package `SuperSemantics.Vsl` (`dotnet add package SuperSemantics.Vsl`), namespace `SuperSemantics.Vsl`.
- Targets .NET 10 (LTS). No dependencies beyond the .NET base library.
- Ports vsl-core 0.4.0, assurance rules `2026.10`.
- **Ledger files and hashes match vsl-core byte for byte.** A ledger written by a C# agent verifies in Python, and the other way round, and both can write to the same file at the same time.
- MIT licensed.

## What this is, and isn't

**Is:** the agnostic template (`PreNode`, `Invariant`, `TerminalState`, `Cluster`, the hash-chained `VerbaLedger`), the Drift Class / Stabilisation Operator catalog, and the conformance contract a framework adapter must pass.

**Is not:** a framework adapter. Nothing here references Microsoft Agent Framework, Semantic Kernel, AutoGen, or any model-provider SDK. Adapters are separate packages that pass `ConformanceSuite`.

## Quick start

```csharp
using SuperSemantics.Vsl;
using SuperSemantics.Vsl.Conformance;

const string RefundGate = "refund-within-policy";
const double RefundLimit = 500.0;

var ledger = new VerbaLedger(new JsonlLedgerStore("ledger.jsonl", fsync: true));

var refundPreNode = new PreNode(
    name: RefundGate,
    description: "Refunds must stay comfortably inside the policy limit.",
    monitor: request =>
    {
        var r = (RefundRequest)request!;
        // A ratio where 1.0 is break-even; 0.0 for unreadable input, so the gate fails closed.
        var estimate = new GammaEstimate(r.Amount > 0 ? RefundLimit / r.Amount : 0.0);
        ledger.WriteGateDecision(
            identityKey: "refund-agent",
            primitiveName: RefundGate,                         // must equal the PreNode's name
            estimate: estimate,
            gammaThreshold: Thresholds.RobustGammaDefault,     // must equal the PreNode's threshold
            decisionId: r.DecisionId);
        return Task.FromResult(estimate);
    },
    gammaThreshold: Thresholds.RobustGammaDefault);

CompiledGate gate = new PlainReferenceAdapter().CompilePreNode(refundPreNode);

try
{
    await gate(new RefundRequest("d-42", Amount: 120.0));   // runs before the refund
    IssueRefund();                                         // only reached if the gate passed
    ledger.WriteAction("refund-agent", "IssueRefund", decisionId: "d-42");
}
catch (AutomationDeniedException denied)
{
    // Denied before any side effect. Route to a human, record the escalation, and so on.
}

record RefundRequest(string DecisionId, double Amount);
```

`ledger.Enforcement("d-42")` then returns `Enforcement.BeforeAction`: the gate allowed the refund before it ran.

## What a gate reports

Gates take no assurance claim: build a `PreNode` or `Invariant` from its name, description and monitor or rule. Whether a gate ran before its action is computed from the ledger, not declared. `ledger.Enforcement(decisionId)` reads the order of the decision's `WriteGateDecision` and `WriteAction` records:

| Result | Meaning |
|---|---|
| `BeforeAction` | Every recorded action came after an allowing decision from each gate recorded before it; or a gate denied and no action was recorded. |
| `AfterAction` | An action was recorded with no gate decision before it, or after a gate's most recent decision was a deny. |
| `NotObserved` | The ledger can't tell: no action record to compare against, or no gate record at all. |

Pass `primitiveName` to judge one gate. `EnforcementByDecision()` does it for every decision. See [vsl-core's "What a gate reports"](https://github.com/supersemantics/VSL-Core#what-a-gate-reports).

**Upgrading from 0.1.x.** The constructors that take an `AssuranceBasis` still work and are marked `[Obsolete]`, so the compiler warns (CS0618): use the overload without it. The basis changes nothing. `AssuranceLevel` is obsolete too, and is `null` for a construct built without a basis. `AssuranceBasis`, `F2Modification` and `AssuranceLevel` are removed in 1.0. With `TreatWarningsAsErrors`, the warning fails the build until the argument is removed.

## The ledger doesn't write itself

Compiling and calling a gate is the only automatic part. Recording that it ran is a separate act your code performs: `WriteGateDecision` for a gate's decision, `WriteAction` when the guarded action runs, `WriteMonitor`, `WriteVerification` and `Write` for the rest, and `WriteHandover` when one agent hands part of a decision to another. An agent that never writes has working gates and no audit trail, and this package cannot tell the difference.

`VerifyIntegrity()` recomputes every hash and never throws. `Write` refuses to extend a chain whose last entry fails its own hash. `Audit()` runs the specification's five checks, matching causally through `causedBy` when it is set; action and handover records don't count as monitoring in the gap check. `IssueCertificate()` returns a certificate only for a fully passing audit, and the certificate certifies the governance process, not the underlying system. `CurrentCheckpoint()` gives the chain's tip for an external party to witness, because internal consistency alone cannot detect truncation.

`JsonlLedgerStore` appends under a lock on a sibling `.lock` file. The lock is the same one vsl-core takes (a byte-range lock on Windows, `flock` elsewhere), so .NET and Python writers can share a file. Pass `fsync: true` in production.

## Compatibility with vsl-core

The test suite checks this package against output from the real Python package (`tools/generate_vectors.py`):

- Float formatting: over 2,500 doubles, including edge cases, format exactly as Python's `repr`.
- Canonical JSON: key order by code point, `\uXXXX` escaping, integers and floats kept distinct.
- Entry hashes, cluster keys, transition and certificate hashes, and the Gamma rules, bit for bit.
- A JSONL ledger written by this package is byte-identical to the one Python writes from the same entries.
- `EnforcementByDecision()` reads a Python-written ledger exactly as vsl-core's `enforcement_by_decision()` does.

Two rules follow from matching Python exactly:

- A payload integer (`int`, `long`) and a payload double (`double`) are different values, as in Python: `2` and `2.0` hash differently.
- `decimal` payload values are refused. Python's json module cannot write one either, and converting to `double` would change the value. Use minor units (`long`), a `double` or a string.

Names follow .NET conventions:

| vsl-core (Python) | SuperSemantics.Vsl (.NET) |
|---|---|
| `InvariantViolation` | `InvariantViolationException` |
| `write_gate_decision(...)` | `WriteGateDecision(...)` |
| `write_action(...)` | `WriteAction(...)` |
| `enforcement(decision_id)` | `Enforcement(decisionId)` |
| `Enforcement.BEFORE_ACTION` | `Enforcement.BeforeAction` (`ToWire()` gives `BEFORE_ACTION`) |
| `ROBUST_GAMMA_DEFAULT_THRESHOLD` | `Thresholds.RobustGammaDefault` |
| `ASSURANCE_RULES_VERSION` | `AssuranceRules.Version` |
| `request_re_enablement(...)` | `Governance.RequestReEnablement(...)` |
| `Instance._re_enable()` | `internal`: not callable from outside the package |
| `run_conformance_suite(adapter)` | `await ConformanceSuite.RunAsync(adapter)` |
| `PlainPythonReferenceAdapter` | `PlainReferenceAdapter` |
| `vsl_core.catalog.loader` | `SuperSemantics.Vsl.Catalog.DriftCatalog` |

One deliberate strengthening: in Python, `Instance._re_enable()` is private only by naming convention. Here it is `internal`, so the compiler enforces that the only way out of a Terminal State is `Governance.RequestReEnablement`, which also produces the `HumanAuthorisedTransition` that authorises it.

Not yet ported: the optional immudb store.

## Building an adapter

Implement `IVslAdapter` (`CompilePreNode`, `CompileInvariant`) and get an empty list from `await ConformanceSuite.RunAsync(yourAdapter)` before calling the adapter VSL-conformant. The suite proves, with a real `PreNode`/`Invariant` and a side-effect counter, that your compiled gates:

1. block before any side effect when Gamma is insufficient (F1);
2. pass through when Gamma is sufficient;
3. throw `InvariantViolationException` specifically when an Invariant fails;
4. don't throw when an Invariant holds;
5. are reusable, with no state leaking between calls.

An adapter must not write to the ledger: that is the application's choice, not the adapter's. `PlainReferenceAdapter` is the minimal reference implementation.

## The catalog

`DriftCatalog` loads the same data vsl-core ships: 45 Drift Classes across five categories and 10 Stabilisation Operators. It is explicitly **not** a validated detection system: 106 of the 113 Legion heuristics are self-labelled `SPECULATIVE`. `OnlyValidatedHeuristics()` returns the seven with real confidence, and `Validate()` reports the data's known quality findings (a stale class count, an orphan `CLUSTER-HANDOVER` entry, 10 classes with no heuristics) without fixing them.

## Build and test

```bash
dotnet test
```

To regenerate the Python vectors after a vsl-core change:

```bash
PYTHONPATH=../vsl-core/src python tools/generate_vectors.py
```

## Layout

```
src/SuperSemantics.Vsl/
├── Exceptions.cs        exception hierarchy
├── AssuranceRules.cs    rules version written into gate-decision records
├── Metrics.cs           GammaEstimate, Delta, Ges, Eef, Beta, thresholds (and the deprecated AssuranceBasis)
├── Identity.cs          IdentityKey, ClusterKey, Instance, Evidence
├── Constructs.cs        AllowedState, PreNode, Invariant, TerminalState, Fallback
├── Cluster.cs           Cluster, ClusterPreNode
├── Governance.cs        GovernanceAuthority, HumanAuthorisedTransition, Proofing, Binding, Specification
├── Ledger/              VerbaLedger, LedgerEntry, stores, Python-compatible JSON
├── Catalog/             DriftCatalog and its data
└── Conformance/         IVslAdapter, ConformanceSuite, PlainReferenceAdapter
```

## License

MIT, see [LICENSE](https://github.com/supersemantics/VSL-Core.NET/blob/main/LICENSE). Copyright Super Semantics.

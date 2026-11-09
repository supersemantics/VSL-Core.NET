# VSL-Core.NET

The .NET port of [vsl-core](https://github.com/supersemantics/VSL-Core): framework-agnostic and model-agnostic **VSL** (VERBA Specification Language) governance primitives, for deciding whether an automated system's next action is allowed to happen and proving that the decision was made.

- NuGet package ID `SuperSemantics.Vsl` (not yet published), namespace `SuperSemantics.Vsl`.
- Targets .NET 10 (LTS). No dependencies beyond the .NET base library.
- Ports vsl-core 0.3.0, assurance rules `2026.10`.
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
    // No model at the guarded call: F1 = true, F2 = None.
    assuranceBasis: new AssuranceBasis(f1PreCommitment: true, F2Modification.None),
    gammaThreshold: Thresholds.RobustGammaDefault);

CompiledGate gate = new PlainReferenceAdapter().CompilePreNode(refundPreNode);

try
{
    await gate(new RefundRequest("d-42", Amount: 120.0));   // runs before the refund
    IssueRefund();                                         // only reached if the gate passed
}
catch (AutomationDeniedException denied)
{
    // Denied before any side effect. Route to a human, record the escalation, and so on.
}

record RefundRequest(string DecisionId, double Amount);
```

## The F1/F2 assurance distinction

Every claim about how trustworthy a governance mechanism is reduces to one test:

- **F1 (pre-commitment):** does the check run *before* the system commits to an action, with no side effect yet fired?
- **F2 (energy-landscape modification):** how much does the application change what the model produces, judged at the call the gate guards?

| Gate pattern | F1 | F2 | Level |
|---|---|---|---|
| Amount cap before `IssueRefund` (no model involved) | true | `None` | Low |
| Gate before a hosted-model call whose system prompt the application writes | true | `Partial` | Medium |
| Gate before a self-hosted model call where the application steers logits or activations | true | `Full` | High |
| A check that runs after the action, or only logs | false | any | Low |

F2 is never earned by the gate itself: allowing or blocking changes nothing about what a model produces. A hosted model API rules out `Full`. The level measures influence on the model, not enforcement: a Low gate with F1 = true still blocks every action it denies. Report the two separately.

`AssuranceLevel` is derived from an `AssuranceBasis` and cannot be set directly, and an undefined `F2Modification` value is rejected. But **stating F1/F2 is not proving them**: nothing in this package checks that a stated `Full` reflects what the monitor actually does. Treat a derived level as what a developer asserted.

## The ledger doesn't write itself

Compiling and calling a gate is the only automatic part. Recording that it ran is a separate act your code performs: `WriteGateDecision` for a gate's decision, `WriteMonitor`, `WriteVerification` and `Write` for the rest, and `WriteHandover` when one agent hands part of a decision to another. An agent that never writes has working gates and no audit trail, and this package cannot tell the difference.

`VerifyIntegrity()` recomputes every hash and never throws. `Write` refuses to extend a chain whose last entry fails its own hash. `Audit()` runs the specification's five checks, matching causally through `causedBy` when it is set. `IssueCertificate()` returns a certificate only for a fully passing audit, and the certificate certifies the governance process, not the underlying system. `CurrentCheckpoint()` gives the chain's tip for an external party to witness, because internal consistency alone cannot detect truncation.

`JsonlLedgerStore` appends under a lock on a sibling `.lock` file. The lock is the same one vsl-core takes (a byte-range lock on Windows, `flock` elsewhere), so .NET and Python writers can share a file. Pass `fsync: true` in production.

## Compatibility with vsl-core

The test suite checks this package against output from the real Python package (`tools/generate_vectors.py`):

- Float formatting: over 2,500 doubles, including edge cases, format exactly as Python's `repr`.
- Canonical JSON: key order by code point, `\uXXXX` escaping, integers and floats kept distinct.
- Entry hashes, cluster keys, transition and certificate hashes, and the Gamma rules, bit for bit.
- A JSONL ledger written by this package is byte-identical to the one Python writes from the same entries.

Two rules follow from matching Python exactly:

- A payload integer (`int`, `long`) and a payload double (`double`) are different values, as in Python: `2` and `2.0` hash differently.
- `decimal` payload values are refused. Python's json module cannot write one either, and converting to `double` would change the value. Use minor units (`long`), a `double` or a string.

Names follow .NET conventions:

| vsl-core (Python) | SuperSemantics.Vsl (.NET) |
|---|---|
| `InvariantViolation` | `InvariantViolationException` |
| `write_gate_decision(...)` | `WriteGateDecision(...)` |
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
├── Metrics.cs           AssuranceBasis, GammaEstimate, Delta, Ges, Eef, Beta, thresholds
├── Identity.cs          IdentityKey, ClusterKey, Instance, Evidence
├── Constructs.cs        AllowedState, PreNode, Invariant, TerminalState, Fallback
├── Cluster.cs           Cluster, ClusterPreNode
├── Governance.cs        GovernanceAuthority, HumanAuthorisedTransition, Proofing, Binding, Specification
├── Ledger/              VerbaLedger, LedgerEntry, stores, Python-compatible JSON
├── Catalog/             DriftCatalog and its data
└── Conformance/         IVslAdapter, ConformanceSuite, PlainReferenceAdapter
```

## License

MIT, see [LICENSE](LICENSE). Copyright Super Semantics.

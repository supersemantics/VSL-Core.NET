# Changelog

All notable changes, newest first. Each entry says what was there before, what changed, and why.
Tagged `vX.Y.Z` on `main` when a pull request merges.

---

## 0.1.1 (unreleased): the README works as the NuGet package page

**Before:** the README, which NuGet shows as the package page, said the package was "not yet published", and its LICENSE link was relative, so it broke on nuget.org.

**After:** the README gives the `dotnet add package` command and links to LICENSE on GitHub. The code is unchanged from 0.1.0.

**Why:** 0.1.1 is the first version published to NuGet, and a published version's page can't be changed later.

## 0.1.0 (2026-11-09, tag `v0.1.0`)

### First release: vsl-core for .NET

**Before:** VSL governance primitives existed for Python (vsl-core) and TypeScript/JavaScript (vsl-corejs) only. A .NET agent, for example one built on Microsoft Agent Framework, had no way to write a VSL gate.

**After:** `SuperSemantics.Vsl` ports vsl-core 0.3.0 (assurance rules `2026.10`) to .NET 10, with no dependencies beyond the base library:
- the constructs (`PreNode`, `Invariant`, `TerminalState`, `AllowedState`, `Fallback`), `Cluster`, identity and governance types;
- `VerbaLedger` with in-memory and JSONL stores, gate-decision and handover records, the five audit checks, checkpoints and certificates;
- the Drift Class catalog;
- the conformance suite and a reference adapter.

Ledger files and hashes match vsl-core byte for byte, unlike vsl-corejs, which only aims to be consistent with itself. The JSONL store takes the same cross-process lock as vsl-core, so .NET and Python writers can share a ledger file.

`Instance.ReEnable` is `internal`, where vsl-core's `_re_enable` is private only by convention: outside the package, `Governance.RequestReEnablement` is the only way out of a Terminal State.

**Why:** to let .NET agents use VSL gates, and to keep one audit format across languages, so a ledger can be verified with whichever runtime the auditor has.

**Not included:** the optional immudb store; framework adapters (separate packages).

**Tests:** 52 pass on .NET 10.0.403, Windows 11. They include Python vectors generated from vsl-core 0.3.0 (Python 3.12.10), among them 2,538 float formatting cases. Also checked by hand: a .NET process and a Python process wrote 300 entries each to one JSONL file at the same time, and both runtimes verified the 600-entry chain.

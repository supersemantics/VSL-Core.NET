# Changelog

All notable changes, newest first. Each entry says what was there before, what changed, and why.
Tagged `vX.Y.Z` on `main` when a pull request merges.

---

## Unreleased: the publish workflow no longer adds a GitHub build attestation

**Before:** 0.1.2's publish workflow added a GitHub build attestation for the package, and this CHANGELOG said anyone could check the package with `gh attestation verify`.

**After:** the attestation step is gone, and the 0.1.2 entry below is corrected. Publishing is otherwise unchanged: Trusted Publishing, a one-hour key, no stored API key. No new package version: nothing in the package changes.

**Why:** nuget.org adds its own repository signature to every package it accepts, which changes the file. The attestation covered only the file built in GitHub Actions, never the one you download from nuget.org, so it couldn't be used to check that download. To check a package from nuget.org, use its signature (`dotnet nuget verify --all <file>.nupkg`).

## 0.1.2 (2026-11-14, tag `v0.1.2`): published from GitHub Actions through NuGet Trusted Publishing

**Before:** 0.1.1 was packed on a laptop and pushed to nuget.org with a personal API key.

**After:** pushing a `vX.Y.Z` tag runs `.github/workflows/publish.yml`. It runs the tests, checks the tag matches the version, packs `SuperSemantics.Vsl` (with symbols), adds a GitHub build attestation (see Unreleased: it did not match the file on nuget.org), and pushes to nuget.org with a key that nuget.org issues for one hour to this repository, workflow and `nuget` environment only. No API key is stored anywhere. The code is unchanged from 0.1.1.

**Why:** PyPI and npm already publish this way. A key stored on a laptop can leak and has to be rotated by hand; a one-hour key tied to this workflow can't be reused elsewhere.

## 0.1.1 (2026-11-12, tag `v0.1.1`): the README works as the NuGet package page

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

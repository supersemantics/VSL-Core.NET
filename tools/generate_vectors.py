"""Generates tests/SuperSemantics.Vsl.Tests/Vectors/python-vectors.json from
the real Python vsl-core package, so the .NET port is checked against
Python's own output byte for byte: float formatting, canonical JSON, ledger
entry hashes, cluster keys, transition and certificate hashes, and the
Gamma sufficiency rule.

Run from the repository root with vsl-core importable, for example:

    PYTHONPATH=../vsl-core/src python tools/generate_vectors.py

Deterministic: fixed seeds, fixed ids and timestamps.
"""
import json
import math
import random
import struct
import sys
from pathlib import Path

import vsl_core
from vsl_core.governance import GovernanceAuthority, HumanAuthorisedTransition
from vsl_core.identity import ClusterKey, Evidence, IdentityKey
from vsl_core.ledger import (
    InMemoryLedgerStore,
    LedgerAuditReport,
    LedgerEntry,
    LedgerEntryType,
    VerbaCertificate,
    _compute_entry_hash,
    _seal,
    GENESIS_HASH,
)
from vsl_core.metrics import GammaEstimate

OUT = Path(__file__).resolve().parent.parent / "tests" / "SuperSemantics.Vsl.Tests" / "Vectors" / "python-vectors.json"


def bits(value: float) -> str:
    return struct.pack(">d", value).hex()


def float_cases() -> list:
    rng = random.Random(20261109)
    values = [
        0.0, -0.0, 1.0, -1.0, 0.1, 0.5, 1.1, 1.2, 2.0, 5.0, 10.0, 100.0, 1e15, 1e16, 1.5e16, 1e17,
        123456789012345678.0, 1e-4, 1e-5, 0.00012345, 1.5e-5, 1e-300, 5e-324, 1.7976931348623157e308,
        1700000000.123456, 1762700000.5, 0.1 + 0.2, 1 / 3, 2 / 3, math.pi, math.e, 9007199254740993.0,
        float("inf"), float("-inf"), float("nan"), 1.1 / 1.1, 1.0 / (1.0 + 0.1), 5.0 * (1.0 / 1.1),
    ]
    for _ in range(1500):
        values.append(rng.uniform(-1e6, 1e6))
    for _ in range(500):
        values.append(rng.random() * 10 ** rng.randint(-320, 308))
    for _ in range(500):
        values.append(struct.unpack(">d", struct.pack(">Q", rng.getrandbits(64)))[0])
    out = []
    for v in values:
        out.append({"bits": bits(v), "json": json.dumps(v)})
    return out


def json_cases() -> list:
    cases = [
        {"b": 1, "a": 2},
        {"text": "plain ascii"},
        {"quote": "he said \"hi\"", "backslash": "a\\b", "slash": "a/b"},
        {"controls": "\b\f\n\r\t\x00\x01\x1f\x7f"},
        {"latin": "café naïve", "greek": "Γάμμα", "cjk": "日本語", "emoji": "agent 🤖 ok", "rtl": "שלום"},
        {"nested": {"z": [1, 2.0, "three", None, True, False, {"k": 1.5e-7}], "a": {}}, "list": []},
        {"ints": [0, -1, 9007199254740993, -9223372036854775808, 123456789012345678901234567890]},
        {"floats": [0.1, 2.0, 1e16, 1e-5, -0.0]},
        {"é": 1, "e": 2, "\U0001f916": 3, "￿": 4, "Z": 5, "a": 6},
        {"empty_key": {"": "empty"}},
    ]
    out = []
    for case in cases:
        out.append(
            {
                "value": case,
                "compact": json.dumps(case, sort_keys=True, separators=(",", ":")),
                "default": json.dumps(case, sort_keys=True),
            }
        )
    return out


def ledger_chain() -> dict:
    store = InMemoryLedgerStore()
    drafts = [
        LedgerEntry(
            entry_type=LedgerEntryType.MONITOR,
            identity_key="refund-agent",
            decision_id="d-1",
            schema_version="1.0",
            payload={
                "primitive_name": "refund-within-policy",
                "allowed": True,
                "gamma_hat": 2.5,
                "delta_estimation_error": 0.0,
                "robust_gamma": 2.5,
                "gamma_threshold": 1.1,
                "required_gamma_hat": 1.1,
                "rules_version": "2026.10",
                "drift_detected": False,
            },
            timestamp=1762700000.123456,
            entry_id="00000000-0000-4000-8000-000000000001",
        ),
        LedgerEntry(
            entry_type=LedgerEntryType.MONITOR,
            identity_key="refund-agent",
            instance_id="inst-1",
            decision_id="d-2",
            schema_version="1.0",
            payload={"drift_detected": True, "note": "amount 1 200 € exceeds cap", "count": 3},
            timestamp=1762700001.0,
            entry_id="00000000-0000-4000-8000-000000000002",
        ),
        LedgerEntry(
            entry_type=LedgerEntryType.PRE_NODE,
            identity_key="refund-agent",
            instance_id="inst-1",
            decision_id="d-2",
            caused_by="00000000-0000-4000-8000-000000000002",
            schema_version="1.0",
            payload={"pre_node": "refund-within-policy"},
            timestamp=1762700002.5,
            entry_id="00000000-0000-4000-8000-000000000003",
        ),
        LedgerEntry(
            entry_type=LedgerEntryType.TERMINAL,
            identity_key="refund-agent",
            cluster_key="cluster-abc",
            schema_version=None,
            payload={},
            timestamp=1762700003.0,
            entry_id="00000000-0000-4000-8000-000000000004",
        ),
        LedgerEntry(
            entry_type=LedgerEntryType.MONITOR,
            identity_key="intake-agent",
            decision_id="d-3",
            schema_version="1.0",
            payload={
                "handover": {"from_identity": "intake-agent", "to_identity": "refund-agent", "reason": "refund request"},
                "drift_detected": False,
            },
            timestamp=1762700004.25,
            entry_id="00000000-0000-4000-8000-000000000005",
        ),
    ]
    sealed = [store.append(d).to_dict() for d in drafts]
    jsonl = "".join(json.dumps(e, sort_keys=True) + "\n" for e in sealed)
    return {"entries": sealed, "jsonl": jsonl, "genesis": GENESIS_HASH}


def misc() -> dict:
    keys = (IdentityKey("agent-b"), IdentityKey("agent-a"), IdentityKey("agënt-ç"))
    cluster = ClusterKey.from_constituents(keys)
    authority = GovernanceAuthority(name="safety-board", escalation_contact="oncall@example.com")
    transition = HumanAuthorisedTransition.authorise(
        authority,
        authorised_by="jane.doe",
        evidence=Evidence(root_cause_analysis="alarm loop — fixed", specification_update_proposal="add heartbeat invariant"),
    )
    report = LedgerAuditReport(
        checks_passed=("no_monitoring_gaps", "drift_flagged_monitor_has_pre_node"),
        checks_failed=(),
        violations={},
    )
    certificate = VerbaCertificate(
        issued_at=1762700010.75, period_start=1762700000.123456, period_end=1762700004.25, entries_covered=5, audit_report=report
    )
    gamma = []
    for gamma_hat in (0.0, 0.5, 1.0, 1.1, 1.1000000000000001, 1.2, 1.21, 2.0, 5.0):
        for delta in (0.0, 0.05, 0.1, 0.2, 0.5):
            for threshold in (1.1, 1.2):
                e = GammaEstimate(gamma_hat=gamma_hat, delta_estimation_error=delta, measured_at=0.0)
                gamma.append(
                    {
                        "gamma_hat": gamma_hat,
                        "delta": delta,
                        "threshold": threshold,
                        "robust_gamma_bits": bits(e.robust_gamma()),
                        "required_bits": bits(e.required_gamma_hat(threshold)),
                        "sufficient": e.sufficient(threshold),
                    }
                )
    return {
        "cluster": {"constituents": [k.value for k in keys], "value": cluster.value},
        "transition": {"evidence": list(transition.evidence), "hash": transition.authorisation_evidence_hash},
        "certificate": {
            "issued_at": certificate.issued_at,
            "period_start": certificate.period_start,
            "period_end": certificate.period_end,
            "entries_covered": certificate.entries_covered,
            "checks_passed": list(report.checks_passed),
            "checks_failed": list(report.checks_failed),
            "hash": certificate.certificate_hash,
        },
        "gamma": gamma,
    }


def main() -> None:
    vectors = {
        "generated_from": f"vsl-core {vsl_core.__version__}, rules {vsl_core.ASSURANCE_RULES_VERSION}, Python {sys.version.split()[0]}",
        "floats": float_cases(),
        "json": json_cases(),
        "ledger": ledger_chain(),
        **misc(),
    }
    OUT.write_text(json.dumps(vectors, indent=1, ensure_ascii=True) + "\n", encoding="utf-8")
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()

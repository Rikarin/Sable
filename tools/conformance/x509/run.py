#!/usr/bin/env python3
"""Sable.Crypto.X509's verifier against x509-limbo.

    python3 -I tools/conformance/x509/run.py data/limbo.json [--profile compatible]
"""

import argparse
import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import common

PURPOSES = {
    "serverAuth": "1.3.6.1.5.5.7.3.1", "clientAuth": "1.3.6.1.5.5.7.3.2", "codeSigning": "1.3.6.1.5.5.7.3.3",
    "emailProtection": "1.3.6.1.5.5.7.3.4", "timeStamping": "1.3.6.1.5.5.7.3.8", "OCSPSigning": "1.3.6.1.5.5.7.3.9",
    "anyExtendedKeyUsage": "2.5.29.37.0",
}

# What is not asked, and why.
FEATURES = {
    "has-mldsa": "ML-DSA, which is #124",
    "pedantic-public-suffix-wildcard": "the public suffix list",
}

# What the other profile's tests say the opposite of.
CASES = {
    "rfc5280::nc::permitted-dns-match-noncritical": "a name constraint that is not critical is taken, as webpki::nc::permitted-dns-match-noncritical has it",
}

# The profile each part is of, where none is asked for.
PROFILES = {"webpki": "webpki", "bettertls": "compatible", "online": "compatible", "cve": "compatible"}


def words(*texts):
    return ",".join(text.encode().hex() for text in texts) or "-"


def name(peer):
    return {"DNS": "DNS:", "IP": "IP:", "RFC822": "email:"}[peer["kind"]] + peer["value"]


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("data", type=pathlib.Path)
    parser.add_argument("--profile", choices=["compatible", "rfc5280", "webpki"])
    arguments = parser.parse_args()
    tally = common.Tally()
    asked = []
    for case in json.loads(arguments.data.read_text())["testcases"]:
        part = case["id"].split("::")[0]
        reason = CASES.get(case["id"]) or next((FEATURES[each] for each in case["features"] or [] if each in FEATURES), None)
        if reason:
            tally.skip(part, reason)
            continue
        host = case.get("expected_peer_name")
        names = case.get("expected_peer_names") or []
        purposes = [PURPOSES[each] for each in case["extended_key_usage"]] or [PURPOSES["serverAuth" if case["validation_kind"] == "SERVER" else "clientAuth"]]
        line = " ".join([
            "verify", arguments.profile or PROFILES.get(part, "rfc5280"), words(case["validation_time"]) if case["validation_time"] else "-",
            words(host["value"]) if host else "-", words(*map(name, names)), words(*purposes),
            "-" if case["max_chain_depth"] is None else str(case["max_chain_depth"]), words(case["peer_certificate"]),
            words(*case["untrusted_intermediates"]), words(*case["trusted_certs"]), words(*case["crls"]),
        ])
        asked.append((part, case, line))
    answers = common.ask(common.build(HERE / "answer.sb"), [line for _, _, line in asked])
    for (part, case, _), answer in zip(asked, answers):
        tally.check(part, (answer == "valid") == (case["expected_result"] == "SUCCESS"), f"{case['id']} {case['features'] or ''}: {case['expected_result']}, answered {answer}")
    tally.finish()


main()

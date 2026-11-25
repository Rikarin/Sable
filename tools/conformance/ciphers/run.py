#!/usr/bin/env python3
"""Sable.Crypto's ciphers against NIST's CAVP and ACVP vectors and
Wycheproof's.

    python3 -I tools/conformance/ciphers/run.py <data>

tools/conformance/README.md says what is in the directory of data."""

import argparse
import json
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from common import Tally, ask, build

tally = Tally()
# Each: the part of the suite, what a failure is called, what the answer must
# be, and the request.
asked = []


def want(part, name, expected, *words):
    asked.append((part, str(name), (expected or "-").lower(), " ".join(str(word) or "-" for word in words)))


def aead(part, name, cipher, direction, key, nonce, data, plaintext, ciphertext, tag, valid):
    """A message sealed, or opened, as the vector has it."""
    size = len(tag) // 2
    if cipher == "GCM" and not 12 <= size <= 16:
        return tally.skip(part, "of a tag shorter than the library takes")
    if direction != "decrypt" and valid:
        want(part, f"{name} sealed", f"{ciphertext or '-'} {tag}", "seal", cipher, key, nonce, data, plaintext, size)
    if direction != "encrypt":
        want(part, f"{name} opened", plaintext if valid else "error", "open", cipher, key, nonce, data, ciphertext, tag)


# ---- CAVP ---------------------------------------------------------------------

def records(path):
    """A file's records: what each 'key = value' before its end, and not set
    again since, says; and under '' the section's one name. A record is a run
    of lines that has a 'Count'."""
    said, section, here = {}, "", set()
    for line in path.read_text(encoding="latin-1").splitlines() + [""]:
        line = line.strip()
        if line.startswith("#"):
            continue
        if line.startswith("["):
            for part in line.strip("[]").split(","):
                key, equals, value = part.partition("=")
                if equals:
                    said[key.strip().upper()] = value.strip()
                else:
                    section = key.strip()
        elif line:
            key, _, value = line.partition("=")
            said[key.strip().upper()] = value.strip()
            here.add(key.strip().upper())
        elif here:
            if "COUNT" in here:
                yield {**{key: value for key, value in said.items() if key in here or key not in ("FAIL", "PT", "RESULT")}, "": section}
            here = set()


def cavp(root):
    for path in sorted(root.rglob("*.rsp")) + sorted(root.rglob("KW*_A?_???*.txt")):
        stem = path.stem
        part = f"CAVP {stem}"
        if stem.startswith(("CFB1GFS", "CFB1Key", "CFB1Var", "CFB1MMT", "CFB1MCT", "OFB")):
            tally.skip(part, "CFB1 or OFB, which the library has not")
            continue
        if stem.startswith("KW"):
            match = re.fullmatch(r"(KWP?)_A([DE])_\d+(_inv)?", stem)
            for record in records(path):
                if match[3]:
                    tally.skip(part, "by the inverse cipher, which the library does not do")
                elif match[2] == "E":
                    want(part, record["COUNT"], record["C"], "wrap", match[1], record["K"], record["P"])
                else:
                    want(part, record["COUNT"], record.get("P") if "FAIL" not in record else "error", "unwrap", match[1], record["K"], record["C"])
        elif stem.startswith("gcm"):
            decrypt = stem.startswith("gcmDecrypt")
            for record in records(path):
                aead(part, record["COUNT"], "GCM", "decrypt" if decrypt else "encrypt", record["KEY"], record["IV"], record["AAD"], record.get("PT", ""), record["CT"], record["TAG"], "FAIL" not in record)
        elif path.parent.name == "ccmtestvectors" or re.fullmatch(r"(DVPT|VADT|VNT|VPT|VTT)\d+", stem):
            for record in records(path):
                size = int(record["PLEN"]) * 2
                data = record["ADATA"][: int(record["ALEN"]) * 2]
                valid = not record.get("RESULT", "Pass").startswith("Fail")
                plaintext = record.get("PAYLOAD", "")[:size]
                aead(part, record["COUNT"], "CCM", "decrypt" if stem.startswith("DVPT") else None, record["KEY"], record["NONCE"], data, plaintext, record["CT"][:size], record["CT"][size:], valid)
        else:
            mode, kind = re.match(r"(ECB|CBC|CFB128|CFB8)(MCT|MMT|.*)\d{3}$", stem).groups()
            for record in records(path):
                direction = record[""].lower()
                given, made = ("PLAINTEXT", "CIPHERTEXT") if direction == "encrypt" else ("CIPHERTEXT", "PLAINTEXT")
                iv = record.get("IV", "")
                want(part, f"{direction} {record['COUNT']}", record[made], "monte" if kind == "MCT" else "mode", mode, direction, record["KEY"], iv, record[given])


# ---- ACVP ---------------------------------------------------------------------

def acvp(root):
    for path in sorted(root.glob("ACVP-AES-*.json")):
        name = path.stem[len("ACVP-AES-"):-len("-1.0")]
        part = f"ACVP AES-{name}"
        for group in json.loads(path.read_text())["testGroups"]:
            direction = group["direction"]
            for case in group["tests"]:
                title = case["tcId"]
                if name in ("KW", "KWP"):
                    if group["kwCipher"] != "cipher":
                        tally.skip(part, "by the inverse cipher, which the library does not do")
                    elif group["payloadLen"] % 8:
                        tally.skip(part, "not of whole bytes")
                    elif direction == "encrypt":
                        want(part, title, case["ct"], "wrap", name, case["key"], case["pt"])
                    else:
                        want(part, title, case["pt"] if case["testPassed"] else "error", "unwrap", name, case["key"], case["ct"])
                elif name == "GCM":
                    if any(group[length] % 8 for length in ("ivLen", "payloadLen", "aadLen", "tagLen")):
                        tally.skip(part, "not of whole bytes")
                        continue
                    aead(part, title, "GCM", direction, case["key"], case["iv"], case["aad"], case.get("pt") or "", case.get("ct") or "", case["tag"], case.get("testPassed", True))
                elif name == "CCM":
                    size = len(case["ct"]) - group["tagLen"] // 4
                    aead(part, title, "CCM", direction, case["key"], case["iv"], case["aad"], case.get("pt") or "", case["ct"][:size], case["ct"][size:], case.get("testPassed", True))
                elif group["testType"] == "MCT":
                    for i, result in enumerate(case["resultsArray"]):
                        given, made = ("pt", "ct") if direction == "encrypt" else ("ct", "pt")
                        want(part, f"{title} {i}", result[made], "monte", name, direction, result["key"], result.get("iv", ""), result[given])
                elif case.get("payloadLen", 0) % 8 or ("pt" not in case or "ct" not in case or "iv" not in case) and name == "CTR":
                    tally.skip(part, "not of whole bytes, or with counters of its own for each block")
                else:
                    given, made = ("pt", "ct") if direction == "encrypt" else ("ct", "pt")
                    want(part, title, case[made], "mode", name, direction, case["key"], case.get("iv", ""), case[given])


# ---- Wycheproof ---------------------------------------------------------------

def wycheproof(root):
    aeads = {"aes_gcm": "GCM", "aes_gmac": "GCM", "aes_ccm": "CCM", "chacha20_poly1305": "CHACHA", "xchacha20_poly1305": "XCHACHA"}
    for name in [*aeads, "aes_cbc_pkcs5", "aes_wrap", "aes_kwp"]:
        for path in sorted(root.rglob(f"testvectors_v1/{name}_test.json")):
            part = f"Wycheproof {name}"
            for group in json.loads(path.read_text())["testGroups"]:
                for case in group["tests"]:
                    title = f"{case['tcId']} {case.get('comment', '')}"
                    valid = case["result"] != "invalid"
                    if name == "aes_gmac":
                        aead(part, title, "GCM", None, case["key"], case["iv"], case["msg"], "", "", case["tag"], valid)
                    elif name in aeads:
                        aead(part, title, aeads[name], None, case["key"], case["iv"], case["aad"], case["msg"], case["ct"], case["tag"], valid)
                    elif name == "aes_cbc_pkcs5":
                        if valid:
                            want(part, title, case["ct"], "mode", "PKCS7", "encrypt", case["key"], case["iv"], case["msg"])
                        want(part, title, case["msg"] if valid else "error", "mode", "PKCS7", "decrypt", case["key"], case["iv"], case["ct"])
                    else:
                        kind = "KW" if name == "aes_wrap" else "KWP"
                        if case["result"] == "acceptable":
                            # Keys of 8 bytes, which RFC 3394 does not have: refused.
                            valid = False
                        if valid:
                            want(part, title, case["ct"], "wrap", kind, case["key"], case["msg"])
                        want(part, title, case["msg"] if valid else "error", "unwrap", kind, case["key"], case["ct"])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("data", type=pathlib.Path)
    arguments = parser.parse_args()
    cavp(arguments.data / "cavp")
    acvp(arguments.data / "acvp")
    wycheproof(arguments.data / "wycheproof")
    answers = ask(build(pathlib.Path(__file__).resolve().parent / "answer.sb"), [request for _, _, _, request in asked])
    for (part, name, expected, request), answer in zip(asked, answers):
        tally.check(part, answer == expected, f"{name}: {request[:160]} -> {answer[:120]}, not {expected[:120]}")
    tally.finish()


main()

#!/usr/bin/env python3
"""Sable.Crypto, and the ChaCha generator, against NIST's CAVP and ACVP
vectors and Wycheproof's.

    python3 -I tools/conformance/crypto/run.py <data> [--large <GiB>]

tools/conformance/README.md says what is in the directory of data. ACVP's
large-data tests are run up to so many GiB each (default 1, 0 for none)."""

import argparse
import json
import pathlib
import re
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from common import Tally, ask, build

tally = Tally()
# Each: the part of the suite, the request, what must come of the answer (a
# string, or a function of it), and what a failure is called.
asked = []


def want(part, name, expected, *words):
    asked.append((part, name, expected, " ".join(str(word) or "-" for word in words)))


def names(algorithm):
    """A hash as the vectors call it, as the library does; none it has not."""
    plain = re.sub(r"[^A-Z0-9/_-]", "", algorithm.upper()).replace("HMAC", "").strip("-_")
    return {
        "SHA1": "SHA1", "SHA-1": "SHA1",
        "SHA256": "SHA256", "SHA-256": "SHA256", "SHA2-256": "SHA256",
        "SHA384": "SHA384", "SHA-384": "SHA384", "SHA2-384": "SHA384",
        "SHA512": "SHA512", "SHA-512": "SHA512", "SHA2-512": "SHA512",
        "SHA3-256": "SHA3_256", "SHA3-384": "SHA3_384", "SHA3-512": "SHA3_512",
    }.get(plain)


# ---- CAVP ---------------------------------------------------------------------

def records(path):
    """A .rsp file's records: what each 'key = value' of one, and the [..] of
    its section, say. A record ends at a blank line."""
    section, record = {}, {}
    for line in path.read_text(encoding="latin-1").splitlines() + [""]:
        line = line.strip()
        if line.startswith("#"):
            continue
        if line.startswith("["):
            key, _, value = line.strip("[]").partition("=")
            section[key.strip()] = value.strip()
        elif line:
            key, _, value = line.partition("=")
            record[key.strip()] = value.strip()
        elif record:
            yield {**section, **record}
            record = {}


def cavp(root):
    sizes = {"20": "SHA1", "32": "SHA256", "48": "SHA384", "64": "SHA512"}
    for path in sorted(root.rglob("*.rsp")):
        match = re.fullmatch(r"(SHA3_\d+|SHA512_\d+|SHA\d+|SHAKE\d+|HMAC)(.*)", path.stem)
        name, kind = match.groups()
        part = f"CAVP {path.stem}"
        all_records = list(records(path))
        if name == "HMAC":
            for record in all_records:
                hash_name = sizes.get(record["L"])
                if hash_name is None:
                    tally.skip(part, "SHA-224, which the library has not")
                    continue
                count = int(record["Tlen"]) * 2
                want(part, f"{hash_name} {record['Count']}", lambda answer, mac=record["Mac"], count=count: answer[:count] == mac, "hmac", hash_name, record["Key"], record["Msg"])
            continue
        if name in ("SHA224", "SHA3_224", "SHA512_224", "SHA512_256"):
            for _ in all_records:
                tally.skip(part, "SHA-224, SHA3-224 or SHA-512/t, which the library has not")
            continue
        if kind == "Monte":
            if name.startswith("SHAKE"):
                seed = all_records[0]
                least, most = (int(seed[f"{which} Output Length (bits)"]) // 8 for which in ("Minimum", "Maximum"))
                outputs = [record["Output"] for record in all_records if "Output" in record]
                want(part, "Monte Carlo", "".join(output + "," for output in outputs), "montexof", name, seed["Msg"], least, most)
            else:
                seed = all_records[0]["Seed"]
                digests = [record["MD"] for record in all_records if "MD" in record]
                want(part, "Monte Carlo", "".join(digests), "monte3" if name.startswith("SHA3_") else "monte2", name, seed, 0)
            continue
        for record in all_records:
            if name.startswith("SHAKE"):
                if "Msg" not in record:
                    continue
                count = int(record["Outputlen"]) // 8
                length = int(record.get("Len", record.get("Input Length", 0)))
                want(part, record.get("COUNT", str(length)), record["Output"], "xof", name, record["Msg"][: length // 4], count)
            elif "Msg" in record:
                want(part, record["Len"], record["MD"], "hash", name, record["Msg"][: int(record["Len"]) // 4])


# ---- ACVP ---------------------------------------------------------------------

def concatenated(pattern, case, length):
    """SP 800-56C's fixed info of ACVP's pattern."""
    made = ""
    parameters = case.get("kdfParameter", case)
    for word in pattern.split("||"):
        if word in ("uPartyInfo", "vPartyInfo"):
            party = case["fixedInfoPartyU" if word[0] == "u" else "fixedInfoPartyV"]
            made += party["partyId"] + (party.get("ephemeralData") or "")
        elif word == "l":
            made += f"{length:08x}"
        elif word.startswith("literal["):
            made += word[8:-1]
        elif word in parameters:
            made += parameters[word]
        else:
            return None
    return made


def acvp_hkdf(part, group, case):
    configuration = group.get("kdfConfiguration") or group["kdfMultiExpansionConfiguration"]
    hash_name = names(configuration["hmacAlg"])
    if hash_name is None:
        return tally.skip(part, "a hash the library has not")
    parameters = case.get("kdfParameter") or case["kdfMultiExpansionParameter"]
    secret = parameters["z"] + (parameters.get("t") or "")
    if group.get("multiExpansion"):
        steps = list(zip(parameters["iterationParameters"], case["dkms"]))
        wanted = [(step["fixedInfo"], step["l"], dkm) for step, dkm in steps]
    else:
        info = concatenated(parameters["fixedInfoPattern"], case, parameters["l"])
        if info is None or configuration["fixedInfoEncoding"] != "concatenation":
            return tally.skip(part, "a fixed info this script does not make")
        wanted = [(info, parameters["l"], case["dkm"])]
    for info, length, key in wanted:
        assert length % 8 == 0
        valid = case.get("testPassed", True)
        key = key.lower()
        want(part, f"{case['tcId']}", lambda answer, key=key, valid=valid: (answer == key) == valid, "hkdf", hash_name, secret, parameters["salt"], info, length // 8)


def acvp(root, large):
    for path in sorted(root.rglob("*.json")):
        name = path.stem if path.stem != "internalProjection" else path.parent.name
        data = json.loads(path.read_text())
        if "testGroups" not in data:
            continue
        part = f"ACVP {name}"
        algorithm = re.sub(r"-(\d\.0|FIPS202)$", "", name)
        for group in data["testGroups"]:
            kind = group["testType"]
            for case in group["tests"]:
                title = str(case["tcId"])
                if name.startswith("KDA-HKDF"):
                    acvp_hkdf(part, group, case)
                elif algorithm == "PBKDF":
                    hash_name = names(group["hmacAlg"])
                    if hash_name is None:
                        tally.skip(part, "a hash the library has not")
                        continue
                    want(part, title, case["derivedKey"].lower(), "pbkdf2", hash_name, case["password"].encode().hex(), case["salt"], case["iterationCount"], case["keyLen"] // 8)
                elif algorithm.startswith("HMAC"):
                    bits = [case.get(key, group.get(key)) for key in ("keyLen", "msgLen", "macLen")]
                    if any(count % 8 for count in bits):
                        tally.skip(part, "not of whole bytes")
                        continue
                    count = bits[2] // 4
                    want(part, title, lambda answer, mac=case["mac"].lower(), count=count: answer[:count] == mac, "hmac", names(algorithm[5:]), case["key"], case["msg"])
                elif algorithm.startswith("KMAC"):
                    if case["keyLen"] % 8 or case["msgLen"] % 8 or case["macLen"] % 8:
                        tally.skip(part, "not of whole bytes")
                        continue
                    customization = case["customizationHex"] if group["hexCustomization"] else case["customization"].encode().hex()
                    valid = case.get("testPassed", True)
                    want(part, title, lambda answer, mac=case["mac"].lower(), valid=valid: (answer == mac) == valid, "kmacxof" if group["xof"] else "kmac", algorithm[5:], case["key"], case["msg"], customization, case["macLen"] // 8)
                elif algorithm.startswith("SHAKE"):
                    shake = algorithm.replace("-", "")
                    if kind == "MCT":
                        outputs = "".join(result["md"].lower() + "," for result in case["resultsArray"])
                        want(part, title, outputs, "montexof", shake, case["msg"], (group["minOutLen"] + 7) // 8, group["maxOutLen"] // 8)
                        continue
                    if case["len"] % 8 or case["outLen"] % 8:
                        tally.skip(part, "not of whole bytes")
                        continue
                    want(part, title, case["md"].lower(), "xof", shake, case["msg"][: case["len"] // 4], case["outLen"] // 8)
                else:
                    hash_name = names(algorithm)
                    if kind == "MCT":
                        length = case["len"] // 8 if group.get("mctVersion") == "alternate" else 0
                        want(part, title, "".join(result["md"].lower() for result in case["resultsArray"]), "monte3" if hash_name.startswith("SHA3_") else "monte2", hash_name, case["msg"], length)
                    elif kind == "LDT":
                        count = case["largeMsg"]["fullLength"] // 8
                        if count > large << 30:
                            tally.skip(part, f"of {count >> 30} GiB")
                            continue
                        want(part, title, case["md"].lower(), "large", hash_name, case["largeMsg"]["content"], count)
                    elif case["len"] % 8:
                        tally.skip(part, "not of whole bytes")
                    else:
                        want(part, title, case["md"].lower(), "hash", hash_name, case["msg"][: case["len"] // 4])


# ---- Wycheproof ---------------------------------------------------------------

def wycheproof(root):
    for pattern in ("hmac_*", "hkdf_*", "kmac*", "pbkdf2_*", "chacha20_poly1305_test"):
        for path in sorted(root.rglob(f"testvectors_v1/{pattern}.json")):
            data = json.loads(path.read_text())
            part = f"Wycheproof {data['algorithm']}"
            kind = data["algorithm"].split("-")[0].rstrip("0123456789") if not data["algorithm"].startswith("CHACHA") else "CHACHA"
            for group in data["testGroups"]:
                for case in group["tests"]:
                    title = f"{case['tcId']} {case.get('comment', '')}"
                    valid = case["result"] != "invalid"
                    if kind == "CHACHA":
                        # Only the generator: ciphers/run.py has the cipher.
                        if not valid or len(case["iv"]) != 24:
                            tally.skip(part, "about the tag, or of another nonce's length")
                            continue
                        want(part, title, case["ct"], "chacha", case["key"], case["iv"], case["msg"])
                        continue
                    if kind == "KMAC":
                        count = group["tagSize"] // 8
                        want(part, title, lambda answer, tag=case["tag"], valid=valid: (answer == tag) == valid, "kmac", data["algorithm"][4:], case["key"], case["msg"], "", count)
                        continue
                    hash_name = names(data["algorithm"][4:] if kind.startswith("H") else data["algorithm"].split("-", 1)[1])
                    if hash_name is None:
                        tally.skip(part, "a hash the library has not")
                    elif kind == "HKDF":
                        expected = case["okm"] if valid else "error"
                        want(part, title, expected, "hkdf", hash_name, case["ikm"], case["salt"], case["info"], case["size"])
                    elif kind == "PBKDF":
                        want(part, title, case["dk"] if valid else "error", "pbkdf2", hash_name, case["password"], case["salt"], case["iterationCount"], case["dkLen"])
                    else:
                        tag = case["tag"]
                        count = group["tagSize"] // 4
                        want(part, title, lambda answer, tag=tag, valid=valid, count=count: (answer[:count] == tag) == valid, "hmac", hash_name, case["key"], case["msg"])
                        want(f"{part} isValid", title, "valid" if valid and count == len_of(hash_name) else "invalid", "isvalid", hash_name, case["key"], case["msg"], tag)


def len_of(hash_name):
    """A digest's length in hexadecimal digits."""
    return 40 if hash_name == "SHA1" else int(hash_name[-3:]) // 4


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("data", type=pathlib.Path)
    parser.add_argument("--large", type=int, default=1)
    arguments = parser.parse_args()
    cavp(arguments.data / "cavp")
    acvp(arguments.data / "acvp", arguments.large)
    wycheproof(arguments.data / "wycheproof")
    answers = ask(build(pathlib.Path(__file__).resolve().parent / "answer.sb"), [request for _, _, _, request in asked])
    for (part, name, expected, request), answer in zip(asked, answers):
        passed = expected(answer) if callable(expected) else answer == expected.lower()
        tally.check(part, passed, f"{name}: {request[:120]} -> {answer[:120]}" + ("" if callable(expected) else f", not {expected[:120]}"))
    tally.finish()


main()

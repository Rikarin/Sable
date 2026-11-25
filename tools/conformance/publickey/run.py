#!/usr/bin/env python3
"""Sable.Crypto's public-key algorithms against Wycheproof: signatures, key
agreement and the formats of keys.

    python3 -I tools/conformance/publickey/run.py data/wycheproof
"""

import argparse
import json
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent))
import common

CURVES = {"secp256r1", "secp384r1", "secp521r1", "P-256", "P-384", "P-521"}
NAMES = {"P-256": "secp256r1", "P-384": "secp384r1", "P-521": "secp521r1"}
HASHES = {"SHA-1", "SHA-256", "SHA-384", "SHA-512", "SHA3-256", "SHA3-384", "SHA3-512"}

# What the library has nothing for; the symmetric ones are crypto/run.py's.
OTHERS = {
    "DsaVerify": "DSA", "DsaP1363Verify": "DSA", "EcdsaBitcoinVerify": "secp256k1", "RsaesOaepDecrypt": "RSA encryption",
    "RsaesPkcs1Decrypt": "RSA encryption", "JsonWebSignature": "JWS, of which the library has only JWK", "JsonWebKey": "JWK sets and JWS",
    "JsonWebCrypto": "JWS and JWE", "JsonWebEncryption": "JWE", "HpkeTest": "HPKE", "HpkeEncapTest": "HPKE", "HpkeSequenceTest": "HPKE",
    "EddsaToXdh": "Ed25519 keys made X25519 ones", "EcCurveTest": "curves by their parameters",
    "BlsSigVerify": "BLS", "BlsHashToG2": "BLS", "BlsAggregateVerify": "BLS", "RistrettoScalarTest": "Ristretto",
    "PrimalityTest": "a primality test of its own, which keys' making is not", "MlDsaVerify": "#124", "MlDsaSign": "#124",
    "MLKEMEncapsTest": "#124", "MLKEMTest": "#124", "MLKEMKeyGen": "#124", "MLKEMDecapsValidationTest": "#124",
}

tally = common.Tally()
asked = []


def word(value):
    if isinstance(value, (dict, list)):
        value = json.dumps(value)
    return value.encode().hex() or "-"


def ask(part, line, check, describe):
    asked.append((part, line, check, describe))


def expect(part, line, test, describe):
    """Valid signatures are valid and invalid ones not; what is 'acceptable'
    may be either."""
    result = test["result"]

    def check(answer):
        if answer not in ("valid", "invalid", "error"):
            return False
        return result == "acceptable" or (answer == "valid") == (result == "valid")

    ask(part, line, check, f"{describe}: {result}: {test['comment']} {test['flags']}")


def agree(part, line, test, describe):
    """A valid agreement gives the secret, an invalid one nothing; an
    'acceptable' one the secret or nothing."""
    result, shared = test["result"], test["shared"] or "-"

    def check(answer):
        if result == "valid":
            return answer == shared
        return answer == "error" or (result == "acceptable" and answer == shared)

    ask(part, line, check, f"{describe}: {result}: {test['comment']} {test['flags']}")


def group(file, kind, group):
    curve = group.get("curve") or (group.get("publicKey") or {}).get("curve") if isinstance(group.get("publicKey"), dict) else group.get("curve")
    sha = group.get("sha")
    tests = group["tests"]
    part = kind
    if kind in ("EcdsaVerify", "EcdsaP1363Verify"):
        if curve not in CURVES:
            return skip(part, tests, "of another curve")
        if sha not in HASHES | {"SHAKE128", "SHAKE256"}:
            return skip(part, tests, f"of {sha}, which the library has not")
        formats(group)
        form = "der" if kind == "EcdsaVerify" else "p1363"
        for test in tests:
            expect(part, f"ecdsa {form} {group['publicKeyDer']} {sha} {test['msg'] or '-'} {test['sig'] or '-'}", test, f"{file} {test['tcId']}")
    elif kind in ("EcdhTest", "EcdhEcpointTest", "EcdhPemTest", "EcdhWebcryptoTest"):
        if curve not in CURVES:
            return skip(part, tests, "of another curve")
        form = {"EcdhTest": "spki", "EcdhEcpointTest": "point", "EcdhPemTest": "pem", "EcdhWebcryptoTest": "jwk"}[kind]
        for test in tests:
            public, private = (test["public"], test["private"]) if form in ("spki", "point") else (word(test["public"]), word(test["private"]))
            agree(part, f"ecdh {form} {NAMES.get(curve, curve)} {public or '-'} {private or '-'}", test, f"{file} {test['tcId']}")
    elif kind in ("XdhComp", "XdhAsnComp", "XdhPemComp", "XdhJwkComp"):
        if curve != "curve25519":
            return skip(part, tests, "of X448")
        form = {"XdhComp": "raw", "XdhAsnComp": "spki", "XdhPemComp": "pem", "XdhJwkComp": "jwk"}[kind]
        for test in tests:
            public, private = (test["public"], test["private"]) if form in ("raw", "spki") else (word(test["public"]), word(test["private"]))
            agree(part, f"x25519 {form} {public or '-'} {private or '-'}", test, f"{file} {test['tcId']}")
    elif kind == "EddsaVerify":
        if group["publicKey"]["curve"] != "edwards25519":
            return skip(part, tests, "of Ed448")
        formats(group)
        for test in tests:
            expect(part, f"ed25519 {group['publicKey']['pk']} {test['msg'] or '-'} {test['sig'] or '-'}", test, f"{file} {test['tcId']}")
    elif kind in ("RsassaPkcs1Verify", "RsassaPssVerify", "RsassaPssWithParametersVerify"):
        pss = kind != "RsassaPkcs1Verify"
        mask = group.get("mgfSha", sha) if pss else sha
        if sha not in HASHES or mask not in HASHES:
            return skip(part, tests, f"of {sha}" + (f" and {mask or group.get('mgf')}" if mask != sha else "") + ", which the library has not")
        if int(group["keySize"]) < 1024:
            return skip(part, tests, "of a key under 1024 bits")
        if kind != "RsassaPssWithParametersVerify":
            formats(group)
        key = group["publicKey"]
        for test in tests:
            expect(part, f"rsa {'pss' if pss else 'pkcs1'} {sha} {mask} {group.get('sLen', 0)} {key['modulus']} {key['publicExponent']} {test['msg'] or '-'} {test['sig'] or '-'}", test, f"{file} {test['tcId']}")
    elif kind == "RsassaPkcs1Generate":
        if sha not in HASHES:
            return skip(part, tests, f"of {sha}, which the library has not")
        for test in tests:
            signature = test["sig"]
            ask(part, f"rsasign {sha} {group['privateKeyPkcs8']} {test['msg'] or '-'}", lambda answer, signature=signature, result=test["result"]: answer == signature or (answer == "error" and result == "acceptable"), f"{file} {test['tcId']}: {test['comment']} {test['flags']}")
    elif kind in OTHERS:
        skip(kind, tests, OTHERS[kind])


def skip(part, tests, reason):
    for _ in tests:
        tally.skip(part, reason)


def formats(group):
    jwk = group.get("publicKeyJwk")
    ask("the groups' keys, in each format", f"formats {group['publicKeyDer']} {word(group['publicKeyPem'])} {word(jwk) if jwk else '-'}", lambda answer: answer == "same", group["publicKeyDer"])


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("data", type=pathlib.Path)
    arguments = parser.parse_args()
    for file in sorted(arguments.data.rglob("testvectors_v1/*.json")):
        data = json.loads(file.read_text())
        for each in data["testGroups"]:
            group(file.name, each.get("type"), each)
    answers = common.ask(common.build(HERE / "answer.sb"), [line for _, line, _, _ in asked])
    for (part, line, check, describe), answer in zip(asked, answers):
        tally.check(part, check(answer), f"{describe}: answered {answer}")
    tally.finish()


main()

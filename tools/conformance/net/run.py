#!/usr/bin/env python3
"""Sable.Net's classes of addresses, and its schemes' ports, against IANA's
registries.

    python3 -I tools/conformance/net/run.py <data>

The first address of every block, its last, one in its middle and the ones
before and after it are asked of. What the most specific block that has an
address says is what it is; where the registry says nothing, or N/A, the
library's documentation does."""

import csv
import ipaddress
import pathlib
import sys

sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1]))
from common import Tally, ask, build

# The library's classes, by the registries' names for the blocks.
KINDS = {
    "Loopback": "loopback", "Loopback Address": "loopback",
    "Unspecified Address": "unspecified",
    "Private-Use": "private", "Unique-Local": "private",
    "Link Local": "link-local", "Link-Local Unicast": "link-local",
    "Shared Address Space": "shared",
    "Documentation (TEST-NET-1)": "documentation", "Documentation (TEST-NET-2)": "documentation",
    "Documentation (TEST-NET-3)": "documentation", "Documentation": "documentation",
    "Benchmarking": "benchmarking",
    "Reserved": "reserved", "Limited Broadcast": "broadcast",
}


def blocks(path):
    with open(path, newline="", encoding="utf-8") as file:
        for row in csv.DictReader(file):
            for block in row["Address Block"].split(","):
                yield ipaddress.ip_network(block.split("[")[0].strip()), row["Name"].strip('"'), row["Globally Reachable"].split("[")[0].strip()


def main():
    data = pathlib.Path(sys.argv[1])
    tally = Tally()
    asked = []
    for version, space in ((4, ipaddress.ip_network("224.0.0.0/4")), (6, ipaddress.ip_network("ff00::/8"))):
        rows = list(blocks(data / f"iana-ipv{version}-special-registry-1.csv"))
        top = (1 << (32 if version == 4 else 128)) - 1
        make = ipaddress.IPv4Address if version == 4 else ipaddress.IPv6Address
        for network in [row[0] for row in rows] + [space]:
            first, last = int(network[0]), int(network[-1])
            for number in sorted({first - 1, first, (first + last) // 2, last, last + 1}):
                if 0 <= number <= top:
                    asked.append((version, rows, space, make(number)))
    answers = ask(build(pathlib.Path(__file__).resolve().parent / "answer.sb"), [str(address) for _, _, _, address in asked] + ["port " + scheme for scheme in ("ftp", "http", "https", "ws", "wss")])
    for (version, rows, space, address), answer in zip(asked, answers):
        got = set(answer.split())
        part = f"IPv{version}"
        # The library looks at the IPv4 address in a mapped one, and in NAT64's.
        if version == 6 and (address.ipv4_mapped or address in ipaddress.ip_network("64:ff9b::/96")):
            tally.skip(part, "of an IPv4 address in an IPv6 one, which is asked of instead")
            continue
        has = sorted((row for row in rows if address in row[0]), key=lambda row: row[0].prefixlen)
        kinds = {KINDS[name] for _, name, _ in has if name in KINDS}
        if "broadcast" in kinds:
            kinds.discard("reserved")
        if str(address) == "0.0.0.0":
            kinds.add("unspecified")
        if address in space:
            kinds.add("multicast")
        elif has:
            # N/A, and the blank of a block withdrawn, is no.
            if has[-1][2] == "True":
                kinds.add("global")
        elif version == 4 or address in ipaddress.ip_network("2000::/3"):
            kinds.add("global")
        tally.check(part, got == kinds, f"{address} {has[-1][1] if has else ''}: {sorted(got)}, not {sorted(kinds)}")
    ports = {}
    with open(data / "service-names-port-numbers.csv", newline="", encoding="utf-8") as file:
        for row in csv.DictReader(file):
            if row["Transport Protocol"] == "tcp" and row["Port Number"].isdigit():
                ports.setdefault(row["Service Name"], row["Port Number"])
    for scheme, service, answer in zip(("ftp", "http", "https", "ws", "wss"), ("ftp", "http", "https", "http", "https"), answers[len(asked):]):
        tally.check("ports", answer == ports[service], f"{scheme}: {answer}, not {ports[service]}")
    tally.finish()


main()

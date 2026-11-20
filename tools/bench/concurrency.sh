#!/usr/bin/env bash
# The concurrency benchmarks (benchmarks/concurrency/*.sb): each built once
# and run with no pool (SABLE_WORKERS=0, stage 2's single executor on the
# main thread), with one worker, and with one per core. Prints what each
# program says it measured; docs/04 ("Async, as implemented") records them.
#
#   tools/bench/concurrency.sh            # every benchmark
#   tools/bench/concurrency.sh ping-pong  # some
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
sable="${SABLE:-}"
if [[ -z "$sable" ]]; then
    dotnet build "$root/src/Sable.Driver" -c Release -m:2 >/dev/null
    sable="$root/src/Sable.Driver/bin/Release/net11.0/sable"
fi

out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

names=("$@")
if [[ ${#names[@]} -eq 0 ]]; then
    for file in "$root"/benchmarks/concurrency/*.sb; do
        names+=("$(basename "$file" .sb)")
    done
fi

cores="$(getconf _NPROCESSORS_ONLN)"
for name in "${names[@]}"; do
    "$sable" build "$root/benchmarks/concurrency/$name.sb" -o "$out/$name" >/dev/null
    for workers in 0 1 "$cores"; do
        SABLE_WORKERS="$workers" "$out/$name" | sed "s/^/$name, $workers workers: /"
    done
done

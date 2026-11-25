#!/usr/bin/env bash
# The concurrency benchmarks (benchmarks/concurrency): the harness's, as jobs
# with no pool (SABLE_WORKERS=0, stage 2's single executor on the main
# thread), with one worker and with one per core, each compared with the
# first; then the program 'sleepers' with each. docs/04 ("Async, as
# implemented") records them.
#
#   tools/bench/concurrency.sh                       # every benchmark
#   tools/bench/concurrency.sh PingPong --samples 5  # some, and 'sable bench' options
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
sable="${SABLE:-}"
if [[ -z "$sable" ]]; then
    dotnet build "$root/src/Sable.Driver" -c Release -m:2 >/dev/null
    sable="$root/src/Sable.Driver/bin/Release/net11.0/sable"
fi

cores="$(getconf _NPROCESSORS_ONLN)"
"$sable" bench "$root/benchmarks/concurrency" --launches 1 \
    --job "0 workers=SABLE_WORKERS=0" --job "1 worker=SABLE_WORKERS=1" --job "$cores workers=SABLE_WORKERS=$cores" "$@"

if [[ $# -eq 0 ]]; then
    out="$(mktemp -d)"
    trap 'rm -rf "$out"' EXIT
    "$sable" build "$root/benchmarks/concurrency/sleepers/sleepers.sb" -o "$out/sleepers" >/dev/null
    for workers in 0 1 "$cores"; do
        SABLE_WORKERS="$workers" "$out/sleepers" | sed "s/^/sleepers, $workers workers: /"
    done
fi

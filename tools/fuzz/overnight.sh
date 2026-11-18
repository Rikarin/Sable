#!/usr/bin/env bash
# Runs every fuzzer until a deadline, collecting every failure it finds.
#
# Each fuzzer stops at its first failure, so one long run reports at most
# one bug per fuzzer. This script runs them in rounds instead, each round
# from a new seed, and keeps going after a failure: by morning there is a
# failure for every distinct bug the night found, each with the seed that
# reproduces it.
#
#     tools/fuzz/overnight.sh                 # 8 hours, all four fuzzers
#     tools/fuzz/overnight.sh 2h parser compiler
#
# The fuzzers, which run side by side:
#
#     parser     random token soup through the parser: text must round-trip
#     compiler   mutated programs through binding, lowering and the verifier
#     resolver   random registries through the package resolver
#     frombytes  @inline_layout's fromBytes under AddressSanitizer and UBSan
#
# The duration takes s, m or h. Results go to artifacts/fuzz/<start time>/:
#
#     summary.txt         one line per distinct failure, with the seeds that hit it
#     <fuzzer>/<seed>.log the whole output of each failing round
#     <fuzzer>/<seed>/    the input a compiler round was on when it failed
#
# A failure reproduces with its seed: SABLE_FUZZ_SEED=<seed> and the same
# SABLE_FUZZ_SCALE (or SABLE_FUZZ_CASES) as the log's first line says.
#
# Round sizes are set so a round takes about five minutes; change them with
# PARSER_SCALE, COMPILER_SCALE, RESOLVER_SCALE and FROMBYTES_CASES. Each test
# process is capped at FUZZ_HEAP (default 4 GB) of managed heap.
set -uo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"

duration=${1:-8h}
[ $# -gt 0 ] && shift
fuzzers=("$@")
[ ${#fuzzers[@]} -eq 0 ] && fuzzers=(parser compiler resolver frombytes)

case "$duration" in
    *h) seconds=$(( ${duration%h} * 3600 )) ;;
    *m) seconds=$(( ${duration%m} * 60 )) ;;
    *s) seconds=${duration%s} ;;
    *) seconds=$duration ;;
esac
deadline=$(( $(date +%s) + seconds ))

out="$root/artifacts/fuzz/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$out"
export DOTNET_GCHeapHardLimit=${FUZZ_HEAP:-0x100000000}

echo "fuzzing ${fuzzers[*]} for $duration; results in $out"

# Build once, in Release, so that every round only runs.
for project in Syntax SIL CodeGen; do
    if ! dotnet build "tests/Sable.$project.Tests" -c Release -v quiet -nologo > "$out/build-$project.log" 2>&1; then
        echo "error: tests/Sable.$project.Tests does not build; see $out/build-$project.log" >&2
        exit 1
    fi
done

# What makes two failures the same one: the first line that says what went
# wrong, with numbers, addresses and quoted text taken out.
signature() {
    grep -m1 -E -A1 'Error Message:|ERROR: AddressSanitizer|runtime error:|fatal error|does not write back|hang' "$1" \
        | grep -v -E '^\s*(Error Message:|--)\s*$' | head -1 \
        | sed -E 's/0x[0-9a-f]+/ADDR/g; s/[0-9]+/N/g; s/"[^"]*"/"…"/g; s/^[[:space:]]+//' \
        | cut -c1-200
}

# One fuzzer's rounds, until the deadline. A round that outlives the time
# left is stopped, and one that runs past the round limit is kept as a hang.
fuzz() {
    local name=$1
    local dir="$out/$name"
    local round=0
    mkdir -p "$dir"
    while [ "$(date +%s)" -lt "$deadline" ]; do
        round=$((round + 1))
        local seed=$(( (RANDOM * 32768 + RANDOM) % 2147483647 ))
        local left=$(( deadline - $(date +%s) ))
        local limit=$(( left < 1800 ? left : 1800 ))
        local log="$dir/$seed.log"
        local status
        case "$name" in
            parser)
                echo "SABLE_FUZZ_SEED=$seed SABLE_FUZZ_SCALE=${PARSER_SCALE:-1000}" > "$log"
                SABLE_FUZZ_SEED=$seed SABLE_FUZZ_SCALE=${PARSER_SCALE:-1000} timeout "$limit" \
                    dotnet test tests/Sable.Syntax.Tests -c Release --no-build \
                    --filter "FullyQualifiedName~Soup" --logger "console;verbosity=normal" >> "$log" 2>&1
                status=$? ;;
            compiler)
                echo "SABLE_FUZZ_SEED=$seed SABLE_FUZZ_SCALE=${COMPILER_SCALE:-60}" > "$log"
                SABLE_FUZZ_SEED=$seed SABLE_FUZZ_SCALE=${COMPILER_SCALE:-60} SABLE_FUZZ_CRASH_DIR="$dir/$seed" timeout "$limit" \
                    dotnet test tests/Sable.SIL.Tests -c Release --no-build \
                    --filter "FullyQualifiedName~CompilerFuzzTests" --logger "console;verbosity=normal" >> "$log" 2>&1
                status=$? ;;
            resolver)
                echo "SABLE_FUZZ_SEED=$seed SABLE_FUZZ_SCALE=${RESOLVER_SCALE:-300}" > "$log"
                SABLE_FUZZ_SEED=$seed SABLE_FUZZ_SCALE=${RESOLVER_SCALE:-300} timeout "$limit" \
                    dotnet test tests/Sable.CodeGen.Tests -c Release --no-build \
                    --filter "FullyQualifiedName~ResolverFuzzTests" --logger "console;verbosity=normal" >> "$log" 2>&1
                status=$? ;;
            frombytes)
                echo "SABLE_FUZZ_SEED=$seed SABLE_FUZZ_CASES=${FROMBYTES_CASES:-20000000}" > "$log"
                SABLE_FUZZ_SEED=$seed SABLE_FUZZ_CASES=${FROMBYTES_CASES:-20000000} timeout "$limit" \
                    tools/fuzz/run.sh >> "$log" 2>&1
                status=$? ;;
        esac

        if [ "$status" -eq 0 ]; then
            rm -f "$log"
            rm -rf "${dir:?}/$seed"
        elif [ "$status" -eq 124 ] && [ "$(date +%s)" -ge "$deadline" ]; then
            # Stopped by the deadline, not by a failure.
            rm -f "$log"
            rm -rf "${dir:?}/$seed"
            break
        else
            [ "$status" -eq 124 ] && echo "hang: the round ran past ${limit}s" >> "$log"
            local what
            what=$(signature "$log")
            echo "$name	$seed	${what:-exit $status (see the log)}" >> "$out/failures.tsv"
            echo "$(date +%H:%M) $name: seed $seed failed: ${what:-exit $status}"
        fi
    done
    echo "$(date +%H:%M) $name: done after $round rounds"
}

for name in "${fuzzers[@]}"; do
    fuzz "$name" &
done
wait

# One line per distinct failure, most frequent first, with its seeds.
if [ -s "$out/failures.tsv" ]; then
    awk -F'\t' '{ key = $1 "\t" $3; n[key]++; s[key] = s[key] (s[key] ? " " : "") $2 }
        END { for (k in n) { split(k, p, "\t"); printf "%4d  %-9s  %s  [seeds: %s]\n", n[k], p[1], p[2], s[k] } }' \
        "$out/failures.tsv" | sort -rn > "$out/summary.txt"
    echo
    echo "distinct failures:"
    cat "$out/summary.txt"
else
    echo "no failures" | tee "$out/summary.txt"
fi
echo "results: $out"

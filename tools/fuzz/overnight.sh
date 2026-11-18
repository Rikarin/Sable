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
#     tools/fuzz/overnight.sh --summary       # the latest run's results so far
#     tools/fuzz/overnight.sh --summary <dir> # a given run's
#
# Ctrl-C (or SIGTERM) stops a run early: the rounds in progress are stopped
# and dropped, and the results so far are summarised as at the deadline. A
# run started in the background (nohup, &) ignores Ctrl-C's SIGINT; stop it
# with 'kill <pid>', the pid it prints when it starts. --summary reads a run
# while it is still going, from another terminal.
#
# The fuzzers, which run side by side:
#
#     parser     random token soup through the parser: text must round-trip
#     compiler   mutated programs through binding, lowering and the verifier
#     resolver   random registries through the package resolver
#     frombytes  @inline_layout's fromBytes under AddressSanitizer and UBSan
#
# The duration takes s, m or h. Results go to artifacts/fuzz/<start time>/,
# laid out as tools/fuzz/coverage.sh lays out its own (tools/fuzz/common.sh):
#
#     summary.txt         one line per distinct failure, with the seeds that hit it
#     <fuzzer>/<seed>.log the whole output of each failing round
#     <fuzzer>/<seed>/    the input a compiler round was on when it failed
#
# A failure reproduces with its seed: SABLE_FUZZ_SEED=<seed> and the same
# SABLE_FUZZ_SCALE (or SABLE_FUZZ_CASES) as the log's first line says.
#
# The test-based fuzzers are built once, from the working tree as it is at
# the start; frombytes builds again each round (tools/fuzz/run.sh), so leave
# the checkout alone while a run goes.
#
# Round sizes are set so a round takes about five minutes; change them with
# PARSER_SCALE, COMPILER_SCALE, RESOLVER_SCALE and FROMBYTES_CASES. Each test
# process is capped at FUZZ_HEAP (default 4 GB) of managed heap.
set -uo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root"

. "$root/tools/fuzz/common.sh"

if [ "${1:-}" = "--summary" ]; then
    run=$(run_dir "${2:-}") || exit 1
    summarise "$run" seeds
    exit 0
fi

duration=${1:-8h}
[ $# -gt 0 ] && shift
fuzzers=("$@")
[ ${#fuzzers[@]} -eq 0 ] && fuzzers=(parser compiler resolver frombytes)

deadline=$(( $(date +%s) + $(seconds_of "$duration") ))

out="$root/artifacts/fuzz/$(date +%Y%m%d-%H%M%S)"
mkdir -p "$out"
export DOTNET_GCHeapHardLimit=${FUZZ_HEAP:-0x100000000}

echo "fuzzing ${fuzzers[*]} for $duration; results in $out"
echo "Ctrl-C (or 'kill $$') stops early and summarises; 'tools/fuzz/overnight.sh --summary' shows the results so far"

# Build once, in Release, so that every round only runs.
for project in Syntax SIL CodeGen; do
    if ! dotnet build "tests/Sable.$project.Tests" -c Release -v quiet -nologo > "$out/build-$project.log" 2>&1; then
        echo "error: tests/Sable.$project.Tests does not build; see $out/build-$project.log" >&2
        exit 1
    fi
done

stopped() { [ -e "$out/.stop" ]; }

# One fuzzer's rounds, until the deadline or a stop. A round that outlives
# the time left is stopped, and one that runs past the round limit is kept
# as a hang.
fuzz() {
    local name=$1
    local dir="$out/$name"
    local round=0
    mkdir -p "$dir"
    echo 0 > "$dir/rounds"
    while [ "$(date +%s)" -lt "$deadline" ] && ! stopped; do
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

        if stopped || { [ "$status" -eq 124 ] && [ "$(date +%s)" -ge "$deadline" ]; }; then
            # Cut short by a stop or the deadline: not a result either way.
            rm -f "$log"
            rm -rf "${dir:?}/$seed"
            break
        fi

        round=$((round + 1))
        echo "$round" > "$dir/rounds"
        if [ "$status" -eq 0 ]; then
            rm -f "$log"
            rm -rf "${dir:?}/$seed"
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

# On Ctrl-C or SIGTERM: say so, then stop every round in progress. A script
# without job control starts its background jobs ignoring SIGINT, and the
# test processes inherit that, so they are sent SIGTERM directly.
stop() {
    if stopped; then
        return
    fi
    touch "$out/.stop"
    echo
    echo "$(date +%H:%M) stopping: the rounds in progress are dropped"
    # What each fuzzer is running, not the fuzzer's own loop, which then
    # sees the stop and says how far it got.
    local job
    for job in $(jobs -rp); do
        kill -TERM $(descendants "$job") 2>/dev/null
    done
}
trap stop INT TERM

for name in "${fuzzers[@]}"; do
    fuzz "$name" &
done

# 'wait' returns early when a signal arrives; keep waiting until every
# fuzzer has finished.
while [ -n "$(jobs -rp)" ]; do
    wait
done
rm -f "$out/.stop"

echo
summarise "$out" seeds
exit 0

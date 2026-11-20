#!/usr/bin/env bash
# Coverage-guided fuzzing, for a duration, keeping every distinct failure.
#
# The other fuzzers (tools/fuzz/overnight.sh) mutate at random, or steer by
# what they can see from outside: syntax kinds, diagnostic codes, how far
# down the pipeline an input got. These see the code itself: the Sable
# assemblies are rewritten by SharpFuzz to record every branch they take,
# and an input that takes one no input has taken before is kept and mutated
# further. That is what gets a fuzzer to the corners a day of random inputs
# misses.
#
#     tools/fuzz/coverage.sh                    # 1 hour, every target
#     tools/fuzz/coverage.sh 4h parser frontend
#     tools/fuzz/coverage.sh --summary [<dir>]  # the latest run's results so far, or a given run's
#
# The targets, which run side by side (tools/fuzz/Sable.Fuzz/Targets.cs):
#
#     parser     parsed as a file, statement, expression and type, text and
#                tree must round-trip; edited, an incremental reparse must
#                equal a full one
#     editor     the language server's documents: a run of edits, as an
#                editor sends them by line and column, applied by the
#                incremental parser, must equal a full parse of the text
#     frontend   the whole front end, as CompilerFuzzTests holds it: never
#                throw, never hang, and SIL the verifier accepts for what
#                lowers with nothing to say, through the passes, async
#                lowering and the optimising passes, printing as SIL that
#                reads back as itself
#     sil        SIL as text: refused only with the parser's own error;
#                what reads prints as text that reads back as itself, and
#                what verifies still verifies after the passes
#     resolver   a registry as text through the package resolver, checked
#                against trying every assignment; and the version parsers
#     packages   a manifest, a lock file and a registry's index: refused
#                only with their own errors, and written back as they read
#     frombytes  tools/fuzz/inline_layout.sb's fromBytes under libFuzzer,
#                AddressSanitizer and UBSan (tools/fuzz/from_bytes.c)
#
# The engine is libFuzzer where there is one. For the .NET targets that is
# libfuzzer-dotnet, which runs the target in a process of its own: set
# LIBFUZZER_DOTNET to its path (the nightly workflow builds it with clang
# -fsanitize=fuzzer; .github/workflows/fuzz.yml). Without it, the targets run
# under Sable.Fuzz's own loop (CoverageLoop.cs), the same coverage map read
# in-process. frombytes needs a clang with libFuzzer's runtime: Linux's has
# it, Apple's does not, and there frombytes only checks that its seeds run
# clean under the sanitizers.
#
# Results go to artifacts/fuzz/<start time>/, as overnight.sh's do
# (tools/fuzz/common.sh):
#
#     summary.txt               one line per distinct failure, with the inputs that hit it
#     <target>/crashes/crash-*  each failing input as found (timeout-* for a hang), with
#                               .replay.txt, what replaying it says, and .min, the
#                               smallest input that fails the same way
#     <target>/run-<n>.log      each run of the engine's output
#
# A failure reproduces with its input: Sable.Fuzz <target> replay <file>,
# from <run>/bin (the instrumented build), or the minimised input pasted into
# a test. For frombytes, <run>/frombytes/replay <file>.
#
# The corpus — the inputs that found new coverage — persists between runs in
# artifacts/fuzz-corpus/<target> (FUZZ_CORPUS moves it), so each run starts
# where the last left off. The seeds are the repository's .sb files
# (tests/corpus, tests/programs, tests/negative, examples, lib), the package
# tests' manifests, and hand-written registries and records; the
# dictionaries come from the lexer's own tables.
#
# A target's engine stops at a hang (and libFuzzer at any failure); the
# script starts it again, from the corpus, until the deadline. The deadline
# is held from outside as well: libFuzzer checks -max_total_time only
# between inputs, and only once it has run its whole corpus, so an engine
# still running FUZZ_GRACE (default 2m) past the deadline is killed. Its
# input on the go, if it had been running longer than the target's limit,
# is kept as a timeout; if not, the overrun is itself the failure. Replaying
# and minimising what was found stops FUZZ_TRIAGE (default 10m) after that,
# so that the summary is always written in good time. Ctrl-C (or
# SIGTERM) stops a run early, as overnight.sh's: what is in progress is
# dropped and the results so far are summarised. Each .NET process is capped
# at FUZZ_HEAP (default 4 GB) of managed heap; an input that needs more is a
# failure (OutOfMemoryException).
set -uo pipefail

root=$(cd "$(dirname "$0")/../.." && pwd)
cd "$root" || exit 1
. "$root/tools/fuzz/common.sh"

if [ "${1:-}" = "--summary" ]; then
    run=$(run_dir "${2:-}") || exit 1
    summarise "$run" inputs
    exit 0
fi

duration=${1:-1h}
[ $# -gt 0 ] && shift
targets=("$@")
[ ${#targets[@]} -eq 0 ] && targets=(parser editor frontend sil resolver packages interface frombytes)
for target in "${targets[@]}"; do
    case "$target" in
        parser|editor|frontend|sil|resolver|packages|interface|frombytes) ;;
        *) echo "error: no target '$target'; the targets are parser, editor, frontend, sil, resolver, packages, interface and frombytes" >&2; exit 2 ;;
    esac
done
deadline=$(( $(date +%s) + $(seconds_of "$duration") ))
grace=$(seconds_of "${FUZZ_GRACE:-2m}")
triage_until=$(( deadline + grace + $(seconds_of "${FUZZ_TRIAGE:-10m}") ))

out="$root/artifacts/fuzz/$(date +%Y%m%d-%H%M%S)"
corpora=${FUZZ_CORPUS:-$root/artifacts/fuzz-corpus}
mkdir -p "$out" "$corpora"
export DOTNET_GCHeapHardLimit=${FUZZ_HEAP:-0x100000000}
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
bin="$out/bin"

echo "coverage-guided fuzzing ${targets[*]} for $duration; results in $out"
echo "Ctrl-C (or 'kill $$') stops early and summarises; 'tools/fuzz/coverage.sh --summary' shows the results so far"

# ---- Building ---------------------------------------------------------------

needs_dotnet=0
for target in "${targets[@]}"; do
    [ "$target" != frombytes ] && needs_dotnet=1
done

if [ "$needs_dotnet" -eq 1 ]; then
    if ! dotnet publish tools/fuzz/Sable.Fuzz -c Release -o "$bin" -v quiet -nologo > "$out/build.log" 2>&1; then
        echo "error: tools/fuzz/Sable.Fuzz does not build; see $out/build.log" >&2
        exit 1
    fi

    # SharpFuzz's command-line tool rewrites the Sable assemblies — not the
    # fuzzer's own, the tests' or .NET's — so that each branch they take is
    # counted in the coverage map. It targets an older .NET, hence the roll forward.
    sharpfuzz=${SHARPFUZZ:-}
    if [ -z "$sharpfuzz" ]; then
        sharpfuzz="$root/artifacts/fuzz-tools/sharpfuzz"
        if [ ! -x "$sharpfuzz" ] && ! dotnet tool install SharpFuzz.CommandLine --version 2.3.0 --tool-path "$root/artifacts/fuzz-tools" >> "$out/build.log" 2>&1; then
            echo "error: cannot install SharpFuzz.CommandLine; see $out/build.log" >&2
            exit 1
        fi
    fi
    # The language server for the editor target: its documents and their edits.
    for assembly in Sable.Syntax Sable.Symbols Sable.SIL Sable.LanguageServer; do
        DOTNET_ROLL_FORWARD=Major "$sharpfuzz" "$bin/$assembly.dll" >> "$out/build.log" 2>&1 || { echo "error: cannot instrument $assembly; see $out/build.log" >&2; exit 1; }
    done
    # Of the driver, only the package code: the rest is the command line.
    DOTNET_ROLL_FORWARD=Major "$sharpfuzz" "$bin/sable.dll" Sable.Driver.Packages >> "$out/build.log" 2>&1 \
        || { echo "error: cannot instrument the package code; see $out/build.log" >&2; exit 1; }
fi

# libFuzzer's runtime: whether this clang links -fsanitize=fuzzer.
has_libfuzzer() {
    local probe="$out/frombytes/probe"
    printf 'int LLVMFuzzerTestOneInput(const unsigned char *d, unsigned long n) { return 0; }\n' > "$probe.c"
    ${CC:-clang} -fsanitize=fuzzer "$probe.c" -o "$probe" > /dev/null 2>&1
}

fuzzer_engine=none
for target in "${targets[@]}"; do
    [ "$target" = frombytes ] || continue
    mkdir -p "$out/frombytes"
    # The compiler, as tools/fuzz/run.sh builds it, uninstrumented.
    if ! dotnet build src/Sable.Driver -c Release -o "$out/compiler" -v quiet -nologo >> "$out/build.log" 2>&1 \
        || ! "$out/compiler/sable" build tools/fuzz/inline_layout.sb --emit llvm-ir > "$out/frombytes/program.ll" 2>> "$out/build.log"; then
        echo "error: cannot compile tools/fuzz/inline_layout.sb; see $out/build.log" >&2
        exit 1
    fi
    # libFuzzer brings its own main, and from_bytes.c its own replay one, so
    # the program's is renamed out of the way: what C calls is the @export functions.
    sed 's/^define i32 @main(/define i32 @sable_fuzz_program_main(/' "$out/frombytes/program.ll" > "$out/frombytes/harness.ll"
    flags=(-O1 -g -fno-omit-frame-pointer -fno-sanitize-recover=all -Wno-override-module)
    sources=("$out/frombytes/harness.ll" runtime/sable_runtime.c tools/fuzz/from_bytes.c)
    if ! ${CC:-clang} "${flags[@]}" -fsanitize=address,undefined -DSABLE_FUZZ_STANDALONE "${sources[@]}" -o "$out/frombytes/replay" >> "$out/build.log" 2>&1; then
        echo "error: cannot build the fromBytes harness; see $out/build.log" >&2
        exit 1
    fi
    if has_libfuzzer; then
        ${CC:-clang} "${flags[@]}" -fsanitize=fuzzer,address,undefined "${sources[@]}" -o "$out/frombytes/fuzzer" >> "$out/build.log" 2>&1 \
            || { echo "error: cannot build the fromBytes fuzzer; see $out/build.log" >&2; exit 1; }
        fuzzer_engine=libfuzzer
    fi
done

# ---- Fuzzing ----------------------------------------------------------------

stopped() { [ -e "$out/.stop" ]; }

hash_of() { git hash-object "$1"; }

# Seconds a triage step may take: at most the given number, and none past
# the time triage has after the deadline. 0 or less when none is left, which
# means do not start ('timeout 0' would be no limit at all).
within() {
    local left=$(( triage_until - $(date +%s) ))
    echo $(( left < $1 ? left : $1 ))
}

# A number of seconds for 'timeout', never 0: a step started as time runs
# out gets a second, not for ever.
at_least_one() { echo $(( $1 < 1 ? 1 : $1 )); }

# What a failing input is, and the smallest one that fails the same way:
# replayed in a process of its own, minimised, and added to failures.tsv.
# One kept as timeout-* is a timeout, whatever replaying it says.
triage() {
    local name=$1 file=$2 limit=$3
    local replay="$file.replay.txt" what="" status budget
    if [ "$(within 600)" -le 0 ]; then
        # No time left: recorded, with the engine's notes beside it, not replayed.
        status=-1
        what="not replayed, out of time"
    elif [ "$name" = frombytes ]; then
        ASAN_OPTIONS=detect_leaks=0 timeout "$(at_least_one "$(within 60)")" "$out/frombytes/replay" "$file" > "$replay" 2>&1
        status=$?
        what=$(signature "$replay")
        budget=$(within 300)
        if [ "$fuzzer_engine" = libfuzzer ] && [ "$status" -ne 0 ] && [ "$status" -ne 124 ] && [ "$budget" -gt 0 ]; then
            ASAN_OPTIONS=detect_leaks=0 timeout "$budget" "$out/frombytes/fuzzer" -minimize_crash=1 -max_total_time=120 \
                -exact_artifact_path="$file.min" "$file" > "$file.min.log" 2>&1
        fi
    else
        timeout "$(at_least_one "$(within 600)")" "$bin/Sable.Fuzz" "$name" replay "$file" > "$replay" 2>&1
        status=$?
        # -a throughout: the replay ends with the input, and one with a NUL
        # in it makes grep call the whole file binary and print no line.
        what=$(grep -a -m1 '^signature: ' "$replay" | cut -c12-)
        if [ -z "$what" ] && grep -a -q 'Stack overflow' "$replay"; then
            # Not an exception: the process is gone. Name it by the frame that repeats.
            what="stack overflow: $(grep -a -m1 -E '^\s+at ' "$replay" | sed -E 's/^[[:space:]]+at //; s/\(.*//' | cut -c1-150)"
        fi
        budget=$(within 600)
        if [ "$status" -eq 1 ] && [ "$budget" -gt 0 ]; then
            timeout "$budget" "$bin/Sable.Fuzz" "$name" minimise "$file" "$file.min" 120 > "$file.min.log" 2>&1
        fi
    fi
    if [ -z "$what" ]; then
        if [ "$status" -eq 0 ]; then
            what="does not fail when replayed"
        elif [ "$status" -eq 124 ]; then
            what="no answer when replayed, in the time triage had left"
        else
            what="exit $status when replayed (see $(basename "$replay"))"
        fi
    fi
    case "$(basename "$file")" in
        timeout-*)
            case "$what" in
                timeout:*) ;;
                "does not fail when replayed") what="timeout: over ${limit}s under the engine, but not when replayed" ;;
                *) what="timeout: $what" ;;
            esac ;;
    esac
    echo "$name	$file	$what" >> "$out/failures.tsv"
    echo "$(date +%H:%M) $name: $(basename "$file"): $what"
}

# A target that could not be set up: its log shown, and the failure recorded.
setup_failed() {
    local name=$1 log=$2 why=$3 what
    what=$(signature "$log")
    case "$why" in
        [0-9]*) why="exit $why" ;;
    esac
    echo "cannot start: $why${what:+: $what}; see $log" > "$out/$name/about"
    echo "$name	$log	cannot start: ${what:-$why}" >> "$out/failures.tsv"
    echo "$(date +%H:%M) $name: $(cat "$out/$name/about")"
    cat "$log"
}

# One target, run by its engine until the deadline or a stop, started again
# whenever it stops early, at a hang or (libFuzzer) any failure.
fuzz() {
    local name=$1
    local dir="$out/$name"
    local corpus="$corpora/$name"
    local crashes="$dir/crashes"
    local round=0 executions=0 engine
    mkdir -p "$dir" "$corpus" "$crashes"
    echo 0 > "$dir/rounds"

    # A target that cannot even start — its seeds, dictionary or limits not
    # written, the harness dying as it starts — fuzzed nothing: a failure,
    # said and summarised, not a quiet zero rounds.
    local setup="$dir/setup.log" length limit
    if [ "$name" = frombytes ]; then
        engine=$fuzzer_engine
        length=4096 limit=10
        mkdir -p "$dir/seeds"
        "$out/frombytes/replay" seeds "$dir/seeds" > "$setup" 2>&1 || { setup_failed "$name" "$setup" $?; return; }
    else
        engine=${LIBFUZZER_DOTNET:+libfuzzer}
        engine=${engine:-in-process}
        read -r length limit < <("$bin/Sable.Fuzz" "$name" info 2> "$setup")
        [ -n "${limit:-}" ] || { setup_failed "$name" "$setup" "no limits from 'info'"; return; }
        "$bin/Sable.Fuzz" "$name" seeds "$dir/seeds" > "$setup" 2>&1 || { setup_failed "$name" "$setup" $?; return; }
        "$bin/Sable.Fuzz" "$name" dict "$dir/dict" > "$setup" 2>&1 || { setup_failed "$name" "$setup" $?; return; }
    fi

    if [ "$engine" = none ]; then
        # No libFuzzer here: the seeds under the sanitizers, as a check that the harness works.
        if ASAN_OPTIONS=detect_leaks=0 "$out/frombytes/replay" "$dir/seeds"/* > "$dir/run-0.log" 2>&1; then
            echo "not fuzzed: ${CC:-clang} has no libFuzzer runtime (Apple's has none); its $(find "$dir/seeds" -type f | wc -l | tr -d ' ') seeds run clean under ASan and UBSan" > "$dir/about"
        else
            echo "not fuzzed: no libFuzzer runtime; and its seeds fail under the sanitizers: see run-0.log" > "$dir/about"
            echo "$name	$dir/run-0.log	$(signature "$dir/run-0.log")" >> "$out/failures.tsv"
        fi
        echo "$(date +%H:%M) $name: $(cat "$dir/about")"
        return
    fi

    while [ "$(date +%s)" -lt "$deadline" ] && ! stopped; do
        local left=$(( deadline - $(date +%s) ))
        local log="$dir/run-$round.log"
        local before status ran
        before=$(ls "$crashes")
        # The engine is given the time left, and killed (its .NET process
        # with it: timeout signals its whole process group) if it is still
        # running FUZZ_GRACE past that.
        local hard=(timeout -k 10 $(( left + grace )))
        case "$engine" in
            libfuzzer)
                local command=()
                if [ "$name" = frombytes ]; then
                    command=(env ASAN_OPTIONS=detect_leaks=0 "$out/frombytes/fuzzer")
                else
                    command=(env SABLE_FUZZ_CURRENT="$crashes/.current" "$LIBFUZZER_DOTNET" --target_path="$bin/Sable.Fuzz" --target_arg="$name" -dict="$dir/dict")
                fi
                "${hard[@]}" "${command[@]}" -max_len="$length" -timeout="$limit" -rss_limit_mb=0 -max_total_time="$left" \
                    -artifact_prefix="$crashes/" -print_final_stats=1 "$corpus" "$dir/seeds" > "$log" 2>&1
                status=$?
                # Its final count, or, killed before it could give one, the last it printed.
                ran=$(grep -a -m1 'stat::number_of_executed_units' "$log" | awk '{ print $2 }')
                [ -n "$ran" ] || ran=$(grep -a -o -E '^#[0-9]+' "$log" | tail -1 | tr -d '#') ;;
            in-process)
                "${hard[@]}" "$bin/Sable.Fuzz" "$name" fuzz "$left" "$corpus" "$crashes" "$dir/seeds" > "$log" 2>&1
                status=$?
                ran=$(grep -a '^#execs: ' "$log" | tail -1 | awk '{ print $2 }') ;;
        esac
        executions=$(( executions + ${ran:-0} ))

        echo "$engine engine; $executions inputs run; corpus of $(find "$corpus" -type f | wc -l | tr -d ' ') in $corpus" > "$dir/about"
        if stopped; then
            # Cut short by a stop: what it was running is not a result.
            local path
            for path in "$crashes"/*; do
                [ -e "$path" ] || continue
                echo "$before" | grep -qxF "$(basename "$path")" || rm -f "$path"
            done
            rm -f "$crashes/.current"
            break
        fi

        round=$((round + 1))
        echo "$round" > "$dir/rounds"

        # Killed at the hard deadline: still running FUZZ_GRACE past the time it was given.
        local overran=0
        if { [ "$status" -eq 124 ] || [ "$status" -eq 137 ]; } && [ "$(date +%s)" -ge $(( deadline + grace )) ]; then
            overran=1
        fi

        # The input on the go when the engine ended. Under libFuzzer it is
        # only a record, as libFuzzer keeps what fails itself.
        local current="$crashes/.current" kept=""
        if [ -e "$current" ]; then
            local age=$(( $(date +%s) - $(modified "$current") ))
            if [ "$overran" -eq 1 ] && [ "$age" -ge "$limit" ]; then
                # Running past the target's limit and never stopped by the
                # engine: a hang it did not see, kept as libFuzzer keeps one.
                kept="$crashes/timeout-$(hash_of "$current")"
                mv "$current" "$kept"
                { echo "still running after ${age}s (the limit is ${limit}s) when the engine was killed, ${grace}s past the deadline; the end of its log:"; tail -20 "$log"; } > "$kept.txt"
            elif [ "$overran" -eq 1 ] || [ "$engine" = libfuzzer ]; then
                rm -f "$current"
            else
                # The in-process loop died with an input on the go: that input is the crash.
                kept="$crashes/crash-$(hash_of "$current")"
                mv "$current" "$kept"
                { echo "the process died (exit $status) on this input; the end of its log:"; tail -20 "$log"; } > "$kept.txt"
            fi
        fi

        if [ "$overran" -eq 1 ] && [ -z "$kept" ]; then
            # No input to blame: the engine itself would not stop. libFuzzer
            # runs its whole corpus before it looks at the clock.
            local doing="fuzzing"
            if [ "$engine" = libfuzzer ] && ! grep -a -q 'INITED' "$log"; then
                doing="running its corpus of $(sed -n 's/.*seed corpus: files: \([0-9]*\).*/\1/p' "$log" | head -1) inputs, $ran of them run"
            fi
            echo "$name	$log	overran: the engine was still running ${grace}s past the deadline, and was killed" >> "$out/failures.tsv"
            echo "$(date +%H:%M) $name: the engine was still running ${grace}s past the deadline ($doing), and was killed; see $log"
        fi

        # What this run found: each failing input, not the notes kept beside it.
        local path
        for path in "$crashes"/crash-* "$crashes"/timeout-* "$crashes"/oom-*; do
            [ -e "$path" ] || continue
            case "$(basename "$path")" in
                *.*) continue ;;
            esac
            echo "$before" | grep -qxF "$(basename "$path")" || triage "$name" "$path" "$limit"
        done

        if [ "$status" -ne 0 ] && [ "$(ls "$crashes")" = "$before" ] && [ "$(date +%s)" -lt "$deadline" ]; then
            # Stopped early with nothing to show for it: say so, and do not spin.
            echo "$(date +%H:%M) $name: the engine stopped (exit $status) with no new failure; see $log"
            sleep 5
        fi
    done
    echo "$(date +%H:%M) $name: done after $round runs, $executions inputs"
}

# On Ctrl-C or SIGTERM: say so, then stop every engine. A script without job
# control starts its background jobs ignoring SIGINT, and what they run
# inherits that, so the engines are sent SIGTERM directly: what each target
# runs, which is 'timeout' with the engine under it (or a triage step), and
# 'timeout' passes the signal on.
stop() {
    if stopped; then
        return
    fi
    touch "$out/.stop"
    echo
    echo "$(date +%H:%M) stopping: the runs in progress are dropped"
    local job
    for job in $(jobs -rp); do
        kill -TERM $(pgrep -P "$job") 2>/dev/null
    done
}
trap stop INT TERM

for name in "${targets[@]}"; do
    fuzz "$name" &
done

# 'wait' returns early when a signal arrives; keep waiting until every
# target has finished.
while [ -n "$(jobs -rp)" ]; do
    wait
done
rm -f "$out/.stop"

echo
summarise "$out" inputs
exit 0

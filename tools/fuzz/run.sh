#!/bin/sh
# Fuzzes fromBytes (docs/04 §3.8): builds tools/fuzz/inline_layout.sb with
# AddressSanitizer and UndefinedBehaviorSanitizer over the runtime, and runs
# its ten million mutated inputs. Any read outside a block, any use after
# free, any undefined behaviour stops it with a report; a value accepted
# that does not write back as it was read stops it with a message.
#
#     tools/fuzz/run.sh
#
# SABLE_FUZZ_SEED and SABLE_FUZZ_CASES change where the mutations start and
# how many there are; a failure reproduces with the seed it printed.
#
# These mutations are random. 'sable fuzz' fuzzes the same fromBytes under
# libFuzzer instead, steered by coverage, as the nightly workflow does.
set -eu

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/sable-fuzz.XXXXXX")
trap 'rm -rf "$work"' EXIT

# Whatever stops it says why: a build that fails shows its output, and a
# fuzzer that exits other than cleanly, its status.
if ! dotnet build "$root/src/Sable.Driver" -c Release -o "$work/compiler" -v quiet -nologo > "$work/build.log" 2>&1; then
    cat "$work/build.log" >&2
    echo "error: the compiler does not build" >&2
    exit 1
fi
if ! "$work/compiler/sable" build "$root/tools/fuzz/inline_layout.sb" --emit llvm-ir > "$work/fuzz.ll"; then
    echo "error: tools/fuzz/inline_layout.sb does not compile" >&2
    exit 1
fi

if ! ${CC:-clang} -O1 -g -fno-omit-frame-pointer -fsanitize=address,undefined -fno-sanitize-recover=all \
    -Wno-override-module "$work/fuzz.ll" "$root/runtime/sable_runtime.c" -o "$work/fuzz"; then
    echo "error: the fuzzer does not build with ${CC:-clang}" >&2
    exit 1
fi

status=0
ASAN_OPTIONS=detect_leaks=0 SABLE_CHECK_LEAKS=1 "$work/fuzz" || status=$?
if [ "$status" -ne 0 ]; then
    echo "error: the fuzzer exited with status $status" >&2
    exit "$status"
fi

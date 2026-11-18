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
# These mutations are random. tools/fuzz/coverage.sh fuzzes the same
# fromBytes under libFuzzer instead, steered by coverage, through the
# program's @export entry points and tools/fuzz/from_bytes.c.
set -eu

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/sable-fuzz.XXXXXX")
trap 'rm -rf "$work"' EXIT

dotnet build "$root/src/Sable.Driver" -c Release -o "$work/compiler" -v quiet -nologo >/dev/null
"$work/compiler/sable" build "$root/tools/fuzz/inline_layout.sb" --emit llvm-ir > "$work/fuzz.ll"

${CC:-clang} -O1 -g -fno-omit-frame-pointer -fsanitize=address,undefined -fno-sanitize-recover=all \
    -Wno-override-module "$work/fuzz.ll" "$root/runtime/sable_runtime.c" -o "$work/fuzz"

ASAN_OPTIONS=detect_leaks=0 SABLE_CHECK_LEAKS=1 "$work/fuzz"

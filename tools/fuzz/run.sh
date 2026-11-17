#!/bin/sh
# Fuzzes fromBytes (docs/04 §3.8): builds tools/fuzz/inline_layout.sab with
# AddressSanitizer and UndefinedBehaviorSanitizer over the runtime, and runs
# its ten million mutated inputs. Any read outside a block, any use after
# free, any undefined behaviour stops it with a report; a value accepted
# that does not write back as it was read stops it with a message.
#
#     tools/fuzz/run.sh
set -eu

root=$(cd "$(dirname "$0")/../.." && pwd)
work=$(mktemp -d "${TMPDIR:-/tmp}/sable-fuzz.XXXXXX")
trap 'rm -rf "$work"' EXIT

dotnet build "$root/src/Sable.Driver" -c Release -o "$work/compiler" -v quiet -nologo >/dev/null
"$work/compiler/sable" build "$root/tools/fuzz/inline_layout.sab" --emit llvm-ir > "$work/fuzz.ll"

${CC:-clang} -O1 -g -fno-omit-frame-pointer -fsanitize=address,undefined -fno-sanitize-recover=all \
    -Wno-override-module "$work/fuzz.ll" "$root/runtime/sable_runtime.c" -o "$work/fuzz"

ASAN_OPTIONS=detect_leaks=0 SABLE_CHECK_LEAKS=1 "$work/fuzz"

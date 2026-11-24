#!/usr/bin/env bash
# Sable programs under ThreadSanitizer: the runtime compiled with
# -fsanitize=thread, and the program's own functions instrumented too (its
# IR's definitions marked sanitize_thread), linked and run with
# SABLE_CHECK_LEAKS set. Prints each program's reports' summaries; nothing
# but the program's own output means no race was seen.
#
#   tools/tsan/run.sh benchmarks/concurrency/*/*.sb examples/04-async-service.sb
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
sable="${SABLE:-}"
if [[ -z "$sable" ]]; then
    dotnet build "$root/src/Sable.Driver" -m:2 >/dev/null
    sable="$root/src/Sable.Driver/bin/Debug/net11.0/sable"
fi

cc="${CC:-clang}"
out="$(mktemp -d)"
trap 'rm -rf "$out"' EXIT

"$cc" -O1 -g -fsanitize=thread -c "$root/runtime/sable_runtime.c" -o "$out/runtime.o"
status=0
for program in "$@"; do
    name="$(basename "$program" .sb)"
    "$sable" build "$program" --emit llvm-ir > "$out/$name.ll"
    sed -E 's/^(define .*\)[^{]*) \{$/\1 sanitize_thread {/' "$out/$name.ll" > "$out/$name.tsan.ll"
    "$cc" -O1 -fsanitize=thread -Wno-override-module "$out/$name.tsan.ll" "$out/runtime.o" -o "$out/$name"
    echo "== $program"
    if ! SABLE_CHECK_LEAKS=1 "$out/$name" > "$out/$name.out" 2> "$out/$name.err"; then
        status=1
    fi

    grep -E "SUMMARY|leaked" "$out/$name.err" | sort | uniq -c || true
    if grep -q "SUMMARY" "$out/$name.err"; then
        status=1
    fi
done

exit $status

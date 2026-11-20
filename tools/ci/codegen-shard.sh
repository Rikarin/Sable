#!/usr/bin/env bash
# Splits Sable.CodeGen.Tests into shards by test class, for CI to run in
# parallel jobs, and prints what `dotnet test --filter` takes for one of them.
#
#   tools/ci/codegen-shard.sh <shard>             the filter for shard 1..N
#   tools/ci/codegen-shard.sh --classes <shard>   its classes, one a line
#   tools/ci/codegen-shard.sh --count             N
#
# A class's shard is the CRC (cksum) of its name, modulo N, plus one: stable,
# the same on every OS, and with no list to keep up to date, so every class
# lands in exactly one shard. The classes are those the project's files
# declare at their top level; ShardTests checks that every class with tests
# in the built assembly is among them, so a test class this cannot see fails
# the build rather than going unrun. N is also the matrix of the codegen job
# in .github/workflows/ci.yml, which ShardTests holds to it.
set -euo pipefail

count=4
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

classes() {
    # 'public sealed class XTests', 'public sealed partial class DocTests', ...
    sed -n -E 's/^public ((sealed|static|abstract|partial) )*class ([A-Za-z_][A-Za-z0-9_]*).*/\3/p' "$root"/tests/Sable.CodeGen.Tests/*.cs | sort -u
}

shard_of() {
    local crc
    crc=$(printf '%s' "$1" | cksum | cut -d' ' -f1)
    echo $((crc % count + 1))
}

in_shard() {
    local shard=$1 class
    for class in $(classes); do
        if [ "$(shard_of "$class")" = "$shard" ]; then
            echo "$class"
        fi
    done
}

case "${1:-}" in
    --count)
        echo "$count"
        ;;
    --classes)
        in_shard "${2:?usage: $0 --classes <shard>}"
        ;;
    [0-9]*)
        if [ "$1" -lt 1 ] || [ "$1" -gt "$count" ]; then
            echo "there are $count shards: 1 to $count" >&2
            exit 2
        fi

        # 'Name.' so that one class's name is never taken for the start of another's.
        in_shard "$1" | sed 's/.*/FullyQualifiedName~Sable.CodeGen.Tests.&./' | paste -sd '|' -
        ;;
    *)
        echo "usage: $0 <shard> | --classes <shard> | --count" >&2
        exit 2
        ;;
esac

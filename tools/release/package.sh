#!/usr/bin/env bash
# Packages a release of the toolchain for this machine (#182):
#
#     tools/release/package.sh <output-directory> [--targets "<triple> ..."]
#
# Publishes `sable` (Release) into <output-directory>, then prebuilds the host's
# build-cache entries into <output-directory>/cache — the C runtime's archive
# and the standard library's, alone and with each module, for the dev and
# release profiles — with `sable cache warm --into`. The compiler reads that
# directory as prebuilt, never writing it, so a first build on a fresh machine
# compiles only the program. The entries are keyed by this very build of the
# compiler, so they must be warmed by the `sable` they ship with: never copy
# a cache/ from another build.
#
# --targets warms other targets too (each needs its sysroot; see
# `sable build --help`). The directory is what `sable toolchain install
# <version> --from <directory>` installs, or what a release archive holds.
set -euo pipefail

if [[ $# -lt 1 ]]; then
    echo "usage: $0 <output-directory> [--targets \"<triple> ...\"]" >&2
    exit 2
fi

root=$(cd "$(dirname "$0")/../.." && pwd)
output=$1
shift
targets=""
if [[ ${1:-} == "--targets" ]]; then
    targets=$2
fi

rm -rf "$output"
dotnet publish "$root/src/Sable.Driver" -c Release -m:2 -o "$output" --nologo -v quiet

sable="$output/sable"
for profile in dev release; do
    "$sable" cache warm --profile "$profile" --into "$output/cache"
    for target in $targets; do
        "$sable" cache warm --target "$target" --profile "$profile" --into "$output/cache"
    done
done

echo "packaged $("$sable" --version) in $output"

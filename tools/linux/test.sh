#!/bin/sh
# Runs the whole test suite on Linux x64, in a container — docs/04 §2.5's
# "hello world links and runs on Linux x64", and everything else with it.
#
#     tools/linux/test.sh [extra dotnet test arguments]
#
# The repository is copied into the container rather than built in place, so
# that the host's bin/ and obj/ are left alone. On an arm64 host the x64
# container runs under emulation, which is slow but faithful.
set -eu

root=$(cd "$(dirname "$0")/../.." && pwd)
image=${SABLE_LINUX_IMAGE:-mcr.microsoft.com/dotnet/sdk:11.0}

docker run --rm --platform linux/amd64 \
    -v "$root":/source:ro \
    "$image" \
    sh -c '
        set -eu
        export DEBIAN_FRONTEND=noninteractive
        apt-get update -qq >/dev/null
        apt-get install -y -qq clang lldb >/dev/null 2>&1
        # The repository without its history or local tool state (worktrees
        # under .claude can be many times the size of the tree itself).
        # (tar cannot extract under emulation, so each entry is copied.)
        mkdir /work
        for entry in /source/* /source/.[!.]*; do
            case "${entry##*/}" in
                .git | .claude | ".[!.]*") ;;
                *) cp -r "$entry" /work/ ;;
            esac
        done
        find /work -type d \( -name bin -o -name obj \) -prune -exec rm -rf {} +
        cd /work
        uname -m
        clang --version | head -1
        dotnet test Sable.slnx '"$*"'
    '

#!/usr/bin/env bash
# Runs the standard library's own tests with coverage counted in the library,
# and holds each file of it to a threshold.
#
#   tools/stdlib-coverage.sh [Module...]      (default: every module in lib/)
#
# For each module, the tests in tests/lib/<Module>/ are built as one program
# and run with `sable test --coverage --cover lib/<Module>`: the counters go in
# the library's code, not the tests'. Each file of lib/<Module>/ with code is
# then printed with the share of its lines and branches the tests ran, and
# checked against tools/stdlib-coverage.thresholds. A module with no tests yet
# is measured all the same, with nothing run (a program that only imports it).
#
# Exits 1 if a test fails or a file is under its threshold. SABLE names the
# `sable` to use; without it, src/Sable.Driver is built (Debug) and its build
# used. The LCOV of each module is left in target/stdlib-coverage/, with
# report.tsv, a row a file, which tools/ci/coverage-report.py makes tables of:
# module, file, lines run, lines, branches run, branches, least lines, least
# branches, verdict (ok or UNDER), and a row of the module alone, its verdict
# FAILED, where its tests failed.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"
thresholds="tools/stdlib-coverage.thresholds"

if [[ -n "${SABLE:-}" ]]; then
    sable=("$SABLE")
else
    dotnet build src/Sable.Driver -c Debug -v quiet --nologo > /dev/null
    sable=(dotnet "$root/src/Sable.Driver/bin/Debug/net11.0/sable.dll")
fi

if [[ $# -gt 0 ]]; then
    modules=("$@")
else
    modules=()
    for directory in lib/*/; do
        modules+=("$(basename "$directory")")
    done
fi

out="target/stdlib-coverage"
mkdir -p "$out"
report="$out/report.tsv"
: > "$report"
scratch="$(mktemp -d)"
trap 'rm -rf "$scratch"' EXIT

failed=0
for module in "${modules[@]}"; do
    if [[ ! -d "lib/$module" ]]; then
        echo "stdlib-coverage: no module lib/$module" >&2
        exit 2
    fi

    tests="tests/lib/$module"
    if [[ ! -d "$tests" ]] || [[ -z "$(find "$tests" -name '*.sb' -print -quit)" ]]; then
        # Nothing tests it yet: a program that imports it, so that every file
        # of it is measured, and none run.
        tests="$scratch/$module"
        mkdir -p "$tests"
        printf 'module StdlibCoverageProbe\n\nimport %s\n' "$module" > "$tests/Probe.sb"
    fi

    lcov="$out/$module.info"
    rm -f "$lcov"
    echo "== $module ($tests)"
    log="$scratch/$module.log"
    if ! "${sable[@]}" test --cover "lib/$module" --lcov "$lcov" "$tests" > "$log" 2>&1; then
        cat "$log"
        echo "stdlib-coverage: $module: the tests failed" >&2
        printf '%s\t\t0\t0\t0\t0\t0\t0\tFAILED\n' "$module" >> "$report"
        failed=1
        continue
    fi

    if [[ ! -f "$lcov" ]]; then
        cat "$log"
        echo "stdlib-coverage: $module: no coverage was written" >&2
        printf '%s\t\t0\t0\t0\t0\t0\t0\tFAILED\n' "$module" >> "$report"
        failed=1
        continue
    fi

    # Each file's lines and branches, against the last line of the
    # thresholds that names it (a glob; '*' is the default).
    if ! awk -v thresholds="$thresholds" -v module="$module" -v report="$report" '
        function percent(part, whole) { return whole == 0 ? 100 : 100 * part / whole }
        BEGIN {
            count = 0; defaultLines = 85; defaultBranches = 70
            while ((getline line < thresholds) > 0) {
                reason = ""
                if (index(line, "#") > 0) {
                    reason = substr(line, index(line, "#") + 1)
                    line = substr(line, 1, index(line, "#") - 1)
                }
                n = split(line, field, " ")
                if (n == 0) continue
                if (n != 3) { print "stdlib-coverage: bad line in " thresholds ": " line > "/dev/stderr"; bad = 1; continue }
                if (field[1] != "*" && reason ~ /^[ \t]*$/) {
                    print "stdlib-coverage: " field[1] ": an override needs a reason (a # comment)" > "/dev/stderr"; bad = 1
                }
                count++
                pattern[count] = field[1]; minLines[count] = field[2]; minBranches[count] = field[3]
                if (field[1] == "*") { defaultLines = field[2]; defaultBranches = field[3] }
            }
        }
        function matches(file, glob,    regex) {
            regex = glob
            gsub(/[.+^$(){}|\[\]\\]/, "\\\\&", regex)
            gsub(/\*/, ".*", regex)
            gsub(/\?/, ".", regex)
            return file ~ ("^" regex "$")
        }
        /^SF:/ { file = substr($0, 4); lf = lh = brf = brh = 0; next }
        /^LF:/ { lf = substr($0, 4) + 0; next }
        /^LH:/ { lh = substr($0, 4) + 0; next }
        /^BRF:/ { brf = substr($0, 5) + 0; next }
        /^BRH:/ { brh = substr($0, 5) + 0; next }
        /^end_of_record/ {
            wantLines = defaultLines; wantBranches = defaultBranches; overridden = 0
            for (i = 1; i <= count; i++) {
                if (matches(file, pattern[i])) {
                    wantLines = minLines[i]; wantBranches = minBranches[i]; overridden = pattern[i] != "*"
                }
            }
            lines = percent(lh, lf); branches = percent(brh, brf)
            verdict = "ok"
            if (lines + 0.05 < wantLines || branches + 0.05 < wantBranches) { verdict = "UNDER"; under = 1 }
            else if (overridden && lines + 0.05 >= defaultLines && branches + 0.05 >= defaultBranches) { verdict = "ok (meets the default: drop its override)" }
            printf "%s\t%s\t%d\t%d\t%d\t%d\t%s\t%s\t%s\n", module, file, lh, lf, brh, brf, wantLines, wantBranches, (verdict == "UNDER" ? "UNDER" : "ok") >> report
            printf "  %-48s lines %5.1f%% (%d/%d)  branches %5.1f%% (%d/%d)  min %d/%d  %s\n", file, lines, lh, lf, branches, brh, brf, wantLines, wantBranches, verdict
        }
        END { exit (bad || under) ? 1 : 0 }
    ' "$lcov"; then
        failed=1
    fi
done

if [[ $failed -ne 0 ]]; then
    echo "stdlib-coverage: failed; see above (thresholds: $thresholds)" >&2
fi
exit $failed

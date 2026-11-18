# shellcheck shell=bash
# What tools/fuzz/overnight.sh and tools/fuzz/coverage.sh share: sourced by
# both, not run. A run's results are artifacts/fuzz/<start time>/, with
# failures.tsv (fuzzer, what reproduces it, signature: one line a failure)
# and <fuzzer>/rounds, summed up in summary.txt.

# Seconds in a duration given as <n>s, <n>m, <n>h or plain seconds.
seconds_of() {
    case "$1" in
        *h) echo $(( ${1%h} * 3600 )) ;;
        *m) echo $(( ${1%m} * 60 )) ;;
        *s) echo "${1%s}" ;;
        *) echo "$1" ;;
    esac
}

# The latest run under artifacts/fuzz, or the one given.
run_dir() {
    local run=${1:-$(ls -d "$root"/artifacts/fuzz/[0-9]*/ 2>/dev/null | sort | tail -1)}
    run=${run%/}
    if [ -z "$run" ] || [ ! -d "$run" ]; then
        echo "error: no fuzz run in artifacts/fuzz" >&2
        return 1
    fi
    echo "$run"
}

# The results of a run, finished or not: how far each fuzzer got, then one
# line per distinct failure, most frequent first, with what reproduces it
# (seeds for overnight.sh, input files for coverage.sh).
summarise() {
    local run=$1 label=$2
    {
        echo "run: $run"
        local rounds fuzzer
        for rounds in "$run"/*/rounds; do
            [ -e "$rounds" ] || continue
            fuzzer=$(dirname "$rounds")
            echo "  $(basename "$fuzzer"): $(cat "$rounds") rounds$( [ -s "$fuzzer/about" ] && echo "; $(cat "$fuzzer/about")")"
        done
        echo
        if [ -s "$run/failures.tsv" ]; then
            echo "distinct failures:"
            awk -F'\t' -v label="$label" '{ key = $1 "\t" $3; n[key]++; s[key] = s[key] (s[key] ? " " : "") $2 }
                END { for (k in n) { split(k, p, "\t"); printf "%4d  %-9s  %s  [%s: %s]\n", n[k], p[1], p[2], label, s[k] } }' \
                "$run/failures.tsv" | sort -rn
        else
            echo "no failures"
        fi
    } > "$run/summary.txt"
    cat "$run/summary.txt"
}

# What makes two failures the same one, from a log: the first line that says
# what went wrong, with numbers, addresses and quoted text taken out.
signature() {
    grep -m1 -E -A1 'Error Message:|ERROR: AddressSanitizer|runtime error:|fatal error|not write back|hang' "$1" \
        | grep -v -E '^\s*(Error Message:|--)\s*$' | head -1 \
        | sed -E 's/0x[0-9a-f]+/ADDR/g; s/[0-9]+/N/g; s/"[^"]*"/"…"/g; s/^[[:space:]]+//' \
        | cut -c1-200
}

# Every process under a pid, deepest first.
descendants() {
    local child
    for child in $(pgrep -P "$1"); do
        descendants "$child"
        echo "$child"
    done
}

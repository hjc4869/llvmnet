#!/usr/bin/env bash
set -euo pipefail
if [[ $# -eq 0 ]]; then printf 'Usage: %s results.tsv [results.tsv ...]\n' "$0" >&2; exit 2; fi
for results in "$@"; do
    if [[ ! -f "$results" ]]; then printf 'Missing result file: %s\n' "$results" >&2; exit 2; fi
done
awk -F '\t' '
    FNR == 1 {
        if ($1 != "suite" || $2 != "runtime" || $3 != "benchmark" || $7 != "result") {
            print "Invalid SPEC matrix header in " FILENAME > "/dev/stderr"
            invalid = 1
            exit 2
        }
        next
    }
    {
        if ((NF != 9 && NF != 11 && NF != 12) || ($2 != "system" && $2 != "portable") || ($7 != "PASS" && $7 != "FAIL" && $7 != "TIMEOUT") ||
            (NF >= 11 && ($10 !~ /^[1-9][0-9]*$/ || $11 !~ /^[1-9][0-9]*$/)) ||
            (NF == 12 && $12 !~ /^(serial|kit-openmp-v[12])$/)) {
            print "Invalid SPEC matrix row in " FILENAME > "/dev/stderr"
            invalid = 1
            exit 2
        }
        copies = NF == 9 ? 1 : $10
        threads = NF == 9 ? 1 : $11
        threading = NF == 12 ? $12 : (threads == 1 ? "serial" : "unspecified")
        key = $1 SUBSEP $3 SUBSEP $4 SUBSEP $5 SUBSEP $6 SUBSEP copies SUBSEP threads SUBSEP threading
        present[key] = 1
        result[key, $2] = $7
    }
    END {
        if (invalid) exit 2
        print "suite\tbenchmark\tsize\toptimization\tvectorize\tsystem\tportable\tcopies\tthreads\tthreading_profile"
        for (key in present) {
            split(key, fields, SUBSEP)
            systemResult = (key SUBSEP "system" in result) ? result[key, "system"] : "MISSING"
            portableResult = (key SUBSEP "portable" in result) ? result[key, "portable"] : "MISSING"
            printf "%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\t%s\n", fields[1], fields[2], fields[3], fields[4], fields[5], systemResult, portableResult, fields[6], fields[7], fields[8]
        }
    }
' "$@" | { IFS= read -r header; printf '%s\n' "$header"; LC_ALL=C sort; }
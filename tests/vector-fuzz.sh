#!/usr/bin/env bash
# tags: fuzz
# requires: python3
# timeout: 7200
# SV-04 in the fuzzing job: tests/vector-semantics.sh with VECTOR_FUZZ_SEEDS seeds from VECTOR_FUZZ_FIRST_SEED (default:
# 10 seeds from one derived from the date), each choosing other random lanes, reduction inputs and shuffle masks.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
first="${VECTOR_FUZZ_FIRST_SEED:-$(date -u +%Y%m%d)00}"
count="${VECTOR_FUZZ_SEEDS:-10}"
for ((seed = first; seed < first + count; seed++)); do
    VECTOR_SEMANTICS_SEED="$seed" bash "$root/tests/vector-semantics.sh" | tail -1
done
printf 'PASS: vector IR probes match native for %s seeds from %s\n' "$count" "$first"

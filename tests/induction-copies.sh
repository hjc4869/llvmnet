#!/usr/bin/env bash
# tags: core
# Induction variables in functions that share locals (PF-29): an induction variable's next value is computed from it
# (iv.next = iv + 1) and copied back into it on the back edge. Its last use is that computation, which reads it before
# the result is stored, so the two take one local and the back edge copies nothing, as a two-address add ties them
# natively. The generated loop counts up an index and down a counter around a body of 600 values, more than the 512
# above which a function shares locals. It must print what its native build prints in both modes, and its IL must have
# no load of one local stored straight into another: before PF-29 the index and the counter each had one per iteration.
source "$(dirname "${BASH_SOURCE[0]}")/lib.sh"
rm -rf "$test_output"
mkdir -p "$test_output"
source="$test_output/induction.ll"
awk 'BEGIN {
    count = 600
    print "target triple = \"x86_64-pc-linux-gnu\""
    print "@format = private constant [6 x i8] c\"%llu\\0A\\00\""
    print "declare i32 @printf(ptr, ...)"
    print "define i64 @counted(i64 %seed, i32 %trip) noinline {"
    print "entry:"
    print "  br label %loop"
    print "loop:"
    print "  %iv = phi i64 [ 1, %entry ], [ %iv.next, %loop ]"
    print "  %count = phi i32 [ %trip, %entry ], [ %count.next, %loop ]"
    print "  %acc = phi i64 [ %seed, %entry ], [ %acc.next, %loop ]"
    print "  %v0 = add i64 %acc, %iv"
    print "  %v1 = mul i64 %v0, -7046029254386353131"
    # Each value is used by the next two, so none is computed at its use instead of having a local.
    split("add xor sub xor", operations, " ")
    for (n = 2; n < count; n++)
        printf "  %%v%d = %s i64 %%v%d, %%v%d\n", n, operations[n % 4 + 1], n - 1, n - 2
    printf "  %%acc.next = add i64 %%v%d, %%v%d\n", count - 1, count - 2
    print "  %iv.next = add nuw nsw i64 %iv, 1"
    print "  %count.next = add nsw i32 %count, -1"
    print "  %more = icmp ne i32 %count.next, 0"
    print "  br i1 %more, label %loop, label %exit"
    print "exit:"
    print "  %total = add i64 %acc.next, %iv.next"
    print "  ret i64 %total"
    print "}"
    print "define i32 @main() {"
    print "  %result = call i64 @counted(i64 12345, i32 100000)"
    print "  %printed = call i32 (ptr, ...) @printf(ptr @format, i64 %result)"
    print "  ret i32 0"
    print "}"
}' > "$source"
clang-22 -O2 "$source" -o "$test_output/native"
"$test_output/native" > "$test_output/native.txt"
for runtime in system portable; do
    tag_ir "$runtime" "$source" "$test_output/induction-$runtime.bc"
    "$llvmnet" --runtime="$runtime" "$test_output/induction-$runtime.bc" -o "$test_output/induction-$runtime.dll"
    dotnet "$test_output/induction-$runtime.dll" > "$test_output/induction-$runtime.txt"
    diff -u "$test_output/native.txt" "$test_output/induction-$runtime.txt"
done
dotnet "$checks" il-opcodes "$test_output/induction-portable.dll" counted --locals > "$test_output/counted.il"
# A copy is a local loaded and stored into another with nothing in between; an edge loads all of its copies before it
# stores any, so pending loads are a stack that each store pops.
copies="$(tr ' ' '\n' < "$test_output/counted.il" | awk -F: '
    $1 == "ldloc" { loaded[++depth] = $2; next }
    $1 == "stloc" && depth > 0 { if ($2 != loaded[depth]) copies++; depth--; next }
    { depth = 0 }
    END { print copies + 0 }')"
if [[ "$copies" != 0 ]]; then
    printf 'FAIL: counted copies one local into another %s times; its index and counter should share their next values'"'"' locals\n' "$copies" >&2
    exit 1
fi
printf 'PASS: a counted loop of 600 values matches native in both modes, with no copies between its locals\n'

target datalayout = "e-m:e-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128"
target triple = "x86_64-unknown-linux-gnu"

; GEPs of constant indices computed at each of their uses (PF-23): field addresses of an argument computed in a loop
; head and used in the cases below and copied into a phi on their edges, struct and array indices added into one
; offset, a GEP of such a GEP (which keeps its local), a negative i32 index and offsets past 32 bits.
%state = type { i32, [3 x i64], { i8, i16 } }

@memory = global [8 x i64] zeroinitializer

define i64 @walk(ptr %state, i32 %count) {
entry:
  %far = getelementptr i8, ptr %state, i64 4294967304
  br label %head

head:
  %index = phi i32 [ 0, %entry ], [ %next, %latch ]
  %sum = phi i64 [ 0, %entry ], [ %total, %latch ]
  %first = getelementptr inbounds i8, ptr %state, i64 8
  %third = getelementptr inbounds %state, ptr %state, i64 0, i32 1, i64 2
  %half = getelementptr inbounds %state, ptr %state, i64 0, i32 2, i32 1
  %case = urem i32 %index, 3
  switch i32 %case, label %narrow [
    i32 0, label %increment
    i32 1, label %triple
  ]

increment:
  %a = load i64, ptr %first
  %a1 = add i64 %a, 1
  store i64 %a1, ptr %first
  br label %latch

triple:
  %b = load i64, ptr %third
  %b1 = mul i64 %b, 3
  store i64 %b1, ptr %third
  br label %latch

narrow:
  %c = load i16, ptr %half
  %c1 = add i16 %c, 5
  store i16 %c1, ptr %half
  %word = getelementptr inbounds i8, ptr %half, i64 -2
  br label %latch

latch:
  %read = phi ptr [ %first, %increment ], [ %third, %triple ], [ %word, %narrow ]
  %value = load i64, ptr %read
  %total = add i64 %sum, %value
  %next = add i32 %index, 1
  %done = icmp eq i32 %next, %count
  br i1 %done, label %exit, label %head

exit:
  %near = getelementptr i8, ptr %far, i64 -4294967296
  %last = load i64, ptr %near
  %end = getelementptr i64, ptr %state, i32 5
  %before = getelementptr i64, ptr %end, i32 -2
  %tripled = load i64, ptr %before
  %scaled = mul i64 %last, 1000000
  %high = mul i64 %tripled, 1000000000
  %partial = add i64 %total, %scaled
  %result = add i64 %partial, %high
  ret i64 %result
}

define i32 @main() {
entry:
  %third = getelementptr inbounds i8, ptr @memory, i64 24
  store i64 1, ptr %third
  %result = call i64 @walk(ptr @memory, i32 9)
  ; Nine passes: [8] counts to 3, [24] triples to 27, the i16 at 34 grows by 5 to 15 and the cases read 1, 3, 5 << 16,
  ; 2, 9, 10 << 16, 3, 27 and 15 << 16; then 3 and 27 are read back through the far and the negative offsets.
  %wrong = icmp ne i64 %result, 27004966125
  %status = zext i1 %wrong to i32
  ret i32 %status
}

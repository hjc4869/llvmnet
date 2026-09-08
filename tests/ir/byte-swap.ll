declare i48 @llvm.bswap.i48(i48)
declare i80 @llvm.bswap.i80(i80)
declare i128 @llvm.bswap.i128(i128)

define i32 @main() {
entry:
  %swap48 = call i48 @llvm.bswap.i48(i48 1)
  %high48 = shl i48 1, 40
  %check48 = icmp eq i48 %swap48, %high48
  %pattern48 = call i48 @llvm.bswap.i48(i48 123456789)
  %round48 = call i48 @llvm.bswap.i48(i48 %pattern48)
  %round48_ok = icmp eq i48 %round48, 123456789
  %swap80 = call i80 @llvm.bswap.i80(i80 1)
  %high80 = shl i80 1, 72
  %check80 = icmp eq i80 %swap80, %high80
  %pattern80 = or i80 %high80, 123456789
  %swapped80 = call i80 @llvm.bswap.i80(i80 %pattern80)
  %round80 = call i80 @llvm.bswap.i80(i80 %swapped80)
  %round80_ok = icmp eq i80 %round80, %pattern80
  %swap128 = call i128 @llvm.bswap.i128(i128 1)
  %high128 = shl i128 1, 120
  %check128 = icmp eq i128 %swap128, %high128
  %pattern128 = or i128 %high128, 987654321
  %swapped128 = call i128 @llvm.bswap.i128(i128 %pattern128)
  %round128 = call i128 @llvm.bswap.i128(i128 %swapped128)
  %round128_ok = icmp eq i128 %round128, %pattern128
  %ok48 = and i1 %check48, %round48_ok
  %ok80 = and i1 %check80, %round80_ok
  %ok128 = and i1 %check128, %round128_ok
  %ok_small = and i1 %ok48, %ok80
  %ok = and i1 %ok_small, %ok128
  %result = select i1 %ok, i32 0, i32 1
  ret i32 %result
}
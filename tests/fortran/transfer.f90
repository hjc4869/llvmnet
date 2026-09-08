program bit_transfer
  implicit none
  real(8), volatile :: value = 1d0
  integer(8) :: bits
  integer(4), volatile :: source(4) = [1,2,3,4]
  integer(4), allocatable :: copied(:)
  integer(8), allocatable :: wide(:)
  bits = transfer(value,bits)
  if (bits /= int(z'3ff0000000000000',8)) stop 1
  copied = transfer(source(4:1:-2),[0])
  if (size(copied) /= 2 .or. any(copied /= [4,2])) stop 2
  wide = transfer(source,[0_8])
  if (size(wide) /= 2 .or. wide(1) /= 8589934593_8 .or. wide(2) /= 17179869187_8) stop 3
  copied = transfer(source,0,2)
  if (size(copied) /= 2 .or. any(copied /= [1,2])) stop 4
  copied = transfer(source,0,0)
  if (size(copied) /= 0) stop 5
  print '(A)', 'TRANSFER bit patterns, strides, sizes and ranks passed'
end program
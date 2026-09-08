program real_remainders
  use, intrinsic :: ieee_arithmetic, only: ieee_is_negative
  implicit none
  real(8), volatile :: value, divisor
  real(4), volatile :: single_value, single_divisor
  value = -5.5d0
  divisor = 2d0
  if (mod(value, divisor) /= -1.5d0 .or. modulo(value, divisor) /= 0.5d0) stop 1
  value = 5.5d0
  divisor = -2d0
  if (mod(value, divisor) /= 1.5d0 .or. modulo(value, divisor) /= -0.5d0) stop 2
  value = -4d0
  divisor = 2d0
  if (.not. ieee_is_negative(mod(value, divisor))) stop 3
  if (modulo(value, divisor) /= 0 .or. ieee_is_negative(modulo(value, divisor))) stop 4
  divisor = -2d0
  if (.not. ieee_is_negative(modulo(value, divisor))) stop 5
  single_value = -5.5
  single_divisor = 2
  if (mod(single_value, single_divisor) /= -1.5 .or. modulo(single_value, single_divisor) /= 0.5) stop 6
  print '(A)', 'Fortran MOD and MODULO signs and signed zero passed'
end program
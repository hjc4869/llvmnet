program core
  use iso_c_binding
  implicit none
  interface
    function puts(text) bind(C, name="puts") result(code)
      import c_ptr, c_int
      type(c_ptr), value :: text
      integer(c_int) :: code
    end function
  end interface
  real(c_double) :: values(4, 5), total
  integer(c_int) :: row, column, code
  integer(c_int64_t) :: wide
  character(kind=c_char, len=80), target :: message
  do column = 1, 5
    do row = 1, 4
      values(row, column) = real(row + column, c_double) / 2.0_c_double
    end do
  end do
  total = sum(values)
  if (total /= 55.0_c_double) stop 1
  if (sum(values(1:4:2, 2:5:2)) /= 10.0_c_double) stop 5
  if (sum(values, mask=values > 2.0_c_double) /= 45.0_c_double) stop 6
  if (factorial(10_c_int64_t) /= 3628800_c_int64_t) stop 2
  wide = ishft(1_c_int64_t, 50)
  if (ishft(wide, -48) /= 4) stop 3
  if (abs(values(2, 3) - sqrt(6.25_c_double)) > 1.0e-14_c_double) stop 4
  message = "fortran: arrays, recursion, integer kinds and C interoperability" // c_null_char
  code = puts(c_loc(message))
contains
  recursive function factorial(number) result(value)
    integer(c_int64_t), intent(in) :: number
    integer(c_int64_t) :: value
    if (number <= 1) then
      value = 1
    else
      value = number * factorial(number - 1)
    end if
  end function
end program
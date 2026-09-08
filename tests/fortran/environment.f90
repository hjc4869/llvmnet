program environment_inquiry
  implicit none
  integer :: status, length
  character(32) :: name = 'LLVMNET_FORTRAN_VALUE'
  character(32) :: value
  character(3) :: short
  character(128) :: message = 'unchanged'
  call get_environment_variable(name, value, length, status, errmsg=message)
  if (status /= 0 .or. length /= 7 .or. value /= 'abc xyz' .or. message /= 'unchanged') stop 1
  call get_environment_variable(name, short, length, status, errmsg=message)
  if (status /= -1 .or. length /= 7 .or. short /= 'abc' .or. len_trim(message) == 0) stop 2
  call get_environment_variable(name, length=length, status=status)
  if (status /= 0 .or. length /= 7) stop 3
  call get_environment_variable(name, value, length, status, trim_name=.false.)
  if (status /= 1 .or. length /= 0 .or. value /= '') stop 4
  call get_environment_variable('LLVMNET_FORTRAN_MISSING', value, length, status, errmsg=message)
  if (status /= 1 .or. length /= 0 .or. value /= '' .or. len_trim(message) == 0) stop 5
  call get_environment_variable('LLVMNET_FORTRAN_EMPTY', value, length, status)
  if (status /= 0 .or. length /= 0 .or. value /= '') stop 6
  print '(A)', 'Fortran environment values, trimming, lengths and status passed'
end program
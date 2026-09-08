program unformatted_io
  implicit none
  integer :: unit, status, scalar, values(3), restored(3)
  real(8) :: real_value
  character(256) :: path
  character(5) :: text
  call get_command_argument(1, path)
  values = [7, 9, 11]
  open(newunit=unit, file=trim(path), status='replace', form='unformatted')
  write(unit) 42, 1.25d0, values
  write(unit) 'hello'
  rewind(unit)
  read(unit) scalar, real_value, restored
  if (scalar /= 42 .or. real_value /= 1.25d0 .or. any(restored /= values)) stop 1
  read(unit) text
  if (text /= 'hello') stop 2
  backspace(unit)
  read(unit) text
  if (text /= 'hello') stop 3
  read(unit, iostat=status) scalar
  if (status >= 0) stop 4
  backspace(unit)
  backspace(unit)
  read(unit) text
  if (text /= 'hello') stop 5
  rewind(unit)
  read(unit) scalar
  read(unit) text
  if (scalar /= 42 .or. text /= 'hello') stop 6
  rewind(unit)
  read(unit, iostat=status) scalar, real_value, restored, scalar
  if (status == 0) stop 7
  close(unit)
  print '(A)', 'unformatted records, arrays, backspace, EOF and short reads passed'
end program
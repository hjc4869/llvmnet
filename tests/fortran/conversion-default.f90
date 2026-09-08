program conversion_default
  implicit none
  integer :: unit, values(2) = [305419896, 7], copied(2)
  character(256) :: path
  call get_command_argument(1, path)
  open(newunit=unit, file=trim(path), status='replace', form='unformatted')
  write(unit) values
  rewind(unit)
  read(unit) copied
  close(unit)
  if (copied(1) /= values(1) .or. copied(2) /= values(2)) stop 1
  print '(A)', 'Fortran default conversion round trip passed'
end program
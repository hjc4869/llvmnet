program runtime_io
  use iso_fortran_env, only: real64
  implicit none
  real(real64), allocatable :: values(:, :)
  real(real64) :: parsed_real
  integer :: row, column, parsed_integer, unit, status, argument_length
  character(len=40) :: record
  allocate(values(0:2, 2:5), stat=status)
  if (status /= 0) stop 1
  do column = 2, 5
    do row = 0, 2
      values(row, column) = real(row + column, real64)
    end do
  end do
  if (sum(values) /= 54.0_real64) stop 2
  values(:, 3:5) = values(:, 2:4)
  if (sum(values) /= 45.0_real64) stop 7
  call get_command_argument(1, record, length=argument_length, status=status)
  if (status /= 0 .or. argument_length /= 7 .or. record(1:7) /= 'payload') stop 8
  write(record, '(I4,1X,F8.2)') 17, 2.5_real64
  read(record, *) parsed_integer, parsed_real
  if (parsed_integer /= 17 .or. parsed_real /= 2.5_real64) stop 3
  open(newunit=unit, file='record.dat', status='replace', action='readwrite', iostat=status)
  if (status /= 0) stop 4
  write(unit, '(A)') trim(record)
  rewind(unit)
  read(unit, *) parsed_integer, parsed_real
  if (parsed_integer /= 17 .or. parsed_real /= 2.5_real64) stop 5
  close(unit, status='delete')
  write(*, '(A,1X,I0,1X,F6.2)') 'fortran-io', size(values), sum(values)
  deallocate(values, stat=status)
  if (status /= 0) stop 6
end program
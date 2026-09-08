program direct_records
  implicit none
  integer :: unit, status, length, record, values(2), oversized(3), strided(4)
  character(256) :: path
  character(16) :: access, conversion
  call get_command_argument(1, path)
  call get_command_argument(2, conversion)
  inquire(iolength=length) values
  open(newunit=unit, file=trim(path), status='replace', access='direct', &
    form='unformatted', recl=length, convert=trim(conversion))
  inquire(unit=unit, access=access)
  if (access /= 'DIRECT') stop 1
  write(unit,rec=3) [5,6]
  write(unit,rec=1) [1,2]
  write(unit,rec=2) [3,4]
  read(unit,rec=3) values
  if (values(1) /= 5 .or. values(2) /= 6) stop 2
  read(unit,rec=1) values
  if (values(1) /= 1 .or. values(2) /= 2) stop 3
  strided = [19,-1,23,-1]
  write(unit,rec=2) strided(1:4:2)
  read(unit,rec=2) values(1), values(2)
  if (values(1) /= 19 .or. values(2) /= 23) stop 4
  read(unit,rec=4,iostat=status) values
  if (status == 0) stop 5
  record = 0
  read(unit,rec=record,iostat=status) values
  if (status == 0) stop 6
  read(unit,rec=1,iostat=status) oversized
  if (status == 0) stop 7
  strided = -1
  read(unit,rec=3) strided(2:4:2)
  if (any(strided /= [-1,5,-1,6])) stop 9
  write(unit,rec=1) 1, 2
  read(unit,iostat=status) values
  if (status == 0) stop 10
  read(unit,iostat=status)
  if (status == 0) stop 11
  rewind(unit,iostat=status)
  if (status == 0) stop 12
  backspace(unit,iostat=status)
  if (status == 0) stop 13
  endfile(unit,iostat=status)
  if (status == 0) stop 14
  close(unit)
  open(newunit=unit, status='scratch', form='unformatted', access='direct', recl=length)
  write(unit,rec=1) 41
  read(unit,rec=1) values(1)
  if (values(1) /= 41) stop 15
  write(unit,rec=1,iostat=status) [1,2,3]
  if (status == 0) stop 8
  close(unit)
  print '(A)', 'Fortran direct records: offsets, byte order, overwrites and bounds passed'
end program
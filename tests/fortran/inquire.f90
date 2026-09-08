program file_inquiry
  implicit none
  character(256) :: path, name
  character(16) :: form, access, action
  integer :: unit, status, number
  integer(8) :: size, position
  logical :: exists, opened, named
  call get_command_argument(1, path)
  inquire(file=trim(path), exist=exists, opened=opened, iostat=status)
  if (status /= 0 .or. exists .or. opened) stop 1
  open(newunit=unit, file=trim(path), status='new', access='stream', form='unformatted')
  write(unit) 'hello'
  flush(unit)
  inquire(file=trim(path), exist=exists, opened=opened, number=number, size=size, iostat=status)
  if (status /= 0 .or. .not. exists .or. .not. opened .or. number /= unit .or. size /= 5) stop 2
  inquire(unit=unit, named=named, name=name, form=form, access=access, action=action, pos=position, iostat=status)
  if (status /= 0 .or. .not. named .or. trim(name) /= trim(path) .or. position /= 6) stop 3
  if (trim(form) /= 'UNFORMATTED' .or. trim(access) /= 'STREAM' .or. trim(action) /= 'READWRITE') stop 4
  close(unit, status='delete')
  inquire(file=trim(path), exist=exists)
  if (exists) stop 5
  print '(A)', 'Fortran file and unit inquiry passed'
end program
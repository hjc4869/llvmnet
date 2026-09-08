program endfile_records
  implicit none
  integer :: unit, status, value
  character(256) :: path
  character(16) :: record
  call get_command_argument(1, path)
  open(newunit=unit, file=trim(path)//'.txt', status='replace', form='formatted')
  write(unit,'(A)') 'first', 'discard', 'last'
  rewind(unit)
  read(unit,'(A)') record
  endfile(unit)
  backspace(unit)
  write(unit,'(A)') 'replacement'
  endfile(unit)
  backspace(unit)
  backspace(unit)
  read(unit,'(A)') record
  if (record /= 'replacement') stop 1
  rewind(unit)
  read(unit,'(A)') record
  if (record /= 'first') stop 2
  read(unit,'(A)') record
  if (record /= 'replacement') stop 3
  read(unit,'(A)',iostat=status) record
  if (status /= -1) stop 4
  close(unit)
  open(newunit=unit, file=trim(path)//'.bin', status='replace', form='unformatted')
  write(unit) 19
  write(unit) 23
  rewind(unit)
  read(unit) value
  endfile(unit)
  backspace(unit)
  backspace(unit)
  read(unit) value
  if (value /= 19) stop 5
  read(unit,iostat=status) value
  if (status /= -1) stop 6
  close(unit)
  print '(A)', 'ENDFILE truncation, backspace and record boundaries passed'
end program
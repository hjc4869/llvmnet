program record_backspace
  implicit none
  integer :: unit, io_status
  character(16) :: record
  open(newunit=unit, file='backspace-records.dat', status='replace', action='readwrite')
  backspace(unit, iostat=io_status)
  if (io_status /= 0) stop 1
  write(unit, '(A)') 'first'
  write(unit, '(A)') ''
  write(unit, '(A)') 'third'
  write(unit, '(A)') 'last'
  rewind(unit)
  read(unit, '(A)') record
  if (record /= 'first') stop 2
  backspace(unit)
  read(unit, '(A)') record
  if (record /= 'first') stop 3
  read(unit, '(A)') record
  if (record /= '') stop 4
  backspace(unit)
  read(unit, '(A)') record
  if (record /= '') stop 5
  read(unit, '(A)') record
  if (record /= 'third') stop 6
  read(unit, '(A)') record
  if (record /= 'last') stop 7
  read(unit, '(A)', iostat=io_status) record
  if (io_status >= 0) stop 8
  backspace(unit)
  backspace(unit)
  read(unit, '(A)') record
  if (record /= 'last') stop 9
  rewind(unit)
  read(unit, '(A)') record
  if (record /= 'first') stop 10
  close(unit, status='delete')
end program
program record_length
  implicit none
  integer :: unit, status, values(2), invalid
  character(5) :: record
  open(newunit=unit, status='scratch', form='formatted', recl=5)
  write(unit,'(A)') 'abcde'
  write(unit,'(A/A)') '12345', '67890'
  rewind(unit)
  read(unit,'(A)') record
  if (record /= 'abcde') stop 1
  read(unit,'(A)') record
  if (record /= '12345') stop 2
  read(unit,'(A)') record
  if (record /= '67890') stop 3
  write(unit,'(A)',iostat=status) '123456'
  if (status == 0) stop 4
  close(unit)
  open(newunit=unit, status='scratch', form='formatted', recl=5)
  write(unit,'(A)',advance='no') '123'
  write(unit,'(A)',advance='no') '45'
  write(unit,'(A)',iostat=status) ''
  if (status /= 0) stop 5
  close(unit)
  open(newunit=unit, status='scratch', form='unformatted', recl=8)
  write(unit) [19,23]
  rewind(unit)
  read(unit) values
  if (values(1) /= 19 .or. values(2) /= 23) stop 6
  write(unit,iostat=status) [1,2,3]
  if (status == 0) stop 7
  close(unit)
  invalid = 0
  open(newunit=unit, status='scratch', form='formatted', recl=invalid, iostat=status)
  if (status == 0) stop 8
  print '(A)', 'Fortran RECL: boundaries, non-advancing output and invalid lengths passed'
end program
program nonadvancing_output
  implicit none
  integer :: unit, number, status
  character(256) :: path
  character(32) :: line
  call get_command_argument(1, path)
  open(newunit=unit, file=trim(path), status='replace', form='formatted')
  write(unit,'(A)',advance='no') 'alpha'
  write(unit,'(A,I3)',advance='no') ':', 42
  write(unit,'(A)') ' done'
  write(unit,'(A)') 'next record'
  write(unit,'(/A,2/I3)') 'title', 42
  write(unit,'(1PE12.3)') 1.25d0
  write(unit,'(A 5, I 1 0)') 'space', 42
  write(unit,'(A/T6,A/T3,A)') 'first line', 'second line', 'third line'
  write(unit,'(A,2/,T4,A)') 'before', 'after'
  write(unit,*) 'coordinate ', 'r', ':'
  write(unit,*) '', 'empty prefix'
  write(unit,*) 'left', 7, 'right', 'end'
  write(unit,*) ['ab', 'cd', 'ef']
  rewind(unit)
  read(unit,'(A)') line
  if (trim(line) /= 'alpha: 42 done') stop 1
  read(unit,'(A)') line
  if (trim(line) /= 'next record') stop 2
  close(unit)
  open(newunit=unit, status='scratch', form='formatted')
  write(unit,'(A)') 'skip first', 'skip second', '42'
  rewind(unit)
  read(unit,*)
  read(unit,'()')
  read(unit,*) number
  if (number /= 42) stop 3
  read(unit,*,iostat=status)
  if (status /= -1) stop 4
  close(unit)
  print '(A)', 'non-advancing formatted output passed'
end program
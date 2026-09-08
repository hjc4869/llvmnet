program complex_io
  implicit none
  integer :: unit
  character(256) :: path
  complex(8) :: values(2), restored(2)
  values = [(1.25d0,-2.5d0), (3.0d0,4.75d0)]
  call get_command_argument(1,path)
  open(newunit=unit,file=trim(path),status='replace',form='formatted')
  write(unit,'(4F8.2)') values
  rewind(unit)
  read(unit,'(4F8.2)') restored
  if (any(restored /= values)) stop 1
  close(unit)
  print '(A)', 'formatted complex components passed'
end program
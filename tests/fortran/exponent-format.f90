program exponent_format
  implicit none
  character(256) :: path
  integer :: unit
  call get_command_argument(1, path)
  open(newunit=unit, file=trim(path), status='replace', form='formatted')
  write(unit,'(E16.5E3)') 1.25d100, -1.25d-100, 0d0, -0d0
  write(unit,'(ES16.5E3)') 1.25d100, -1.25d-100
  write(unit,'(E12.3E1)') 1.25d0, 1.25d20
  write(unit,'(G15.5E3)') 1.25d0, 0d0, -0.125d0, 1234.5d0, 1.25d100, -1.25d-100
  write(unit,'(SP,G15.5E3)') 1.25d0
  close(unit)
  print '(A)', 'Fortran explicit exponent widths passed'
end program
program derived_io
  implicit none
  type :: inner_type
    integer :: value
    character(3) :: label
  end type
  type :: outer_type
    type(inner_type) :: inner
    real(8) :: values(2)
  end type
  type(outer_type) :: original(2), restored(2)
  integer :: unit
  character(256) :: path
  original(1)%inner%value = 19
  original(1)%inner%label = 'one'
  original(1)%values = [1.25d0, 2.5d0]
  original(2)%inner%value = 23
  original(2)%inner%label = 'two'
  original(2)%values = [3.75d0, 4.0d0]
  call get_command_argument(1, path)
  open(newunit=unit, file=trim(path), status='replace', form='unformatted')
  write(unit) original
  rewind(unit)
  read(unit) restored
  close(unit)
  if (any(restored%inner%value /= [19,23])) stop 1
  if (restored(1)%inner%label /= 'one' .or. restored(2)%inner%label /= 'two') stop 2
  if (any(restored(1)%values /= original(1)%values) .or. any(restored(2)%values /= original(2)%values)) stop 3
  print '(A)', 'derived type component I/O passed'
end program
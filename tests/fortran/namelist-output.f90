program namelist_output
  implicit none
  integer :: scalar = -42, matrix(2,2), unit, status
  integer(8) :: large = 123456789012345_8
  real(4) :: single = 0.125
  real(8) :: fraction = -1.23456789012345d-21
  logical :: flags(2) = [.true., .false.]
  complex(8) :: pair = (1.5d0, -2.5d0)
  character(24) :: words(3) = [character(24) :: "a'b/c!d", '', 'two words']
  character(8) :: mode
  character(256) :: path
  character(1024) :: record
  namelist /settings/ scalar, matrix, large, single, fraction, flags, pair, words
  call get_command_argument(1, mode)
  call get_command_argument(2, path)
  matrix = reshape([1,2,3,4], [2,2])
  if (trim(mode) == 'write') then
    open(newunit=unit, file=trim(path), status='replace', delim='apostrophe')
    write(unit, nml=settings, iostat=status)
    if (status /= 0) stop 1
    close(unit)
    write(record, nml=settings, delim='quote', iostat=status)
    if (status /= 0) stop 2
    scalar = 0
    read(record, nml=settings, iostat=status)
    if (status /= 0 .or. scalar /= -42) stop 3
  else
    scalar = 0
    matrix = 0
    large = 0
    single = 0
    fraction = 0
    flags = .false.
    pair = 0
    words = ''
    open(newunit=unit, file=trim(path), status='old')
    read(unit, nml=settings, iostat=status)
    if (status /= 0) stop 4
    close(unit)
  end if
  if (scalar /= -42 .or. large /= 123456789012345_8) stop 5
  if (single /= 0.125 .or. fraction /= -1.23456789012345d-21) stop 6
  if (.not. flags(1) .or. flags(2) .or. pair /= (1.5d0, -2.5d0)) stop 7
  if (words(1) /= "a'b/c!d" .or. words(2) /= '' .or. words(3) /= 'two words') stop 8
  if (any(matrix /= reshape([1,2,3,4], [2,2]))) stop 9
  print '(A)', 'namelist output and native interchange passed'
end program
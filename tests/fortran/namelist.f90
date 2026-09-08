program namelist_input
  implicit none
  integer :: scalar = 0, values(0:4) = -1, matrix(2,2) = 0, unit, status
  real(8) :: real_value = 0
  logical :: enabled = .false., named
  character(32) :: label = ''
  complex(8) :: pair = (0,0)
  character(256) :: record
  character(256) :: message
  namelist /settings/ scalar, values, matrix, real_value, enabled, label, pair
  record = '&SETTINGS scalar=42, values=2*7,,9,11, real_value=1.25D2, enabled=.TRUE., label="a/b!c" /'
  read(record, nml=settings, iostat=status)
  if (status /= 0 .or. scalar /= 42 .or. any(values /= [7,7,-1,9,11])) stop 1
  if (real_value /= 125 .or. .not. enabled .or. trim(label) /= 'a/b!c') stop 2
  record = "&settings label='', scalar=42 /"
  read(record, nml=settings, iostat=status)
  if (status /= 0 .or. len_trim(label) /= 0 .or. scalar /= 42) stop 10
  record = '&settings values(1:3:2)=21,23, matrix(:,2)=31,32, pair=(1.5,-2.5) /'
  read(record, nml=settings, iostat=status)
  if (status /= 0 .or. any(values /= [7,21,-1,23,11])) stop 3
  if (any(matrix(:,2) /= [31,32]) .or. pair /= (1.5d0,-2.5d0)) stop 4
  open(newunit=unit, status='scratch', form='formatted')
  inquire(unit=unit, named=named)
  if (named) stop 9
  write(unit,'(A)') '&other scalar=99 /'
  write(unit,'(A)') '&settings ! comment'
  write(unit,'(A)') 'scalar=19 label=''it''''s fine'', values(3)=41,43'
  write(unit,'(A)') '&end'
  rewind(unit)
  read(unit, nml=settings, iostat=status)
  close(unit)
  if (status /= 0 .or. scalar /= 19 .or. trim(label) /= "it's fine") stop 5
  if (any(values /= [7,21,-1,41,43])) stop 6
  record = '&settings missing=1 /'
  message = ''
  read(record, nml=settings, iostat=status, iomsg=message)
  if (status == 0 .or. len_trim(message) == 0) stop 7
  record = '&settings values(99)=1 /'
  read(record, nml=settings, iostat=status)
  if (status == 0) stop 8
  print '(A)', 'namelist scalars, arrays, sections, repeats, quotes and errors passed'
end program
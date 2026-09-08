program logical_input
  implicit none
  character(64) :: record
  logical(1) :: tiny
  logical(2) :: short
  logical(4) :: normal
  logical(8) :: wide
  logical(8) :: values(4)
  integer :: status
  record = '.true., F, T, .false.'
  read(record,*,iostat=status) tiny, short, normal, wide
  if (status /= 0 .or. .not. tiny .or. short .or. .not. normal .or. wide) stop 1
  record = ' T F'
  values = .true.
  read(record,'(2L2)',iostat=status) values(1:3:2)
  if (status /= 0 .or. .not. values(1) .or. .not. values(2) .or. values(3) .or. .not. values(4)) stop 2
  record = 'invalid'
  read(record,*,iostat=status) normal
  if (status == 0) stop 3
  record = ' .TrUe.  .FaLsE.'
  read(record,'(2L8)',iostat=status) normal, wide
  if (status /= 0 .or. .not. normal .or. wide) stop 4
  print '(A)', 'logical input: scalar kinds, strided arrays, fields and errors passed'
end program
program pointer_allocation
  implicit none
  integer, pointer :: first(:, :), saved(:, :), scalar
  integer, target :: original
  integer :: allocation_status
  nullify(first, saved, scalar)
  allocate(first(-1:1, 3:4), stat=allocation_status)
  if (allocation_status /= 0) stop 1
  if (any(lbound(first) /= [-1, 3])) stop 2
  if (any(ubound(first) /= [1, 4])) stop 3
  first = 7
  saved => first
  allocate(first(2:3, -2:0), stat=allocation_status)
  if (allocation_status /= 0) stop 4
  first = 9
  if (sum(first) /= 54 .or. sum(saved) /= 42) stop 5
  if (.not. is_contiguous(first)) stop 13
  if (is_contiguous(first(:, -2:0:2))) stop 14
  call increment_contiguous(first(:, -2:0:2))
  if (sum(first) /= 58 .or. sum(saved) /= 42) stop 12
  deallocate(saved, stat=allocation_status)
  if (allocation_status /= 0 .or. associated(saved)) stop 6
  deallocate(first, stat=allocation_status)
  if (allocation_status /= 0 .or. associated(first)) stop 7
  deallocate(first, stat=allocation_status)
  if (allocation_status == 0) stop 8
  allocate(first(1:0, 1:2), stat=allocation_status)
  if (allocation_status /= 0 .or. size(first) /= 0) stop 9
  if (.not. is_contiguous(first)) stop 15
  deallocate(first)
  original = 17
  scalar => original
  allocate(scalar, stat=allocation_status)
  if (allocation_status /= 0) stop 10
  scalar = 23
  if (original /= 17) stop 11
  deallocate(scalar)
contains
  subroutine increment_contiguous(values)
    integer, contiguous, intent(inout) :: values(:, :)
    values = values + 1
  end subroutine
end program
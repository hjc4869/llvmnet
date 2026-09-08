module assignable_types
  type inner_type
    integer :: values(3)
    character(7) :: label
  end type
  type record_type
    type(inner_type) :: inner
    integer :: number
    integer, pointer :: alias => null()
  end type
end module

program derived_assignment
  use assignable_types
  implicit none
  type(record_type) :: original, copied, records(4)
  type(record_type), allocatable :: allocated(:)
  integer, target :: target = 19
  integer :: index
  original%inner%values = [1,2,3]
  original%inner%label = 'record'
  original%number = 42
  original%alias => target
  copied = original
  original%inner%values(1) = 99
  if (copied%inner%values(1) /= 1 .or. copied%inner%label /= 'record' .or. copied%number /= 42) stop 1
  if (copied%alias /= target) stop 2
  copied%alias = 23
  if (target /= 23) stop 3
  records = copied
  do index=1,4
    records(index)%number = index
  end do
  records(2:4) = records(1:3)
  if (records(1)%number /= 1 .or. records(2)%number /= 1 .or. records(3)%number /= 2 .or. records(4)%number /= 3) stop 4
  allocated = records(1:2)
  if (size(allocated) /= 2 .or. allocated(2)%number /= 1) stop 5
  allocated = records
  if (size(allocated) /= 4 .or. allocated(4)%number /= 3) stop 6
  deallocate(allocated)
  copied%alias = 29
  if (target /= 29) stop 7
  print '(A)', 'derived assignment: nested values, pointers, overlap and allocation passed'
end program
module derived_lifetime_types
  implicit none
  type :: owned_type
    integer :: marker = 19
    integer, allocatable :: values(:)
  end type
  interface
    subroutine initialize(value, source, line) bind(C, name='_FortranAInitialize')
      use iso_c_binding
      type(*), dimension(..) :: value
      type(c_ptr), value :: source
      integer(c_int), value :: line
    end subroutine
  end interface
contains
  subroutine exercise(count)
    integer, intent(in) :: count
    type(owned_type) :: local
    call initialize(local, c_null_ptr_value(), 1)
    if (local%marker /= 19 .or. allocated(local%values)) stop 1
    allocate(local%values(count))
    local%values = 42
    if (sum(local%values) /= 42 * count) stop 2
  end subroutine
  function c_null_ptr_value() result(value)
    use iso_c_binding
    type(c_ptr) :: value
    value = c_null_ptr
  end function
end module

program derived_lifetime
  use derived_lifetime_types
  implicit none
  integer :: index
  type(owned_type), allocatable :: allocated_values(:)
  type(owned_type), pointer :: pointers(:)
  do index = 1, 1000
    call exercise(1000)
  end do
  allocate(allocated_values(2))
  if (any(allocated_values%marker /= 19) .or. allocated(allocated_values(1)%values)) stop 3
  allocate(allocated_values(1)%values(100))
  allocated_values(1)%values = 7
  deallocate(allocated_values)
  allocate(pointers(-1:1))
  if (any(pointers%marker /= 19) .or. lbound(pointers,1) /= -1) stop 4
  allocate(pointers(0)%values(100))
  pointers(0)%values = 11
  deallocate(pointers)
  nullify(pointers)
  print '(A)', 'derived initialization and allocatable component lifetime passed'
end program
module arithmetic
  implicit none
contains
  integer function triple(value)
    integer, intent(in) :: value
    triple = value * 3
  end function
end module
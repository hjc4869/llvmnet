! Zero-length character variables live at a non-null address, as on a native stack: flang-rt treats a null base
! address as an unallocated variable (gfortran pr96486 and auto_char_dummy_array_1).
program zero_length_character
  implicit none
  character(0) :: value
  integer :: length, status
  call get_environment_variable("HOME", value, length=length, status=status)
  print '(i0,1x,l1)', status, length > 0
  call fill(0)
  call fill(3)
contains
  subroutine fill(n)
    integer, intent(in) :: n
    character(n), dimension(3) :: s
    s = "lmn"
    print '(i0,1x,a,1x,i0)', n, s(2), len(s)
  end subroutine
end program

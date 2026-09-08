program dimension_inquiry
  implicit none
  integer :: values(2:6,-1:7)
  values = 0
  call check(values, [5,9])
  call check(values(2:6:2,-1:7:2), [3,5])
  call check(values(2:1,:), [0,9])
  call check(values(:,3:2), [5,0])
  print '(A)', 'Fortran SIZE dimensions, strided sections and empty extents passed'
contains
  subroutine check(array, expected)
    integer, intent(in) :: array(:,:), expected(2)
    integer :: dimension
    if (size(array,kind=8) /= int(expected(1),kind=8) * expected(2)) stop 2
    do dimension=1,2
      if (size(array,dim=dimension,kind=8) /= expected(dimension)) stop 1
    end do
  end subroutine
end program
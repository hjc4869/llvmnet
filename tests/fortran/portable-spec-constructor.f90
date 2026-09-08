program constructor
  integer, volatile :: count = 3
  integer :: index, outer
  integer, allocatable :: values(:)
  values = [([(index, index=1,outer)], outer=1,count)]
  if (any(values /= [1,1,2,1,2,3])) stop 1
end program
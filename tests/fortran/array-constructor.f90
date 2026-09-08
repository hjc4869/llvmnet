program dynamic_constructor
  implicit none
  integer, volatile :: count = 50
  integer :: index, position, item
  integer, allocatable :: result(:)
  character(3), allocatable :: text(:)
  result = [( [(item,item=1,index)], index=1,count )]
  if (size(result) /= count * (count+1) / 2) stop 1
  position = 0
  do index=1,count
    do item=1,index
      position = position + 1
      if (result(position) /= item) stop 2
    end do
  end do
  text = [(repeat('x',3), index=1,count)]
  if (size(text) /= count .or. any(text /= 'xxx')) stop 3
  print '(A)', 'dynamic array constructors and growth passed'
end program
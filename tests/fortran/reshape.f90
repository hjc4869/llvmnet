program array_reshape
  implicit none
  integer, volatile :: shape(2), order(2), source(4)
  integer, allocatable :: result(:,:)
  character(3) :: words(2) = ['one','two']
  character(3), allocatable :: text(:,:)
  source = [1,2,3,4]
  shape = [2,3]
  order = [2,1]
  result = reshape(source, shape, [5,6], order)
  if (any(result(:,1) /= [1,4]) .or. any(result(:,2) /= [2,5]) .or. any(result(:,3) /= [3,6])) stop 1
  order = [1,2]
  result = reshape(source(4:1:-1), shape, [8], order)
  if (any(result(:,1) /= [4,3]) .or. any(result(:,2) /= [2,1]) .or. any(result(:,3) /= [8,8])) stop 2
  shape = [0,2]
  result = reshape(source, shape)
  if (size(result) /= 0) stop 3
  shape = [2,2]
  text = reshape(words, shape, ['end'])
  if (any(text(:,1) /= words) .or. any(text(:,2) /= ['end','end'])) stop 4
  print '(A)', 'RESHAPE order, padding, strides and empty results passed'
end program
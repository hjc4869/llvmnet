program spread_arrays
  implicit none
  integer :: original(4), actual(2, 3), transposed(3, 2), scalar(3)
  integer :: count, io_status
  character(8) :: argument
  call get_command_argument(1, argument, status=io_status)
  if (io_status /= 0) stop 1
  read(argument, *) count
  original = [1, 2, 3, 4]
  actual = spread(original(1:4:2), 2, count)
  if (any(actual(:, 1) /= [1, 3])) stop 2
  if (any(actual(:, 2) /= [1, 3])) stop 3
  if (any(actual(:, 3) /= [1, 3])) stop 4
  transposed = spread(original(1:4:2), 1, count)
  if (any(transposed(1, :) /= [1, 3])) stop 5
  if (any(transposed(3, :) /= [1, 3])) stop 6
  scalar = spread(original(2), 1, count)
  if (any(scalar /= 2)) stop 7
  if (size(spread(original, 1, count - 3)) /= 0) stop 8
  if (size(spread(original, 1, count - 4)) /= 0) stop 9
  if (size(spread(original(1:0), 2, count)) /= 0) stop 10
end program
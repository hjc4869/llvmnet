program random_sequence
  implicit none
  integer :: size, seed(1), saved(1), index
  real(8) :: values(10), expected(5)
  real(4) :: singles(5)
  call random_seed(size=size)
  if (size /= 1) stop 1
  seed = 12345
  call random_seed(put=seed)
  call random_number(values)
  do index=1,10
    print '(I20)', int(values(index) * 2d0**53,8)
  end do
  call random_seed(get=saved)
  call random_number(expected)
  call random_seed(put=saved)
  values = -1
  call random_number(values(1:9:2))
  if (any(values(1:9:2) /= expected) .or. any(values(2:10:2) /= -1)) stop 2
  seed = -1
  call random_seed(put=seed)
  call random_number(singles)
  do index=1,5
    print '(I20)', int(singles(index) * 2.0**24,8)
  end do
  call random_init(.true., .false.)
  call random_number(values)
  do index=1,10
    print '(I20)', int(values(index) * 2d0**53,8)
  end do
end program
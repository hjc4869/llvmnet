program concurrent_workers
  use iso_c_binding, only: c_int
  implicit none
  interface
    pure function concurrent_worker(index) bind(C) result(encoded)
      import c_int
      integer(c_int), value :: index
      integer(c_int) :: encoded
    end function
  end interface
  integer(c_int) :: values(256), matrix(8,16)
  integer :: index, column, worker, total, counts(0:3)
  do concurrent (index=1:256)
    values(index) = concurrent_worker(index)
  end do
  counts = 0
  do index=1,256
    worker = values(index) - 8 * index
    if (worker < 0 .or. worker > 3) stop 1
    counts(worker) = counts(worker) + 1
  end do
  do index=0,3
    if (counts(index) == 0) stop 2
  end do
  do concurrent (index=1:8, column=1:16)
    matrix(index,column) = index * column
  end do
  total = 0
  do column=1,16
    do index=1,8
      total = total + matrix(index,column)
    end do
  end do
  if (total /= 4896) stop 3
  print '(A)', 'Fortran DO CONCURRENT: four workers and nested loops passed'
end program
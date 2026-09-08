program pointer_association
  implicit none
  integer, target :: values(8), other(8), scalar
  integer, pointer :: pointer(:), alias(:), element
  character(0), target :: empty
  character(:), pointer :: text
  values = 1
  other = 1
  nullify(pointer)
  if (associated(pointer, values)) stop 1
  pointer(-2:) => values
  if (.not. associated(pointer, values) .or. associated(pointer, other)) stop 2
  alias => pointer
  if (.not. associated(alias, values)) stop 3
  pointer => values(1:8:2)
  if (.not. associated(pointer, values(1:8:2))) stop 4
  if (associated(pointer, values(1:4))) stop 5
  pointer => values(8:1:-1)
  if (associated(pointer, values) .or. .not. associated(pointer, values(8:1:-1))) stop 6
  pointer => values(1:0)
  if (associated(pointer, values(1:0))) stop 7
  element => scalar
  if (.not. associated(element, scalar) .or. associated(element, values(1))) stop 8
  text => empty
  if (associated(text, empty)) stop 9
  print '(A)', 'Fortran pointer target association, bounds, strides and empty storage passed'
end program
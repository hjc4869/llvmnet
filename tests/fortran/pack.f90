program array_pack
  implicit none
  integer :: values(4,2), fill(6), index
  integer, allocatable :: packed(:)
  logical :: mask(4,2)
  character(3) :: words(3) = ['one', 'two', 'six']
  character(3), allocatable :: selected_words(:)
  character(2), allocatable :: short_words(:)
  values = reshape([(index, index=1,8)], [4,2])
  mask = mod(values, 2) == 0
  packed = pack(values, mask)
  if (any(packed /= [2,4,6,8])) stop 1
  packed = pack(values(4:1:-2,:), mask(4:1:-2,:))
  if (any(packed /= [4,2,8,6])) stop 2
  fill = [11,12,13,14,15,16]
  packed = pack(values, mask, fill)
  if (any(packed /= [2,4,6,8,15,16])) stop 3
  packed = pack(values, .false.)
  if (size(packed) /= 0) stop 4
  packed = pack(values, .true.)
  if (any(packed /= [(index,index=1,8)])) stop 5
  selected_words = pack(words, [.true.,.false.,.true.])
  if (any(selected_words /= ['one','six'])) stop 6
  short_words = pack(words, [.true.,.false.,.true.])
  if (any(short_words /= ['on','si']) .or. len(short_words) /= 2) stop 7
  print '(A)', 'PACK array order, masks, vector fill and character data passed'
end program
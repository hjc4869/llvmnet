program deferred_character
  implicit none
  character(:), allocatable :: text, words(:)
  character(2), allocatable :: fixed_words(:)
  integer, volatile :: length = 5
  allocate(character(length) :: text)
  text = 'hello'
  if (len(text) /= 5 .or. text /= 'hello') stop 1
  text = 'longer text'
  if (len(text) /= 11 .or. text /= 'longer text') stop 2
  text = ''
  if (.not. allocated(text) .or. len(text) /= 0) stop 3
  deallocate(text)
  allocate(character(length) :: words(2))
  words = ['ab','cd']
  if (len(words) /= 2 .or. any(words /= ['ab','cd'])) stop 4
  words = ['xyz','123','end']
  if (size(words) /= 3 .or. len(words) /= 3) stop 5
  fixed_words = words
  if (len(fixed_words) /= 2 .or. any(fixed_words /= ['xy','12','en'])) stop 6
  print '(A)', 'deferred character allocation and length changes passed'
end program
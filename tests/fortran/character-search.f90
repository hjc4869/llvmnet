program character_search
  implicit none
  character(32) :: text
  character(5) :: binary
  integer :: copies
  call get_command_argument(1, text)
  if (text /= 'ababa') stop 1
  if (index(text(:5), 'aba') /= 1) stop 2
  if (index(text(:5), 'aba', back=.true.) /= 3) stop 3
  if (index(text(:5), 'missing') /= 0) stop 4
  if (index(text(:5), '') /= 1) stop 5
  if (index(text(:5), '', back=.true.) /= 6) stop 6
  if (index(text(:0), '') /= 1) stop 7
  if (index(text(:0), '', back=.true.) /= 1) stop 8
  if (index(text(:0), 'a') /= 0) stop 9
  binary = 'a' // achar(0) // 'b' // achar(0) // 'a'
  if (index(binary, achar(0) // 'b') /= 2) stop 10
  if (index(binary, achar(0), back=.true.) /= 4) stop 11
  copies = index(text(:5), 'b') + 1
  if (repeat(text(:2), copies) /= 'ababab') stop 12
  if (len(repeat(text(:2), copies - 3)) /= 0) stop 13
  if (len(repeat(text(:0), copies)) /= 0) stop 14
  if (index(repeat(binary, copies), achar(0), back=.true.) /= 14) stop 15
  if (adjustl('  ' // text(:5)) /= 'ababa  ') stop 16
  if (len(adjustl(text(:0))) /= 0) stop 17
  if (adjustl(repeat(' ', copies)) /= '   ') stop 18
  if (adjustl(achar(9) // text(:5)) /= achar(9) // 'ababa') stop 19
  if (scan(text(:5), 'bc') /= 2 .or. scan(text(:5), 'bc', back=.true.) /= 4) stop 20
  if (scan(text(:5), '') /= 0 .or. scan(text(:0), 'ab') /= 0) stop 21
  if (verify(text(:5), 'ab') /= 0 .or. verify(text(:5), 'a') /= 2) stop 22
  if (verify(text(:5), '', back=.true.) /= 5 .or. verify(text(:0), '') /= 0) stop 23
  if (scan(binary, achar(0), back=.true.) /= 4 .or. verify(binary, 'ab') /= 2) stop 24
end program
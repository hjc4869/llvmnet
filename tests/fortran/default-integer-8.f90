! Built with -fdefault-integer-8, Flang defines i64 main(i64, ptr, ptr) and calls _FortranAProgramStart, whose
! argument count is a C int, with an i64 (BE-39). Also valid without the option.
program default_integer_8
  integer :: count, big
  big = huge(big)
  count = command_argument_count()
  print '(i0)', kind(count)
  print '(i0)', big
  print '(i0)', count
  stop 3
end program

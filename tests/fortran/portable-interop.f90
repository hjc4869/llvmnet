program portable_interop
  use iso_c_binding
  implicit none
  interface
    function portable_long_double(value) bind(C) result(output)
      import c_long_double
      real(c_long_double), value :: value
      real(c_long_double) :: output
    end function
  end interface
  real(c_long_double) :: value
  value = portable_long_double(1.0_c_long_double)
  if (value - 1.0_c_long_double /= 2.0_c_long_double ** (-63)) stop 1
end program
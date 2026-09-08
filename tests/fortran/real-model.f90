program real_model
  implicit none
  real(8), volatile :: values(11)
  real(8), volatile :: scaled_inputs(9)
  real(8) :: scaled_expected(9), replaced_expected(9), scaled
  real(8) :: fraction_expected(11), fraction_value
  integer(8) :: powers(9) = [0_8,2_8,-1074_8,1074_8,huge(1_8),huge(1_8),-huge(1_8),2_8,2_8]
  integer :: index
  integer :: expected(11) = [0,0,1,0,-1021,-1073,1024,54,2147483647,2147483647,2147483647]
  values = [0d0, -0d0, 1d0, -0.75d0, tiny(1d0), transfer(1_8,1d0), &
    huge(1d0), 9007199254740993d0, transfer(int(z'7ff0000000000000',8),1d0), &
    transfer(int(z'fff0000000000000',8),1d0), transfer(int(z'7ff8000000000000',8),1d0)]
  do index=1,size(values)
    if (exponent(values(index)) /= expected(index)) stop 1
  end do
  fraction_expected = [0d0,-0d0,0.5d0,-0.75d0,0.5d0,0.5d0, &
    0.9999999999999999d0,0.5d0,values(11),values(11),values(11)]
  do index=1,size(values)
    fraction_value = fraction(values(index))
    if (fraction_expected(index) /= fraction_expected(index)) then
      if (fraction_value == fraction_value) stop 5
    else if (fraction_value /= fraction_expected(index)) then
      stop 6
    else if (fraction_value == 0d0) then
      if (transfer(fraction_value,0_8) /= transfer(fraction_expected(index),0_8)) stop 7
    end if
  end do
  scaled_inputs = [1d0,1.5d0,1d0,transfer(1_8,1d0),-0d0,1d0,-1d0,values(9),values(11)]
  scaled_expected = [1d0,6d0,transfer(1_8,1d0),1d0,-0d0,values(9),-0d0,values(9),values(11)]
  do index=1,size(powers)
    scaled = scale(scaled_inputs(index),powers(index))
    if (scaled_expected(index) /= scaled_expected(index)) then
      if (scaled == scaled) stop 2
    else if (scaled /= scaled_expected(index)) then
      stop 3
    else if (scaled == 0d0) then
      if (transfer(scaled,0_8) /= transfer(scaled_expected(index),0_8)) stop 4
    end if
  end do
  replaced_expected = [0.5d0,3d0,0d0,values(9),-0d0,values(9),-0d0,values(11),values(11)]
  do index=1,size(powers)
    scaled = set_exponent(scaled_inputs(index),powers(index))
    if (replaced_expected(index) /= replaced_expected(index)) then
      if (scaled == scaled) stop 8
    else if (scaled /= replaced_expected(index)) then
      stop 9
    else if (scaled == 0d0) then
      if (transfer(scaled,0_8) /= transfer(replaced_expected(index),0_8)) stop 10
    end if
  end do
  print '(A)', 'Fortran real model: exponent, scale, fraction and set_exponent passed'
end program
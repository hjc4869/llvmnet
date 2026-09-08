program inquire_length
  implicit none
  type record_type
    character(3) :: label
    real(8) :: value
    integer(2) :: code
  end type
  type(record_type) :: record
  integer :: length, values(6) = [1,2,3,4,5,6]
  integer(8) :: wide_length
  real(8) :: real_value = 1.5d0
  complex(8) :: pair = (2d0, -3d0)
  logical :: enabled = .true.
  character(7) :: label = 'example'
  record%label = 'abc'
  record%value = 2.5d0
  record%code = 19
  inquire(iolength=length) values(1), real_value, pair, enabled, label
  if (length /= 39) stop 1
  inquire(iolength=wide_length) values(1:6:2)
  if (wide_length /= 12) stop 2
  inquire(iolength=length) values(1:0)
  if (length /= 0) stop 3
  inquire(iolength=length) record
  if (length /= 13) stop 4
  inquire(iolength=length) ''
  if (length /= 0) stop 5
  print '(A)', 'Fortran IOLENGTH: intrinsic bytes, sections and derived components passed'
end program
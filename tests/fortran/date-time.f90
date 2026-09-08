program date_time
  implicit none
  character(8) :: date
  character(10) :: time
  character(5) :: zone
  integer :: values(8), parsed(8), zone_hour, zone_minute
  integer(8) :: wide(8)
  integer :: count, rate, maximum
  integer(8) :: count64, rate64, maximum64, next64
  real(8) :: cpu
  call date_and_time(date, time, zone, values)
  read(date,'(I4,2I2)') parsed(1:3)
  read(time,'(3I2,1X,I3)') parsed(5:8)
  read(zone(2:),'(2I2)') zone_hour, zone_minute
  parsed(4) = zone_hour * 60 + zone_minute
  if (zone(1:1) == '-') parsed(4) = -parsed(4)
  if (any(values /= parsed)) stop 1
  if (values(1) < 2000 .or. values(2) < 1 .or. values(2) > 12) stop 2
  if (values(5) < 0 .or. values(5) > 23 .or. values(8) < 0 .or. values(8) > 999) stop 3
  call date_and_time(values=wide)
  if (wide(1) < 2000 .or. wide(2) < 1 .or. wide(2) > 12) stop 4
  call system_clock(count, rate, maximum)
  if (count < 0 .or. count > maximum .or. rate /= 1000 .or. maximum /= huge(count)) stop 5
  call system_clock(count64, rate64, maximum64)
  call system_clock(next64)
  if (count64 < 0 .or. next64 < count64 .or. rate64 /= 1000000000_8 .or. maximum64 /= huge(count64)) stop 6
  call cpu_time(cpu)
  if (cpu < 0) stop 7
  print '(A)', 'DATE_AND_TIME fields and integer kinds passed'
end program
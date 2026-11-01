! EXECUTE_COMMAND_LINE's EXITSTAT, CMDSTAT and CMDMSG for a command that succeeds, one that exits with a status and
! one that is not found. flang-rt passed system()'s status to musl's WIFSIGNALED as a 64-bit integer, for which the
! macro is true when the status is 0, so portable programs saw CMDSTAT 7 ("Killed by signal") after every successful
! command (ports/flang-rt/execute-status.patch, found by flang-rt's unit tests, WG-03).
program execute_command
  implicit none
  call run('true')
  call run('exit 3')
  call run('nonexistent-llvmnet-command 2>/dev/null')
contains
  subroutine run(command)
    character(len=*), intent(in) :: command
    integer :: exitstat, cmdstat
    character(len=60) :: cmdmsg
    exitstat = 404
    cmdstat = 202
    cmdmsg = 'unchanged'
    call execute_command_line(command, exitstat=exitstat, cmdstat=cmdstat, cmdmsg=cmdmsg)
    print '(a,a,i0,a,i0,a,a)', command, ': exitstat=', exitstat, ' cmdstat=', cmdstat, ' cmdmsg=', trim(cmdmsg)
  end subroutine
end program

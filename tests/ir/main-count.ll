; K&R-style main(argc), as in gcc-c-torture's alias-4: the entry point passes the count alone. With no arguments
; argc is 1, so the program exits with 0.
define i32 @main(i32 %argc) {
entry:
  %status = sub i32 %argc, 1
  ret i32 %status
}

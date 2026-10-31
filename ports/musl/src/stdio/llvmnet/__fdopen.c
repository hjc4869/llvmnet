/* llvmnet port of musl: as upstream, with a 4096-byte buffer instead of BUFSIZ (1024). glibc sizes stdio buffers
 * by st_blksize, 4096 on Linux file systems, so portable programs flush where native builds do, and each host write
 * call carries four times the data (PF-03). BUFSIZ itself, part of the dotnet64-v2 ABI, stays 1024. */
#include "stdio_impl.h"
#include <stdlib.h>
#include <sys/ioctl.h>
#include <fcntl.h>
#include <errno.h>
#include <string.h>
#include "libc.h"

#define STDIO_BUFFER 4096

FILE *__fdopen(int fd, const char *mode)
{
	FILE *f;
	struct winsize wsz;

	/* Check for valid initial mode character */
	if (!strchr("rwa", *mode)) {
		errno = EINVAL;
		return 0;
	}

	/* Allocate FILE+buffer or fail */
	if (!(f=malloc(sizeof *f + UNGET + STDIO_BUFFER))) return 0;

	/* Zero-fill only the struct, not the buffer */
	memset(f, 0, sizeof *f);

	/* Impose mode restrictions */
	if (!strchr(mode, '+')) f->flags = (*mode == 'r') ? F_NOWR : F_NORD;

	/* Apply close-on-exec flag */
	if (strchr(mode, 'e')) __syscall(SYS_fcntl, fd, F_SETFD, FD_CLOEXEC);

	/* Set append mode on fd if opened for append */
	if (*mode == 'a') {
		int flags = __syscall(SYS_fcntl, fd, F_GETFL);
		if (!(flags & O_APPEND))
			__syscall(SYS_fcntl, fd, F_SETFL, flags | O_APPEND);
		f->flags |= F_APP;
	}

	f->fd = fd;
	f->buf = (unsigned char *)f + sizeof *f + UNGET;
	f->buf_size = STDIO_BUFFER;

	/* Activate line buffered mode for terminals */
	f->lbf = EOF;
	if (!(f->flags & F_NOWR) && !__syscall(SYS_ioctl, fd, TIOCGWINSZ, &wsz))
		f->lbf = '\n';

	/* Initialize op ptrs. No problem if some are unneeded. */
	f->read = __stdio_read;
	f->write = __stdio_write;
	f->seek = __stdio_seek;
	f->close = __stdio_close;

	if (!libc.threaded) f->lock = -1;

	/* Add new FILE to open file list */
	return __ofl_add(f);
}

weak_alias(__fdopen, fdopen);

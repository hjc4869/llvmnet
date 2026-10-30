/* llvmnet port of musl: posix_spawn without fork or exec (plan item LC-08). The host starts the program as a
 * separate process (__llvmnet_spawn in src/LlvmNet.Runtime/Host/HostProcess.cs). The file actions are applied
 * here to a table of the child's standard input, output and error, which are all a host process can inherit;
 * descriptors above 2 are never inherited. posix_spawnp, system and popen use this function unchanged. Spawn
 * attributes (signal mask and defaults, process group, scheduling) have no host equivalent and are ignored. */
#define _GNU_SOURCE
#include <spawn.h>
#include <fcntl.h>
#include <unistd.h>
#include <errno.h>
#include "../fdop.h"

extern char **__environ;
int __llvmnet_spawn(pid_t *, const char *, int, char *const *, char *const *, const int *, const char *);

int posix_spawn(pid_t *restrict res, const char *restrict path,
	const posix_spawn_file_actions_t *fa,
	const posix_spawnattr_t *restrict attr,
	char *const argv[restrict], char *const envp[restrict])
{
	int streams[3] = { 0, 1, 2 }, opened[3] = { -1, -1, -1 }, ret = 0, i;
	const char *directory = 0;
	pid_t pid;
	if (fa && fa->__actions) {
		struct fdop *op;
		/* Actions are kept newest first; apply them in the order they were added. */
		for (op = fa->__actions; op->next; op = op->next);
		for (; op && !ret; op = op->prev) {
			switch (op->cmd) {
			case FDOP_CLOSE:
				if (op->fd < 3) streams[op->fd] = -1;
				break;
			case FDOP_DUP2:
				if (op->fd < 3) streams[op->fd] = op->srcfd < 3 ? streams[op->srcfd] : op->srcfd;
				break;
			case FDOP_OPEN:
				if (op->fd < 3) {
					int fd = open(op->path, op->oflag, op->mode);
					if (fd < 0) { ret = errno; break; }
					if (opened[op->fd] >= 0) close(opened[op->fd]);
					opened[op->fd] = streams[op->fd] = fd;
				}
				break;
			case FDOP_CHDIR:
				directory = op->path;
				break;
			case FDOP_FCHDIR:
				ret = ENOSYS;
				break;
			}
		}
	}
	if (!ret) ret = __llvmnet_spawn(&pid, path, attr && attr->__fn, argv,
		envp ? envp : __environ, streams, directory);
	for (i = 0; i < 3; i++)
		if (opened[i] >= 0) close(opened[i]);
	if (!ret && res) *res = pid;
	return ret;
}

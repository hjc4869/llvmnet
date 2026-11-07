/* llvmnet port of mimalloc: the primitive layer of portable programs (dotnet64-v2), included by src/prim/prim.c
 * (ports/mimalloc/llvmnet.patch).
 *
 * Memory comes only from the host's allocator entry points over .NET's NativeMemory (docs/dev/host-abi.md):
 * __llvmnet_memory_allocate returns zeroed memory with the requested alignment and __llvmnet_memory_free releases
 * such a block whole. Nothing here maps, unmaps, advises or protects pages, not even through the host's emulation
 * of those calls, so the allocator works wherever .NET runs. A block is usable as soon as it is allocated: commit,
 * decommit, reset, reuse and guards only report success, a decommitted range keeps its contents (no recommit is
 * needed), and blocks are never freed in part. The clock, random bytes, the environment and error output go through
 * musl, which reaches the host through its system calls; thread exit is detected with a pthread key, as on Unix. */

#include "mimalloc.h"
#include "mimalloc/prim.h"
#include "mimalloc/prim/os.h"
#include "mimalloc/util/libc.h"
#include "mimalloc/heap/process.h"

#include <errno.h>
#include <pthread.h>
#include <sched.h>
#include <stdlib.h>
#include <sys/resource.h>
#include <time.h>
#include <unistd.h>

void *__llvmnet_memory_allocate(size_t size, size_t alignment);
int __llvmnet_memory_free(void *address);

//---------------------------------------------
// Initialize
//---------------------------------------------

// musl sets errno whenever an allocation fails; mimalloc's default handler sets it only when it is still 0.
static void mi_llvmnet_error(int err, void *arg) {
  MI_UNUSED(arg);
  if (err == ENOMEM || err == EOVERFLOW) errno = ENOMEM;
  else if (err == EINVAL) errno = EINVAL;
}

void _mi_prim_mem_init( mi_os_mem_config_t* config ) {
  config->page_size = 4096;           // the page size the host reports (AT_PAGESZ)
  config->alloc_granularity = 4096;
  long pages = sysconf(_SC_PHYS_PAGES);
  if (pages > 0) {
    config->physical_memory_in_kib = (size_t)pages * (config->page_size / MI_KiB);
  }
  config->large_page_size = 0;
  config->has_overcommit = false;
  config->has_partial_free = false;   // a block is freed whole
  config->has_virtual_reserve = false;  // allocated memory is usable at once, so reserving costs memory
  config->has_transparent_huge_pages = false;
  mi_register_error(&mi_llvmnet_error, NULL);
  // Purging decommits or resets unused ranges, which returns nothing to the host here: off unless MIMALLOC_PURGE_DELAY asks.
  mi_option_set_default(mi_option_purge_delay, -1);
}

//---------------------------------------------
// Allocation and free
//---------------------------------------------

int _mi_prim_free(void* addr, size_t size ) {
  MI_UNUSED(size);
  return __llvmnet_memory_free(addr);
}

int _mi_prim_alloc(void* hint_addr, size_t size, size_t try_alignment, bool commit, bool allow_large, bool* is_large, bool* is_zero, void** addr) {
  MI_UNUSED(hint_addr); MI_UNUSED(commit); MI_UNUSED(allow_large);
  *is_large = false;
  *addr = __llvmnet_memory_allocate(size, try_alignment);
  if (*addr == NULL) {
    errno = ENOMEM;  // as a failing mmap would set it
    *is_zero = false;
    return ENOMEM;
  }
  *is_zero = true;
  return 0;
}

//---------------------------------------------
// Commit/Reset/Protect: memory stays accessible and keeps its contents
//---------------------------------------------

int _mi_prim_commit(void* addr, size_t size, bool* is_zero) {
  MI_UNUSED(addr); MI_UNUSED(size);
  *is_zero = false;
  return 0;
}

int _mi_prim_decommit(void* addr, size_t size, bool* needs_recommit) {
  MI_UNUSED(addr); MI_UNUSED(size);
  *needs_recommit = false;
  return 0;
}

int _mi_prim_reset(void* addr, size_t size) {
  MI_UNUSED(addr); MI_UNUSED(size);
  return 0;
}

int _mi_prim_reuse(void* addr, size_t size) {
  MI_UNUSED(addr); MI_UNUSED(size);
  return 0;
}

int _mi_prim_guard(void* addr, size_t size, bool set, bool is_committed) {
  MI_UNUSED(addr); MI_UNUSED(size); MI_UNUSED(set); MI_UNUSED(is_committed);
  return 0;
}

//---------------------------------------------
// Huge pages and NUMA nodes
//---------------------------------------------

int _mi_prim_alloc_huge_os_pages(void* hint_addr, size_t size, int numa_node, bool* is_zero, void** addr) {
  MI_UNUSED(hint_addr); MI_UNUSED(size); MI_UNUSED(numa_node);
  *is_zero = false;
  *addr = NULL;
  return ENOSYS;
}

size_t _mi_prim_numa_node(void) {
  return 0;
}

size_t _mi_prim_numa_node_count(void) {
  return 1;
}

//----------------------------------------------------------------
// Clock and process information
//----------------------------------------------------------------

mi_msecs_t _mi_prim_clock_now(void) {
  struct timespec t;
  if (clock_gettime(CLOCK_MONOTONIC, &t) != 0) return 0;
  return ((mi_msecs_t)t.tv_sec * 1000) + ((mi_msecs_t)t.tv_nsec / 1000000L);
}

static mi_msecs_t mi_llvmnet_msecs(const struct timeval* t) {
  return ((mi_msecs_t)t->tv_sec * 1000) + ((mi_msecs_t)t->tv_usec / 1000);
}

void _mi_prim_process_info(mi_process_info_t* pinfo) {
  struct rusage usage;
  if (getrusage(RUSAGE_SELF, &usage) != 0) return;
  pinfo->utime = mi_llvmnet_msecs(&usage.ru_utime);
  pinfo->stime = mi_llvmnet_msecs(&usage.ru_stime);
  pinfo->page_faults = (size_t)usage.ru_majflt;
  pinfo->peak_rss = (size_t)usage.ru_maxrss * MI_KiB;
  pinfo->current_rss = pinfo->peak_rss;
}

//----------------------------------------------------------------
// Output, environment and randomness
//----------------------------------------------------------------

void _mi_prim_out_stderr( const char* msg ) {
  size_t length = _mi_strlen(msg);
  while (length > 0) {
    ssize_t written = write(STDERR_FILENO, msg, length);
    if (written <= 0) {
      if (written < 0 && errno == EINTR) continue;
      return;
    }
    msg += written;
    length -= (size_t)written;
  }
}

int _mi_prim_getenv(const char* name, char* result, size_t result_size) {
  const char* s = getenv(name);
  if (s == NULL) {
    // the options are spelled in lower case; try the upper-case name as well
    char buf[64+1];
    size_t len = _mi_strnlen(name, sizeof(buf)-1);
    for (size_t i = 0; i < len; i++) {
      buf[i] = _mi_toupper(name[i]);
    }
    buf[len] = 0;
    s = getenv(buf);
  }
  if (s == NULL || _mi_strnlen(s, result_size) >= result_size) return 0;
  _mi_strlcpy(result, s, result_size);
  return 1;
}

bool _mi_prim_random_buf(void* buf, size_t buf_len) {
  return getentropy(buf, buf_len) == 0;
}

//----------------------------------------------------------------
// Thread init/done: musl runs the key's destructor when a thread exits
//----------------------------------------------------------------

static _Atomic(pthread_key_t) _mi_heap_default_key = MI_ATOMIC_VAR_INIT(MI_PTHREAD_KEY_INVALID);

static void mi_pthread_done(void* value) {
  if (value != NULL) {
    _mi_thread_done((mi_theap_t*)value);
  }
}

void _mi_prim_thread_init_auto_done(void) {
  mi_assert_internal(mi_atomic_load_relaxed(&_mi_heap_default_key) == MI_PTHREAD_KEY_INVALID);
  pthread_key_t key;
  if (pthread_key_create(&key, &mi_pthread_done) == 0) {
    mi_atomic_store_release(&_mi_heap_default_key, key);
  }
}

void _mi_prim_thread_done_auto_done(void) {
  pthread_key_t key = mi_atomic_exchange_relaxed(&_mi_heap_default_key, MI_PTHREAD_KEY_INVALID);
  if (key != MI_PTHREAD_KEY_INVALID) {
    pthread_key_delete(key);
  }
}

void _mi_prim_thread_associate_default_theap(mi_theap_t* theap) {
  pthread_key_t key = mi_atomic_load_relaxed(&_mi_heap_default_key);
  if (key != MI_PTHREAD_KEY_INVALID) {
    pthread_setspecific(key, theap);
  }
}

bool _mi_prim_thread_is_in_threadpool(void) {
  return false;
}

void _mi_prim_thread_yield(void) {
  sched_yield();
}

bool _mi_prim_process_is_killed(void) {
  return false;
}

using System.Collections.Concurrent;
using static LlvmNet.Runtime.Linux;

namespace LlvmNet.Runtime;

/// <summary>Threads for musl's pthreads (plan item LC-02, ADR-07): thread pointers, clone on managed threads and futexes.</summary>
public static unsafe partial class HostSyscalls
{
    [ThreadStatic] private static nint threadPointer;
    [ThreadStatic] private static int threadId;
    [ThreadStatic] private static bool clonedThread;
    private static nint processThreadPointer;
    private static int nextThreadId = 1;
    private static int clonedThreads;
    private static readonly ConcurrentDictionary<nint, object> futexes = new();
    private static readonly ConcurrentDictionary<int, Thread> threads = new();
    [ThreadStatic] private static nint robustList;

    [CExport("__llvmnet_get_tp")]
    public static long GetThreadPointer() => threadPointer;

    internal static int CurrentThreadId => threadId != 0 ? threadId : threadId = Interlocked.Increment(ref nextThreadId) + 100;

    private static long SetThreadPointer(nint pointer)
    {
        threadPointer = pointer;
        // The first thread pointer is the main thread's; .NET signal threads borrow it to run handlers.
        if (Interlocked.CompareExchange(ref processThreadPointer, pointer, 0) == 0) threads[CurrentThreadId] = Thread.CurrentThread;
        return 0;
    }

    /// <summary>
    /// tkill and tgkill. A signal for the calling thread is raised; SIGCANCEL for another thread interrupts that thread's
    /// blocking call, which returns -EINTR so its cancellation point acts (ports/musl/src/thread/llvmnet/pthread_cancel.c).
    /// Other signals for other threads run their handler on the calling thread, as signals raised inside the process do.
    /// </summary>
    private static long SendSignal(int thread, int signal)
    {
        if (thread <= 0) return -EINVAL;
        if (thread == CurrentThreadId || signal != SIGCANCEL) return signal == SIGCANCEL ? 0 : Raise(signal);
        if (!threads.TryGetValue(thread, out Thread? target)) return -ESRCH;
        target.Interrupt();
        return 0;
    }

    /// <summary>set_robust_list: musl releases a dead thread's robust mutexes itself, so the head is only remembered.</summary>
    private static long SetRobustList(nint head, long length)
    {
        if (length != 24) return -EINVAL;
        robustList = head;
        return 0;
    }

    private static long GetRobustList(int thread, nint* head, long* length)
    {
        if (thread != 0 && thread != CurrentThreadId) return -EPERM;
        *head = robustList;
        *length = 24;
        return 0;
    }

    private static long Yield()
    {
        Thread.Yield();
        return 0;
    }

    private static long Affinity(byte* mask, long size)
    {
        int bytes = (int)Math.Min(size, 128);
        if (bytes < 8) return -EINVAL;
        new Span<byte>(mask, bytes).Clear();
        for (int processor = 0; processor < Math.Min(Environment.ProcessorCount, bytes * 8); processor++)
            mask[processor / 8] |= (byte)(1 << (processor % 8));
        return bytes;
    }

    /// <summary>
    /// futex wait and wake on per-address monitors. Requeue operations wake every waiter, which futex semantics
    /// allow (waiters must tolerate spurious wakeups).
    /// </summary>
    private static long Futex(nint address, int operation, int value, Timespec* timeout)
    {
        object queue = futexes.GetOrAdd(address, _ => new object());
        switch (operation & FUTEX_CMD_MASK)
        {
            case FUTEX_WAIT or FUTEX_WAIT_BITSET:
                lock (queue)
                {
                    if (Volatile.Read(ref *(int*)address) != value) return -EAGAIN;
                    if (timeout == null) { Monitor.Wait(queue); return 0; }
                    if (timeout->Duration is not TimeSpan wait) return -EINVAL;
                    // FUTEX_WAIT takes a relative timeout; FUTEX_WAIT_BITSET an absolute monotonic or realtime one.
                    if ((operation & FUTEX_CMD_MASK) == FUTEX_WAIT_BITSET)
                        wait -= (operation & FUTEX_CLOCK_REALTIME) != 0 ? DateTime.UtcNow - DateTime.UnixEpoch : Monotonic;
                    return wait <= TimeSpan.Zero || !Monitor.Wait(queue, wait) ? -ETIMEDOUT : 0;
                }
            case FUTEX_WAKE or FUTEX_WAKE_BITSET:
                lock (queue)
                {
                    if (value == int.MaxValue) Monitor.PulseAll(queue);
                    else for (int index = 0; index < value; index++) Monitor.Pulse(queue);
                }
                return value;
            case FUTEX_REQUEUE or FUTEX_CMP_REQUEUE:
                lock (queue) Monitor.PulseAll(queue);
                return value;
            case FUTEX_LOCK_PI or FUTEX_TRYLOCK_PI:
                return LockPriorityInheritance(address, queue, (operation & FUTEX_CMD_MASK) == FUTEX_TRYLOCK_PI, timeout);
            case FUTEX_UNLOCK_PI:
                lock (queue)
                {
                    int word = Volatile.Read(ref *(int*)address);
                    if ((word & FUTEX_TID_MASK) != CurrentThreadId) return -EPERM;
                    Volatile.Write(ref *(int*)address, 0);
                    Monitor.Pulse(queue);
                }
                return 0;
            default:
                return -ENOSYS;
        }
    }

    /// <summary>
    /// FUTEX_LOCK_PI: the futex word holds the owner's thread id, with FUTEX_WAITERS while others wait. There are no
    /// thread priorities to inherit on .NET, so this is an ordinary owner-tracking mutex. The timeout is absolute (realtime).
    /// </summary>
    private static long LockPriorityInheritance(nint address, object queue, bool tryOnly, Timespec* timeout)
    {
        int tid = CurrentThreadId;
        TimeSpan? deadline = timeout == null ? null : timeout->Duration;
        if (timeout != null && deadline is null) return -EINVAL;
        lock (queue)
            while (true)
            {
                int word = Volatile.Read(ref *(int*)address);
                if ((word & FUTEX_TID_MASK) == 0)
                {
                    int waiters = word & FUTEX_WAITERS;
                    if (Interlocked.CompareExchange(ref *(int*)address, tid | waiters | (word & FUTEX_OWNER_DIED), word) == word) return 0;
                    continue;
                }
                if ((word & FUTEX_TID_MASK) == tid) return -EDEADLK;
                if (tryOnly) return -EAGAIN;
                if ((word & FUTEX_WAITERS) == 0 && Interlocked.CompareExchange(ref *(int*)address, word | FUTEX_WAITERS, word) != word) continue;
                if (deadline is null) Monitor.Wait(queue);
                else
                {
                    TimeSpan remaining = deadline.Value - (DateTime.UtcNow - DateTime.UnixEpoch);
                    if (remaining <= TimeSpan.Zero || !Monitor.Wait(queue, remaining)) return -ETIMEDOUT;
                }
            }
    }

    private sealed class ThreadExit : Exception;

    /// <summary>
    /// musl's __clone for pthread_create (LC-02): runs <paramref name="function"/>(<paramref name="argument"/>) on a new
    /// managed thread with CLONE_SETTLS, CLONE_PARENT_SETTID and CLONE_CHILD_CLEARTID semantics. SYS_exit on that
    /// thread unwinds to here, then the child-tid word is cleared and its futex woken, as the kernel does.
    /// </summary>
    [CExport("__llvmnet_clone")]
    public static int Clone(nint function, nint argument, int flags, nint parentTid, nint tls, nint childTid)
    {
        int tid = Interlocked.Increment(ref nextThreadId) + 100;
        if ((flags & CLONE_PARENT_SETTID) != 0) Volatile.Write(ref *(int*)parentTid, tid);
        Interlocked.Increment(ref clonedThreads);
        var thread = new Thread(() =>
        {
            threadId = tid;
            clonedThread = true;
            if ((flags & CLONE_SETTLS) != 0) threadPointer = tls;
            try { ((delegate* managed<nint, int>)function)(argument); }
            catch (ThreadExit) { }
            catch (CxxException exception) { Cxx.CallTerminate(exception.Handle); }
            finally
            {
                if ((flags & CLONE_CHILD_CLEARTID) != 0)
                {
                    Volatile.Write(ref *(int*)childTid, 0);
                    Futex(childTid, FUTEX_WAKE, 1, null);
                }
                threads.TryRemove(tid, out _);
                Interlocked.Decrement(ref clonedThreads);
            }
        }, 8 << 20)
        { IsBackground = true, Name = $"musl thread {tid}" };
        // Registered before it runs, so pthread_cancel right after pthread_create finds it.
        threads[tid] = thread;
        thread.Start();
        return tid;
    }

    private static long ExitThread(int status)
    {
        if (clonedThread) throw new ThreadExit();
        // pthread_exit on the main thread: the process ends once every other thread has exited.
        SpinWait.SpinUntil(() => Volatile.Read(ref clonedThreads) == 0);
        return ExitProcess(status);
    }
}

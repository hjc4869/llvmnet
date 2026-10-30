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

    [CExport("__llvmnet_get_tp")]
    public static long GetThreadPointer() => threadPointer;

    internal static int CurrentThreadId => threadId != 0 ? threadId : threadId = Interlocked.Increment(ref nextThreadId) + 100;

    private static long SetThreadPointer(nint pointer)
    {
        threadPointer = pointer;
        // The first thread pointer is the main thread's; .NET signal threads borrow it to run handlers.
        Interlocked.CompareExchange(ref processThreadPointer, pointer, 0);
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
            default:
                return -ENOSYS;
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
            finally
            {
                if ((flags & CLONE_CHILD_CLEARTID) != 0)
                {
                    Volatile.Write(ref *(int*)childTid, 0);
                    Futex(childTid, FUTEX_WAKE, 1, null);
                }
                Interlocked.Decrement(ref clonedThreads);
            }
        }, 8 << 20)
        { IsBackground = true, Name = $"musl thread {tid}" };
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

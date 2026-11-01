using System.Collections.Concurrent;
using static LlvmNet.Runtime.Linux;

namespace LlvmNet.Runtime;

/// <summary>Threads for musl's pthreads (ADR-07): thread pointers, clone on managed threads, and futexes.</summary>
public static unsafe partial class HostSyscalls
{
    [ThreadStatic] private static nint threadPointer;
    [ThreadStatic] private static int threadId;
    [ThreadStatic] private static bool clonedThread;
    private static nint processThreadPointer;
    private static int nextThreadId = 1;
    private static int clonedThreads;
    private static readonly ConcurrentDictionary<nint, object> priorityFutexes = new();
    private static bool priorityFutexesUsed;
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
    /// tkill and tgkill. SIGCANCEL for another thread interrupts its blocking call, which returns -EINTR so its
    /// cancellation point acts (ports/musl/src/thread/llvmnet/pthread_cancel.c); other signals are raised on the calling thread.
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
    /// A thread blocked in a futex wait, queued in the bucket of the address it waits on. Each thread has one and reuses
    /// it for every wait; a wake takes it off the queue before signaling it.
    /// </summary>
    private sealed class FutexWaiter
    {
        internal nint Address;
        internal uint Bitset;
        internal FutexWaiter? Next, Previous;
        internal FutexWaiter? NextWoken;            // the list of waiters one wake signals once it releases the bucket
        internal bool Queued;
        internal readonly ManualResetEventSlim Woken = new(false);
    }

    /// <summary>
    /// The waiters of every address that hashes to this bucket, in arrival order, under a spin lock: the critical
    /// sections are a few pointer updates, and a monitor's adaptive spinning cost more than the futex work itself when
    /// four threads contend for one mutex. Woken waiters are signaled after the lock is released.
    /// </summary>
    private sealed class FutexBucket
    {
        internal FutexWaiter? Head, Tail;
        private int locked;

        /// <summary>
        /// Spins and yields, never sleeps: Thread.Sleep would deliver a pending pthread_cancel interrupt here, and an
        /// interrupted wake would leave its waiters blocked.
        /// </summary>
        internal void Enter()
        {
            if (Interlocked.CompareExchange(ref locked, 1, 0) == 0) return;
            for (int spin = 0; Volatile.Read(ref locked) != 0 || Interlocked.CompareExchange(ref locked, 1, 0) != 0; spin++)
                if (spin < 10) Thread.SpinWait(1 << spin);
                else Thread.Yield();
        }

        internal void Exit() => Volatile.Write(ref locked, 0);

        internal void Enqueue(FutexWaiter waiter)
        {
            waiter.Previous = Tail;
            waiter.Next = null;
            if (Tail is null) Head = waiter;
            else Tail.Next = waiter;
            Tail = waiter;
            waiter.Queued = true;
        }

        internal void Remove(FutexWaiter waiter)
        {
            if (waiter.Previous is null) Head = waiter.Next;
            else waiter.Previous.Next = waiter.Next;
            if (waiter.Next is null) Tail = waiter.Previous;
            else waiter.Next.Previous = waiter.Previous;
            waiter.Next = waiter.Previous = null;
            waiter.Queued = false;
        }
    }

    private static readonly FutexBucket[] futexBuckets = NewBuckets();

    private static FutexBucket[] NewBuckets()
    {
        var buckets = new FutexBucket[256];
        for (int index = 0; index < buckets.Length; index++) buckets[index] = new FutexBucket();
        return buckets;
    }
    [ThreadStatic] private static FutexWaiter? futexWaiter;

    /// <summary>Fibonacci hashing of the address's 4-byte word into 256 buckets.</summary>
    private static int BucketIndex(nint address) => (int)(((ulong)address >> 2) * 0x9E3779B97F4A7C15ul >> 56);

    private static FutexBucket BucketOf(nint address) => futexBuckets[BucketIndex(address)];

    /// <summary>
    /// futex(2). Waits, wakes and requeues work as in the kernel: a waiter queues in one of 256 buckets chosen by its
    /// address and blocks on its own event; a wake takes matching waiters off the queue and signals them, so a woken
    /// thread leaves without contending for the bucket, and the result is the number woken; a requeue moves waiters to
    /// the queue of the second address. No state per address outlives a wait. The priority-inheritance operations keep
    /// their owner-tracking monitors (<see cref="LockPriorityInheritance"/>).
    /// </summary>
    private static long Futex(nint address, int operation, int value, Timespec* timeout, nint address2, int value3)
    {
        int command = operation & FUTEX_CMD_MASK;
        switch (command)
        {
            case FUTEX_WAIT or FUTEX_WAIT_BITSET:
                return FutexWait(address, value, command == FUTEX_WAIT ? uint.MaxValue : (uint)value3, timeout,
                    command == FUTEX_WAIT_BITSET, (operation & FUTEX_CLOCK_REALTIME) != 0);
            case FUTEX_WAKE or FUTEX_WAKE_BITSET:
                return FutexWake(address, value, command == FUTEX_WAKE ? uint.MaxValue : (uint)value3);
            case FUTEX_REQUEUE or FUTEX_CMP_REQUEUE:
                return Requeue(address, address2, value, (int)(nint)timeout, command == FUTEX_CMP_REQUEUE ? value3 : null);
            case FUTEX_LOCK_PI or FUTEX_TRYLOCK_PI:
                Volatile.Write(ref priorityFutexesUsed, true);
                return LockPriorityInheritance(address, priorityFutexes.GetOrAdd(address, _ => new object()), command == FUTEX_TRYLOCK_PI, timeout);
            case FUTEX_UNLOCK_PI:
                return UnlockPriorityInheritance(address, priorityFutexes.GetOrAdd(address, _ => new object()));
            default:
                return -ENOSYS;
        }
    }

    /// <summary>Wakes up to <paramref name="count"/> waiters on the address (at least one, as the kernel does).</summary>
    private static long FutexWake(nint address, int count, uint bitset)
    {
        if (bitset == 0) return -EINVAL;
        // A dead thread's robust priority-inheritance mutex: musl marks it owner-died and wakes it with FUTEX_WAKE,
        // where the kernel would act on the robust list itself, so the FUTEX_LOCK_PI waiters are woken to retry.
        if (Volatile.Read(ref priorityFutexesUsed) && priorityFutexes.TryGetValue(address, out object? queue))
            lock (queue) Monitor.PulseAll(queue);
        FutexBucket bucket = BucketOf(address);
        FutexWaiter? woken = null;
        int number = 0;
        bucket.Enter();
        try
        {
            for (FutexWaiter? waiter = bucket.Head; waiter is not null && number < Math.Max(count, 1);)
            {
                FutexWaiter? next = waiter.Next;
                if (waiter.Address == address && (waiter.Bitset & bitset) != 0)
                {
                    bucket.Remove(waiter);
                    waiter.NextWoken = woken;
                    woken = waiter;
                    number++;
                }
                waiter = next;
            }
        }
        finally { bucket.Exit(); }
        Signal(woken);
        return number;
    }

    /// <summary>Signals the waiters a wake took off their queue; each has stopped waiting only once its event is set.</summary>
    private static void Signal(FutexWaiter? woken)
    {
        while (woken is not null)
        {
            FutexWaiter next = woken.NextWoken!;
            woken.NextWoken = null;
            woken.Woken.Set();
            woken = next;
        }
    }

    private static long FutexWait(nint address, int value, uint bitset, Timespec* timeout, bool absolute, bool realtime)
    {
        if (bitset == 0) return -EINVAL;
        long deadline = long.MaxValue;
        if (timeout != null)
        {
            if (timeout->Duration is not TimeSpan wait) return -EINVAL;
            // FUTEX_WAIT takes a relative timeout; FUTEX_WAIT_BITSET an absolute monotonic or realtime one.
            if (absolute) wait -= realtime ? DateTime.UtcNow - DateTime.UnixEpoch : Monotonic;
            deadline = DeadlineAfter(wait);
        }
        FutexWaiter waiter = futexWaiter ??= new FutexWaiter();
        FutexBucket bucket = BucketOf(address);
        bucket.Enter();
        try
        {
            if (Volatile.Read(ref *(int*)address) != value) return -EAGAIN;
            waiter.Address = address;
            waiter.Bitset = bitset;
            bucket.Enqueue(waiter);
        }
        finally { bucket.Exit(); }
        try
        {
            if (deadline == long.MaxValue) waiter.Woken.Wait();
            else
                for (int milliseconds; !waiter.Woken.IsSet && (milliseconds = MillisecondsUntil(deadline)) > 0;)
                    waiter.Woken.Wait(milliseconds);
        }
        catch (ThreadInterruptedException)
        {
            // pthread_cancel interrupted the wait: -EINTR, unless a wake took the waiter first, which then counts.
            if (Dequeue(waiter)) throw;
        }
        if (!waiter.Woken.IsSet && Dequeue(waiter)) return -ETIMEDOUT;
        // A wake took the waiter off the queue; its signal follows as soon as the waker releases the bucket. This
        // wait is not interruptible, so a cancellation arriving now cannot leave the event set for the next wait.
        while (!waiter.Woken.IsSet) Thread.Yield();
        waiter.Woken.Reset();
        return 0;
    }

    /// <summary>The waiters queued on an address, for tests (tests/LlvmNet.UnitTests/HostFutexTests.cs).</summary>
    internal static int QueuedFutexWaiters(nint address)
    {
        FutexBucket bucket = BucketOf(address);
        int count = 0;
        bucket.Enter();
        try
        {
            for (FutexWaiter? waiter = bucket.Head; waiter is not null; waiter = waiter.Next)
                if (waiter.Address == address) count++;
        }
        finally { bucket.Exit(); }
        return count;
    }

    /// <summary>Takes a waiter that stopped waiting off its queue; false when a wake already did. A requeue may have moved it.</summary>
    private static bool Dequeue(FutexWaiter waiter)
    {
        while (true)
        {
            FutexBucket bucket = BucketOf(Volatile.Read(ref waiter.Address));
            bucket.Enter();
            try
            {
                if (BucketOf(waiter.Address) != bucket) continue;
                if (!waiter.Queued) return false;
                bucket.Remove(waiter);
                return true;
            }
            finally { bucket.Exit(); }
        }
    }

    /// <summary>
    /// FUTEX_REQUEUE and FUTEX_CMP_REQUEUE: wakes up to <paramref name="wake"/> waiters on the first address and moves
    /// up to <paramref name="move"/> more to the second; the compare form first checks the first word.
    /// </summary>
    private static long Requeue(nint address, nint target, int wake, int move, int? expected)
    {
        if (wake < 0 || move < 0) return -EINVAL;
        if (target == address) move = 0;
        FutexBucket from = BucketOf(address), to = BucketOf(target);
        // Two buckets lock in index order, so concurrent requeues cannot deadlock.
        FutexBucket first = BucketIndex(address) <= BucketIndex(target) ? from : to, second = first == from ? to : from;
        FutexWaiter? signaled = null;
        int woken = 0, moved = 0;
        first.Enter();
        if (second != first) second.Enter();
        try
        {
            if (expected is int compare && Volatile.Read(ref *(int*)address) != compare) return -EAGAIN;
            // Waiters moved within one bucket go behind the end seen here and are not visited again.
            FutexWaiter? last = from.Tail;
            for (FutexWaiter? waiter = from.Head; waiter is not null && (woken < wake || moved < move);)
            {
                FutexWaiter? next = waiter == last ? null : waiter.Next;
                if (waiter.Address == address)
                {
                    from.Remove(waiter);
                    if (woken < wake)
                    {
                        waiter.NextWoken = signaled;
                        signaled = waiter;
                        woken++;
                    }
                    else
                    {
                        waiter.Address = target;
                        to.Enqueue(waiter);
                        moved++;
                    }
                }
                waiter = next;
            }
        }
        finally
        {
            if (second != first) second.Exit();
            first.Exit();
        }
        Signal(signaled);
        return expected is null ? woken : woken + moved;
    }

    /// <summary>
    /// FUTEX_LOCK_PI: the futex word holds the owner's thread id, with FUTEX_WAITERS while others wait. There are no
    /// thread priorities to inherit on .NET, so this is an ordinary owner-tracking mutex. The timeout is absolute (realtime).
    /// </summary>
    private static long LockPriorityInheritance(nint address, object queue, bool tryOnly, Timespec* timeout)
    {
        int tid = CurrentThreadId;
        TimeSpan? absolute = timeout == null ? null : timeout->Duration;
        if (timeout != null && absolute is null) return -EINVAL;
        long deadline = absolute is TimeSpan end ? DeadlineAfter(end - (DateTime.UtcNow - DateTime.UnixEpoch)) : long.MaxValue;
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
                if (absolute is null) Monitor.Wait(queue);
                else
                {
                    int milliseconds = MillisecondsUntil(deadline);
                    if (milliseconds == 0) return -ETIMEDOUT;
                    Monitor.Wait(queue, milliseconds);
                }
            }
    }

    /// <summary>FUTEX_UNLOCK_PI: only the owner releases; one waiter wakes to compete for the word.</summary>
    private static long UnlockPriorityInheritance(nint address, object queue)
    {
        lock (queue)
        {
            int word = Volatile.Read(ref *(int*)address);
            if ((word & FUTEX_TID_MASK) != CurrentThreadId) return -EPERM;
            Volatile.Write(ref *(int*)address, 0);
            Monitor.Pulse(queue);
        }
        return 0;
    }

    private sealed class ThreadExit : Exception;

    /// <summary>The managed stack every thread gets at least: glibc's default on Linux x86-64, which musl's 128 KB would undercut.</summary>
    private const long DefaultStackSize = 8L << 20;
    /// <summary>The largest stack honored per thread; a request beyond it is a program sizing its threads for a 64-bit address space.</summary>
    private const long MaximumStackSize = 1L << 30;

    /// <summary>
    /// The stack size a thread asked for: the length of the mapping musl allocated for its stack (guard, stack and
    /// TLS), when the stack pointer lies in one; the thread gets at least the default, so musl's own small default and
    /// requests below it do not shrink the managed stack, while pthread_attr_setstacksize and OMP_STACKSIZE grow it.
    /// </summary>
    private static int StackSizeFor(nint stack)
    {
        long requested = 0;
        if (stack != 0)
            lock (mappingGate)
                requested = MappingAt(stack - 1)?.Length ?? 0;
        return (int)Math.Clamp(requested, DefaultStackSize, MaximumStackSize);
    }

    /// <summary>
    /// musl's __clone for pthread_create: runs <paramref name="function"/>(<paramref name="argument"/>) on a new
    /// managed thread with CLONE_SETTLS, CLONE_PARENT_SETTID and CLONE_CHILD_CLEARTID semantics, with a stack sized
    /// by <see cref="StackSizeFor"/>. SYS_exit on that thread unwinds to here, then the child-tid word is cleared and
    /// its futex woken, as the kernel does.
    /// </summary>
    [CExport("__llvmnet_clone")]
    public static int Clone(nint function, nint argument, int flags, nint parentTid, nint tls, nint childTid, nint stack)
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
                    Futex(childTid, FUTEX_WAKE, 1, null, 0, 0);
                }
                threads.TryRemove(tid, out _);
                StackMemory.ReleaseThread();
                Interlocked.Decrement(ref clonedThreads);
            }
        }, StackSizeFor(stack))
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

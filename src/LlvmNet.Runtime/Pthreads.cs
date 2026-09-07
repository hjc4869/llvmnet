using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class Pthreads
{
    private sealed class MutexState(int kind)
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal readonly int Kind = kind;
        internal int Owner;
        internal int Depth;
    }
    private sealed class ConditionState(int clock)
    {
        internal readonly object Gate = new();
        internal readonly LinkedList<ManualResetEventSlim> Waiters = [];
        internal readonly int Clock = clock;
    }
    private sealed class ThreadState
    {
        internal Thread Thread = null!;
        internal nint Result;
        internal ExceptionDispatchInfo? Failure;
        internal bool Detached;
    }

    private static readonly ConcurrentDictionary<nint, MutexState> mutexes = [];
    private static readonly ConcurrentDictionary<nint, ConditionState> conditions = [];
    private static readonly ConcurrentDictionary<nint, object> onceLocks = [];
    private static readonly ConcurrentDictionary<long, ThreadState> threads = [];
    private static readonly ConcurrentDictionary<int, nint> keyDestructors = [];
    [ThreadStatic] private static Dictionary<int, nint>? keyValues;
    private static int nextKey;

    [CExport("pthread_mutex_init")]
    public static int MutexInit(nint mutex, nint attributes)
    {
        mutexes[mutex] = new MutexState(attributes == 0 ? 0 : *(int*)attributes);
        return 0;
    }
    [CExport("pthread_mutex_destroy")]
    public static int MutexDestroy(nint mutex)
    {
        if (mutexes.TryGetValue(mutex, out MutexState? state) && Volatile.Read(ref state.Owner) != 0)
            return 16;
        if (mutexes.TryRemove(mutex, out state)) state.Semaphore.Dispose();
        return 0;
    }
    [CExport("pthread_mutex_lock")]
    public static int MutexLock(nint mutex) => Lock(mutex, false);
    [CExport("pthread_mutex_trylock")]
    public static int MutexTrylock(nint mutex) => Lock(mutex, true);
    private static int Lock(nint mutex, bool attempt)
    {
        MutexState state = mutexes.GetOrAdd(mutex, _ => new MutexState(0));
        int current = Environment.CurrentManagedThreadId;
        if (Volatile.Read(ref state.Owner) == current)
        {
            if (state.Kind == 1) { state.Depth++; return 0; }
            if (attempt) return 16;
            if (state.Kind == 2) return 35;
        }
        if (attempt)
        {
            if (!state.Semaphore.Wait(0)) return 16;
        }
        else state.Semaphore.Wait();
        state.Depth = 1;
        Volatile.Write(ref state.Owner, current);
        return 0;
    }
    [CExport("pthread_mutex_unlock")]
    public static int MutexUnlock(nint mutex)
    {
        if (!mutexes.TryGetValue(mutex, out MutexState? state) || Volatile.Read(ref state.Owner) != Environment.CurrentManagedThreadId)
            return 1;
        if (--state.Depth != 0) return 0;
        Volatile.Write(ref state.Owner, 0);
        state.Semaphore.Release();
        return 0;
    }
    [CExport("pthread_mutexattr_init")]
    public static int MutexAttrInit(nint attributes) { *(int*)attributes = 0; return 0; }
    [CExport("pthread_mutexattr_settype")]
    public static int MutexAttrSettype(nint attributes, int kind)
    {
        if (kind is < 0 or > 2) return 22;
        *(int*)attributes = kind;
        return 0;
    }
    [CExport("pthread_mutexattr_destroy"), CExport("pthread_condattr_destroy")]
    public static int AttributeDestroy(nint attributes) => 0;

    [CExport("pthread_cond_init")]
    public static int ConditionInit(nint condition, nint attributes)
    {
        conditions[condition] = new ConditionState(attributes == 0 ? 0 : *(int*)attributes);
        return 0;
    }
    [CExport("pthread_cond_destroy")]
    public static int ConditionDestroy(nint condition)
    {
        if (!conditions.TryGetValue(condition, out ConditionState? state)) return 0;
        lock (state.Gate)
        {
            if (state.Waiters.Count != 0) return 16;
            conditions.TryRemove(condition, out _);
            return 0;
        }
    }
    [CExport("pthread_cond_wait")]
    public static int ConditionWait(nint condition, nint mutex) => Wait(condition, mutex, 0);
    [CExport("pthread_cond_timedwait")]
    public static int ConditionTimedWait(nint condition, nint mutex, nint deadline) => Wait(condition, mutex, deadline);
    private static int Wait(nint condition, nint mutex, nint deadline)
    {
        ConditionState state = conditions.GetOrAdd(condition, _ => new ConditionState(0));
        using var signal = new ManualResetEventSlim();
        LinkedListNode<ManualResetEventSlim> node;
        lock (state.Gate)
        {
            node = state.Waiters.AddLast(signal);
            int unlock = MutexUnlock(mutex);
            if (unlock != 0) { state.Waiters.Remove(node); return unlock; }
        }
        bool awakened;
        if (deadline == 0)
        {
            signal.Wait();
            awakened = true;
        }
        else
        {
            long* now = stackalloc long[2];
            CTime.ClockGettime(state.Clock, (nint)now);
            long milliseconds = Math.Max(0, (*(long*)deadline - now[0]) * 1000 + (*(long*)(deadline + 8) - now[1]) / 1_000_000);
            awakened = signal.Wait((int)Math.Min(milliseconds, int.MaxValue));
        }
        lock (state.Gate)
        {
            if (node.List is not null) state.Waiters.Remove(node);
            else awakened = true;
        }
        int result = MutexLock(mutex);
        return result != 0 ? result : awakened ? 0 : 110;
    }
    [CExport("pthread_cond_signal")]
    public static int ConditionSignal(nint condition) => Signal(condition, false);
    [CExport("pthread_cond_broadcast")]
    public static int ConditionBroadcast(nint condition) => Signal(condition, true);
    private static int Signal(nint condition, bool all)
    {
        ConditionState state = conditions.GetOrAdd(condition, _ => new ConditionState(0));
        lock (state.Gate)
        {
            do
            {
                LinkedListNode<ManualResetEventSlim>? first = state.Waiters.First;
                if (first is null) break;
                state.Waiters.RemoveFirst();
                first.Value.Set();
            } while (all);
        }
        return 0;
    }
    [CExport("pthread_condattr_init")]
    public static int ConditionAttrInit(nint attributes) { *(int*)attributes = 0; return 0; }
    [CExport("pthread_condattr_setclock")]
    public static int ConditionAttrClock(nint attributes, int clock)
    {
        if (clock is not (0 or 1)) return 22;
        *(int*)attributes = clock;
        return 0;
    }

    [CExport("pthread_once")]
    public static int Once(nint control, nint routine)
    {
        lock (onceLocks.GetOrAdd(control, _ => new object()))
        {
            if (*(int*)control == 0)
            {
                ((delegate* managed<void>)routine)();
                *(int*)control = 1;
            }
        }
        return 0;
    }
    [CExport("pthread_attr_init")]
    public static int AttrInit(nint attributes) { NativeMemory.Clear((void*)attributes, 56); return 0; }
    [CExport("pthread_attr_destroy")]
    public static int AttrDestroy(nint attributes) => 0;
    [CExport("pthread_attr_setstacksize")]
    public static int AttrStackSize(nint attributes, long size)
    {
        if (size < 16384 || size > int.MaxValue) return 22;
        *(long*)attributes = size;
        return 0;
    }
    [CExport("pthread_attr_setdetachstate")]
    public static int AttrDetach(nint attributes, int detached)
    {
        if (detached is not (0 or 1)) return 22;
        *(int*)(attributes + 8) = detached;
        return 0;
    }
    [CExport("pthread_create")]
    public static int Create(nint identifier, nint attributes, nint routine, nint argument)
    {
        try
        {
            var state = new ThreadState { Detached = attributes != 0 && *(int*)(attributes + 8) != 0 };
            int stackSize = attributes == 0 ? 0 : checked((int)*(long*)attributes);
            state.Thread = new Thread(() =>
            {
                try { state.Result = ((delegate* managed<nint, nint>)routine)(argument); }
                catch (Exception error) { state.Failure = ExceptionDispatchInfo.Capture(error); }
                finally
                {
                    RunKeyDestructors();
                    if (state.Detached) threads.TryRemove(state.Thread.ManagedThreadId, out _);
                }
            }, stackSize) { IsBackground = true };
            long threadId = state.Thread.ManagedThreadId;
            threads[threadId] = state;
            *(long*)identifier = threadId;
            state.Thread.Start();
            return 0;
        }
        catch (OutOfMemoryException) { return 11; }
    }
    [CExport("pthread_join")]
    public static int Join(long identifier, nint result)
    {
        if (!threads.TryGetValue(identifier, out ThreadState? state)) return 3;
        if (state.Detached) return 22;
        if (identifier == Environment.CurrentManagedThreadId) return 35;
        state.Thread.Join();
        if (result != 0) *(nint*)result = state.Result;
        threads.TryRemove(identifier, out _);
        state.Failure?.Throw();
        return 0;
    }
    [CExport("pthread_detach")]
    public static int Detach(long identifier)
    {
        if (!threads.TryGetValue(identifier, out ThreadState? state)) return 3;
        state.Detached = true;
        if (!state.Thread.IsAlive) threads.TryRemove(identifier, out _);
        return 0;
    }
    [CExport("pthread_self")]
    public static long Self() => Environment.CurrentManagedThreadId;
    [CExport("pthread_equal")]
    public static int Equal(long left, long right) => left == right ? 1 : 0;
    [CExport("sched_yield")]
    public static int Yield() { Thread.Yield(); return 0; }
    [CExport("pthread_key_create")]
    public static int KeyCreate(nint key, nint destructor)
    {
        int identifier = Interlocked.Increment(ref nextKey);
        keyDestructors[identifier] = destructor;
        *(int*)key = identifier;
        return 0;
    }
    [CExport("pthread_key_delete")]
    public static int KeyDelete(int key) => keyDestructors.TryRemove(key, out _) ? 0 : 22;
    [CExport("pthread_getspecific")]
    public static nint Getspecific(int key) => keyValues is not null && keyValues.TryGetValue(key, out nint value) ? value : 0;
    [CExport("pthread_setspecific")]
    public static int Setspecific(int key, nint value)
    {
        if (!keyDestructors.ContainsKey(key)) return 22;
        (keyValues ??= [])[key] = value;
        return 0;
    }
    private static void RunKeyDestructors()
    {
        if (keyValues is null) return;
        for (int pass = 0; pass < 4; pass++)
        foreach ((int key, nint value) in keyValues.ToArray())
        {
            if (value == 0 || !keyDestructors.TryGetValue(key, out nint destructor) || destructor == 0) continue;
            keyValues[key] = 0;
            ((delegate* managed<nint, void>)destructor)(value);
        }
    }

    [CExport("prctl")]
    public static int Prctl(int option, nint arguments)
    {
        VarArgs values = *(VarArgs*)arguments;
        if (option == 15)
        {
            Thread.CurrentThread.Name = CString.Text((nint)values.Integer());
            return 0;
        }
        return ProcessRuntime.Error(38);
    }
}
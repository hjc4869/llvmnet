using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public sealed class CxxException : Exception
{
    public nint Object { get; }
    public nint TypeInfo { get; }
    public nint Destructor { get; }
    public nint Handle { get; }
    internal int CatchDepth;
    internal bool Rethrown;
    internal nint CaughtObject;
    internal CxxException Primary { get; }
    internal int References = 1;

    internal CxxException(nint value, nint typeInfo, nint destructor, bool raised = true) : base($"C++ exception: {Cxx.TypeName(typeInfo)}")
    {
        Object = value;
        CaughtObject = value;
        TypeInfo = typeInfo;
        Destructor = destructor;
        Primary = this;
        References = raised ? 1 : 0;
        Handle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        if (raised) Cxx.ExceptionCreated();
    }

    internal CxxException(CxxException original) : base(original.Message)
    {
        Primary = original.Primary;
        Interlocked.Increment(ref Primary.References);
        Object = Primary.Object;
        CaughtObject = Object;
        TypeInfo = Primary.TypeInfo;
        Destructor = Primary.Destructor;
        Handle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
        Cxx.ExceptionCreated();
    }
}

public static unsafe class Cxx
{
    private static readonly ConcurrentDictionary<string, nint> typeGlobals = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<nint, object> guards = [];
    private static readonly ConcurrentDictionary<nint, CxxException> initializedExceptions = [];
    private static readonly Dictionary<nint, int> typeIds = [];
    private static readonly Stack<(nint Callback, nint Argument, nint Module)> destructors = [];
    private static readonly nint defaultTerminate = SystemAbi.RegisterCallback((nint)(delegate* unmanaged[Cdecl]<void>)&NativeDefaultTerminate, (nint)(delegate* managed<void>)&DefaultTerminate);
    private static nint terminateHandler = defaultTerminate;
    [ThreadStatic] private static Stack<CxxException>? catches;
    [ThreadStatic] private static int uncaughtExceptions;
    private static int nextTypeId;

    internal static void ExceptionCreated() => uncaughtExceptions++;
    [CExport("_ZSt18uncaught_exceptionv")]
    public static int UncaughtException() => uncaughtExceptions != 0 ? 1 : 0;
    [CExport("_ZSt19uncaught_exceptionsv"), CExport("_ZNSt3__119uncaught_exceptionsEv")]
    public static int UncaughtExceptions() => uncaughtExceptions;

    public static bool HasGlobal(string name) => name.StartsWith("_ZTI", StringComparison.Ordinal) || name.StartsWith("_ZTVN10__cxxabiv1", StringComparison.Ordinal) || name is "__dso_handle" or "_ZSt7nothrow" or "_ZNSt3__17nothrowE";
    public static nint Global(string name) => typeGlobals.GetOrAdd(name, CreateGlobal);
    private static nint CreateGlobal(string name)
    {
        nint address = Memory.Allocate(128, 8);
        if (name.StartsWith("_ZTI", StringComparison.Ordinal))
            *(nint*)(address + 8) = Marshal.StringToCoTaskMemUTF8(name[4..]);
        return address;
    }
    public static string TypeName(nint typeInfo) => typeInfo == 0 ? "unknown" : CString.Text(*(nint*)(typeInfo + 8));
    public static int TypeId(nint typeInfo)
    {
        lock (typeIds)
        {
            if (!typeIds.TryGetValue(typeInfo, out int identifier))
                typeIds[typeInfo] = identifier = ++nextTypeId;
            return identifier;
        }
    }
    private static bool SameType(nint actual, nint requested)
    {
        if (actual == requested)
            return true;
        if (actual == 0 || requested == 0)
            return false;
        return TypeName(actual) == TypeName(requested);
    }
    private static List<nint> Bases(nint actual, nint value, nint requested)
    {
        HashSet<(nint Type, nint Object)> visited = [];
        HashSet<nint> found = [];
        Visit(actual, value);
        return found.ToList();

        void Visit(nint type, nint instance)
        {
            if (type == 0 || !visited.Add((type, instance))) return;
            if (SameType(type, requested)) found.Add(instance);
            nint vtable = *(nint*)type;
            if (vtable == Global("_ZTVN10__cxxabiv120__si_class_type_infoE") + 16)
                Visit(*(nint*)(type + 16), instance);
            else if (vtable == Global("_ZTVN10__cxxabiv121__vmi_class_type_infoE") + 16)
            {
                int count = *(int*)(type + 20);
                if (count is < 0 or > 1024) throw new InvalidOperationException("Invalid C++ RTTI base count.");
                for (int index = 0; index < count; index++)
                {
                    nint entry = type + 24 + index * 16;
                    long flags = *(long*)(entry + 8);
                    if ((flags & 2) == 0) continue;
                    long offset = flags >> 8;
                    if ((flags & 1) != 0)
                        offset = *(long*)(*(nint*)instance + (nint)offset);
                    Visit(*(nint*)entry, instance + (nint)offset);
                }
            }
        }
    }
    public static int Selector(CxxException exception, nint[] clauses)
    {
        foreach (nint clause in clauses)
        {
            List<nint> matches = clause == 0 ? [exception.Object] : Bases(exception.TypeInfo, exception.Object, clause);
            if (matches.Count == 1)
            {
                exception.CaughtObject = matches[0];
                return clause == 0 ? 1 : TypeId(clause);
            }
        }
        return 0;
    }

    public static int Filter(CxxException exception, nint[] allowed)
    {
        foreach (nint type in allowed)
            if (type == 0 || Bases(exception.TypeInfo, exception.Object, type).Count == 1)
                return 0;
        return -1;
    }

    [CExport("__dynamic_cast")]
    public static nint DynamicCast(nint value, nint sourceType, nint destinationType, long hint)
    {
        if (value == 0) return 0;
        nint vtable = *(nint*)value;
        nint complete = value + (nint)(*(long*)(vtable - 16));
        nint actualType = *(nint*)(vtable - 8);
        List<nint> candidates = Bases(actualType, complete, destinationType);
        List<nint> downcasts = candidates.Where(candidate => Bases(destinationType, candidate, sourceType).Contains(value)).ToList();
        if (downcasts.Count == 1) return downcasts[0];
        if (downcasts.Count == 0 && candidates.Count == 1 && Bases(actualType, complete, sourceType).Contains(value)) return candidates[0];
        return 0;
    }

    [CExport("_Znwm"), CExport("_Znam")]
    public static nint New(long size)
    {
        nint pointer = CString.Malloc(size);
        if (pointer == 0)
            throw new CxxException(AllocateException(8), Global("_ZTISt9bad_alloc"), 0);
        return pointer;
    }
    [CExport("_ZdlPv"), CExport("_ZdaPv")]
    public static void Delete(nint pointer) => CString.Free(pointer);
    [CExport("_ZnwmRKSt9nothrow_t"), CExport("_ZnamRKSt9nothrow_t")]
    public static nint NothrowNew(long size, nint tag) => CString.Malloc(size);
    [CExport("_ZdlPvRKSt9nothrow_t"), CExport("_ZdaPvRKSt9nothrow_t")]
    public static void NothrowDelete(nint pointer, nint tag) => Delete(pointer);
    [CExport("_ZdlPvm"), CExport("_ZdaPvm")]
    public static void SizedDelete(nint pointer, long size) => Delete(pointer);
    [CExport("_ZnwmSt11align_val_t"), CExport("_ZnamSt11align_val_t")]
    public static nint AlignedNew(long size, long alignment)
    {
        if (!AbiContract.Portable) return Memory.Allocate(size, checked((int)alignment));
        nint result = PortableHeap.Allocate(size, alignment);
        if (result == 0) throw new OutOfMemoryException();
        return result;
    }
    [CExport("_ZdlPvSt11align_val_t"), CExport("_ZdaPvSt11align_val_t")]
    public static void AlignedDelete(nint pointer, long alignment)
    {
        if (AbiContract.Portable) PortableHeap.Free(pointer);
        else NativeMemory.AlignedFree((void*)pointer);
    }
    [CExport("_ZdlPvmSt11align_val_t"), CExport("_ZdaPvmSt11align_val_t")]
    public static void AlignedSizedDelete(nint pointer, long size, long alignment) => AlignedDelete(pointer, alignment);

    [CExport("__cxa_allocate_exception")]
    public static nint AllocateException(long size)
    {
        nint pointer = CString.Calloc(1, size);
        if (pointer == 0)
            throw new OutOfMemoryException();
        return pointer;
    }
    [CExport("__cxa_free_exception")]
    public static void FreeException(nint pointer)
    {
        if (initializedExceptions.TryRemove(pointer, out CxxException? initialized))
            GCHandle.FromIntPtr(initialized.Handle).Free();
        CString.Free(pointer);
    }
    [CExport("__cxa_init_primary_exception")]
    public static nint InitializePrimaryException(nint value, nint typeInfo, nint destructor)
    {
        var exception = new CxxException(value, typeInfo, destructor, false);
        if (!initializedExceptions.TryAdd(value, exception))
        {
            GCHandle.FromIntPtr(exception.Handle).Free();
            throw new InvalidOperationException("C++ exception was already initialized.");
        }
        return exception.Handle;
    }
    [CExport("_ZNSt15__exception_ptr13exception_ptrC1EPv"), CExport("_ZNSt15__exception_ptr13exception_ptrC2EPv")]
    public static void ConstructExceptionPointer(nint pointer, nint value)
    {
        if (value == 0) { *(nint*)pointer = 0; return; }
        if (!initializedExceptions.TryRemove(value, out CxxException? exception))
            throw new InvalidOperationException("C++ exception_ptr requires an initialized primary exception.");
        Interlocked.Increment(ref exception.References);
        *(nint*)pointer = exception.Handle;
    }
    [CExport("__cxa_throw")]
    public static void Throw(nint value, nint typeInfo, nint destructor)
    {
        if (initializedExceptions.TryRemove(value, out CxxException? exception))
        {
            exception.References = 1;
            ExceptionCreated();
            throw exception;
        }
        throw new CxxException(value, typeInfo, destructor);
    }
    [CExport("__cxa_get_exception_ptr")]
    public static nint GetExceptionPointer(nint handle) => ((CxxException)GCHandle.FromIntPtr(handle).Target!).CaughtObject;
    [CExport("_ZNSt15__exception_ptr13exception_ptr9_M_addrefEv")]
    public static void ExceptionPointerAddRef(nint pointer)
    {
        nint handle = *(nint*)pointer;
        if (handle != 0)
            Interlocked.Increment(ref ((CxxException)GCHandle.FromIntPtr(handle).Target!).Primary.References);
    }
    [CExport("_ZNSt15__exception_ptr13exception_ptr10_M_releaseEv")]
    public static void ExceptionPointerRelease(nint pointer)
    {
        nint handle = *(nint*)pointer;
        if (handle != 0) ReleaseException((CxxException)GCHandle.FromIntPtr(handle).Target!);
    }
    [CExport("_ZSt17current_exceptionv")]
    public static void CurrentException(nint result)
    {
        CxxException? exception = catches is not null && catches.TryPeek(out CxxException? current) ? current.Primary : null;
        *(nint*)result = exception?.Handle ?? 0;
        if (exception is not null) Interlocked.Increment(ref exception.References);
    }
    [CExport("_ZSt17rethrow_exceptionNSt15__exception_ptr13exception_ptrE")]
    public static void RethrowExceptionPointer(nint pointer)
    {
        nint handle = *(nint*)pointer;
        if (handle == 0) throw new InvalidOperationException("Cannot rethrow an empty C++ exception_ptr.");
        throw new CxxException((CxxException)GCHandle.FromIntPtr(handle).Target!);
    }
    [CExport("_ZNKSt15__exception_ptr13exception_ptr20__cxa_exception_typeEv")]
    public static nint ExceptionPointerType(nint pointer) => ((CxxException)GCHandle.FromIntPtr(*(nint*)pointer).Target!).TypeInfo;

    private static void ReleaseException(CxxException exception)
    {
        CxxException primary = exception.Primary;
        if (Interlocked.Decrement(ref primary.References) != 0) return;
        if (primary.Destructor != 0) InvokeDestructor(primary.Destructor, primary.Object);
        FreeException(primary.Object);
        GCHandle.FromIntPtr(primary.Handle).Free();
    }

    [CExport("__cxa_begin_catch")]
    public static nint BeginCatch(nint handle)
    {
        CxxException exception = (CxxException)GCHandle.FromIntPtr(handle).Target!;
        if (exception.CatchDepth == 0 || exception.Rethrown) uncaughtExceptions--;
        exception.CatchDepth++;
        exception.Rethrown = false;
        (catches ??= []).Push(exception);
        return exception.CaughtObject;
    }
    [CExport("__cxa_end_catch")]
    public static void EndCatch()
    {
        CxxException exception = catches!.Pop();
        if (--exception.CatchDepth == 0 && !exception.Rethrown)
        {
            if (exception.Primary != exception) GCHandle.FromIntPtr(exception.Handle).Free();
            ReleaseException(exception);
        }
    }
    [CExport("__cxa_rethrow")]
    public static void Rethrow()
    {
        CxxException exception = catches!.Peek();
        uncaughtExceptions++;
        exception.Rethrown = true;
        throw exception;
    }
    [CExport("_Unwind_Resume")]
    public static void Resume(nint handle)
    {
        CxxException exception = (CxxException)GCHandle.FromIntPtr(handle).Target!;
        ExceptionDispatchInfo.Capture(exception).Throw();
    }
    [CExport("__cxa_call_unexpected")]
    public static void Unexpected(nint handle) => throw new InvalidOperationException("C++ terminate: unexpected exception escaped a noexcept function.");
    [CExport("__clang_call_terminate")]
    public static void CallTerminate(nint handle)
    {
        BeginCatch(handle);
        Terminate();
    }
    [CExport("_ZSt13set_terminatePFvvE"), CExport("_ZNSt3__113set_terminateEPFvvE")]
    public static nint SetTerminate(nint handler) => Interlocked.Exchange(ref terminateHandler, handler == 0 ? defaultTerminate : handler);
    [CExport("_ZSt13get_terminatev"), CExport("_ZNSt3__113get_terminateEv")]
    public static nint GetTerminate() => Volatile.Read(ref terminateHandler);
    [CExport("__cxa_pure_virtual"), CExport("_ZSt9terminatev"), CExport("_ZNSt3__19terminateEv")]
    public static void Terminate()
    {
        nint callback = GetTerminate();
        try
        {
            nint managed = SystemAbi.ResolveCallback(callback);
            if (managed != 0) ((delegate* managed<void>)managed)();
            else if (AbiContract.SystemCallbacks) ((delegate* unmanaged[Cdecl]<void>)callback)();
            else ((delegate* managed<void>)callback)();
        }
        catch (Exception exception) { Environment.FailFast("C++ terminate handler threw an exception.", exception); }
        Environment.FailFast("C++ terminate handler returned.");
    }
    private static void DefaultTerminate() => Environment.FailFast("C++ terminate called.");
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void NativeDefaultTerminate() => DefaultTerminate();

    [CExport("__cxa_guard_acquire")]
    public static int GuardAcquire(nint guard)
    {
        object gate = guards.GetOrAdd(guard, _ => new object());
        Monitor.Enter(gate);
        if (*(byte*)guard != 0) { Monitor.Exit(gate); return 0; }
        return 1;
    }
    [CExport("__cxa_guard_release")]
    public static void GuardRelease(nint guard) { *(byte*)guard = 1; Monitor.Exit(guards[guard]); }
    [CExport("__cxa_guard_abort")]
    public static void GuardAbort(nint guard) => Monitor.Exit(guards[guard]);
    [CExport("__cxa_atexit")]
    public static int AtExit(nint callback, nint argument, nint module)
    {
        lock (destructors) destructors.Push((callback, argument, module));
        return 0;
    }
    [CExport("__cxa_finalize")]
    public static void FinalizeModule(nint module)
    {
        List<(nint Callback, nint Argument, nint Module)> deferred = [];
        while (true)
        {
            (nint Callback, nint Argument, nint Module) item;
            lock (destructors)
            {
                if (!destructors.TryPop(out item)) break;
            }
            if (module == 0 || item.Module == module)
                InvokeDestructor(item.Callback, item.Argument);
            else deferred.Add(item);
        }
        lock (destructors)
            for (int index = deferred.Count - 1; index >= 0; index--) destructors.Push(deferred[index]);
    }

    private static void InvokeDestructor(nint callback, nint argument)
    {
        nint managed = SystemAbi.ResolveCallback(callback);
        if (managed != 0) ((delegate* managed<nint, void>)managed)(argument);
        else if (AbiContract.SystemCallbacks) ((delegate* unmanaged[Cdecl]<nint, void>)callback)(argument);
        else ((delegate* managed<nint, void>)callback)(argument);
    }

    [CExport("_ZSt20__throw_length_errorPKc")]
    public static void LengthError(nint message) => Throw(New(16), Global("_ZTISt12length_error"), 0);
    [CExport("_ZSt17__throw_bad_allocv")]
    public static void BadAlloc() => Throw(New(8), Global("_ZTISt9bad_alloc"), 0);
    [CExport("_ZSt28__throw_bad_array_new_lengthv")]
    public static void BadArrayLength() => Throw(New(8), Global("_ZTISt20bad_array_new_length"), 0);
    [CExport("_ZSt21__glibcxx_assert_failPKciS0_S0_")]
    public static void AssertFail(nint file, int line, nint function, nint assertion) => ProcessRuntime.AssertFail(assertion, file, line, function);
    [CExport("_ZSt16__throw_bad_castv"), CExport("__cxa_bad_cast")]
    public static void BadCast() => Throw(New(8), Global("_ZTISt8bad_cast"), 0);
    [CExport("_ZSt17__throw_bad_typeidv"), CExport("__cxa_bad_typeid")]
    public static void BadTypeid() => Throw(New(8), Global("_ZTISt10bad_typeid"), 0);
}
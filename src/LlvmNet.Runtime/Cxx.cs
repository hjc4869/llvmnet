using System.Collections.Concurrent;
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

    internal CxxException(nint value, nint typeInfo, nint destructor) : base($"C++ exception: {Cxx.TypeName(typeInfo)}")
    {
        Object = value;
        CaughtObject = value;
        TypeInfo = typeInfo;
        Destructor = destructor;
        Handle = GCHandle.ToIntPtr(GCHandle.Alloc(this));
    }
}

public static unsafe class Cxx
{
    private static readonly ConcurrentDictionary<string, nint> typeGlobals = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<nint, object> guards = [];
    private static readonly Dictionary<nint, int> typeIds = [];
    private static readonly Stack<(nint Callback, nint Argument, nint Module)> destructors = [];
    [ThreadStatic] private static Stack<CxxException>? catches;
    private static int nextTypeId;

    public static bool HasGlobal(string name) => name.StartsWith("_ZTI", StringComparison.Ordinal) || name.StartsWith("_ZTVN10__cxxabiv1", StringComparison.Ordinal) || name == "__dso_handle";
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
    public static void FreeException(nint pointer) => CString.Free(pointer);
    [CExport("__cxa_throw")]
    public static void Throw(nint value, nint typeInfo, nint destructor) => throw new CxxException(value, typeInfo, destructor);
    [CExport("__cxa_begin_catch")]
    public static nint BeginCatch(nint handle)
    {
        CxxException exception = (CxxException)GCHandle.FromIntPtr(handle).Target!;
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
            if (exception.Destructor != 0)
            {
                if (AbiContract.SystemCallbacks) ((delegate* unmanaged[Cdecl]<nint, void>)exception.Destructor)(exception.Object);
                else ((delegate* managed<nint, void>)exception.Destructor)(exception.Object);
            }
            FreeException(exception.Object);
            GCHandle.FromIntPtr(exception.Handle).Free();
        }
    }
    [CExport("__cxa_rethrow")]
    public static void Rethrow()
    {
        CxxException exception = catches!.Peek();
        exception.Rethrown = true;
        throw exception;
    }
    [CExport("_Unwind_Resume")]
    public static void Resume(nint handle)
    {
        CxxException exception = (CxxException)GCHandle.FromIntPtr(handle).Target!;
        ExceptionDispatchInfo.Capture(exception).Throw();
    }
    [CExport("__cxa_call_unexpected"), CExport("__clang_call_terminate")]
    public static void Unexpected(nint handle) => throw new InvalidOperationException("C++ terminate: unexpected exception escaped a noexcept function.");
    [CExport("__cxa_pure_virtual"), CExport("_ZSt9terminatev")]
    public static void Terminate() => throw new InvalidOperationException("C++ terminate or pure virtual call.");

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
            {
                if (AbiContract.SystemCallbacks) ((delegate* unmanaged[Cdecl]<nint, void>)item.Callback)(item.Argument);
                else ((delegate* managed<nint, void>)item.Callback)(item.Argument);
            }
            else deferred.Add(item);
        }
        lock (destructors)
            for (int index = deferred.Count - 1; index >= 0; index--) destructors.Push(deferred[index]);
    }
    [CExport("_ZSt20__throw_length_errorPKc")]
    public static void LengthError(nint message) => Throw(New(16), Global("_ZTISt12length_error"), 0);
    [CExport("_ZSt17__throw_bad_allocv")]
    public static void BadAlloc() => Throw(New(8), Global("_ZTISt9bad_alloc"), 0);
    [CExport("_ZSt28__throw_bad_array_new_lengthv")]
    public static void BadArrayLength() => Throw(New(8), Global("_ZTISt20bad_array_new_length"), 0);
    [CExport("_ZSt21__glibcxx_assert_failPKciS0_S0_")]
    public static void AssertFail(nint file, int line, nint function, nint assertion) => ProcessRuntime.AssertFail(assertion, file, line, function);
    [CExport("_ZSt16__throw_bad_castv")]
    public static void BadCast() => Throw(New(8), Global("_ZTISt8bad_cast"), 0);
    [CExport("_ZSt17__throw_bad_typeidv")]
    public static void BadTypeid() => Throw(New(8), Global("_ZTISt10bad_typeid"), 0);
}
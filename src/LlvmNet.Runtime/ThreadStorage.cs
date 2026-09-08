using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class ThreadStorage
{
    [ThreadStatic] private static List<nint>? allocations;
    [ThreadStatic] private static Stack<(nint Callback, nint Argument)>? destructors;

    public static nint Allocate(long size, int alignment)
    {
        nint pointer = Memory.Allocate(size, alignment);
        try { (allocations ??= []).Add(pointer); }
        catch { NativeMemory.AlignedFree((void*)pointer); throw; }
        return pointer;
    }

    [CExport("__cxa_thread_atexit"), CExport("__cxa_thread_atexit_impl")]
    public static int AtExit(nint callback, nint argument, nint module)
    {
        (destructors ??= []).Push((callback, argument));
        return 0;
    }

    public static void RunDestructors()
    {
        while (destructors is not null && destructors.TryPop(out var destructor))
        {
            nint managed = SystemAbi.ResolveCallback(destructor.Callback);
            if (managed != 0) ((delegate* managed<nint, void>)managed)(destructor.Argument);
            else if (AbiContract.SystemCallbacks) ((delegate* unmanaged[Cdecl]<nint, void>)destructor.Callback)(destructor.Argument);
            else ((delegate* managed<nint, void>)destructor.Callback)(destructor.Argument);
        }
    }

    public static void Release()
    {
        if (allocations is null) return;
        foreach (nint pointer in allocations) NativeMemory.AlignedFree((void*)pointer);
        allocations.Clear();
    }
}
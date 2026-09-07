using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace LlvmNet.Runtime;

public static unsafe class SystemThreads
{
    private sealed record StartState(nint Routine, nint Argument);
    private static readonly ConcurrentDictionary<int, nint> destructors = [];
    private static readonly nint create = SystemAbi.Symbol("libc.so.6", "pthread_create");
    private static readonly nint keyCreate = SystemAbi.Symbol("libc.so.6", "pthread_key_create");
    private static readonly nint keyDelete = SystemAbi.Symbol("libc.so.6", "pthread_key_delete");
    private static readonly nint getSpecific = SystemAbi.Symbol("libc.so.6", "pthread_getspecific");
    private static readonly nint setSpecific = SystemAbi.Symbol("libc.so.6", "pthread_setspecific");

    public static int Create(nint thread, nint attributes, nint routine, nint argument)
    {
        GCHandle state = GCHandle.Alloc(new StartState(routine, argument));
        int result = ((delegate* unmanaged[Cdecl]<nint, nint, nint, nint, int>)create)(thread, attributes, (nint)(delegate* unmanaged[Cdecl]<nint, nint>)&Start, GCHandle.ToIntPtr(state));
        if (result != 0) state.Free();
        return result;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static nint Start(nint context)
    {
        GCHandle handle = GCHandle.FromIntPtr(context);
        var state = (StartState)handle.Target!;
        handle.Free();
        try
        {
            nint result = ((delegate* unmanaged[Cdecl]<nint, nint>)state.Routine)(state.Argument);
            for (int iteration = 0; iteration < 4; iteration++)
            foreach ((int key, nint destructor) in destructors.ToArray())
            {
                nint value = ((delegate* unmanaged[Cdecl]<int, nint>)getSpecific)(key);
                if (value == 0 || destructor == 0) continue;
                ((delegate* unmanaged[Cdecl]<int, nint, int>)setSpecific)(key, 0);
                ((delegate* unmanaged[Cdecl]<nint, void>)destructor)(value);
            }
            return result;
        }
        catch (Exception error)
        {
            Environment.FailFast("An exception escaped a native-ABI pthread callback.", error);
            return 0;
        }
    }

    public static int KeyCreate(nint key, nint destructor)
    {
        int result = ((delegate* unmanaged[Cdecl]<nint, nint, int>)keyCreate)(key, 0);
        if (result == 0) destructors[*(int*)key] = destructor;
        return result;
    }

    public static int KeyDelete(int key)
    {
        int result = ((delegate* unmanaged[Cdecl]<int, int>)keyDelete)(key);
        if (result == 0) destructors.TryRemove(key, out _);
        return result;
    }
}
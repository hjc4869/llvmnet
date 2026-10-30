namespace LlvmNet.Runtime;

/// <summary>
/// Process termination work in native order: atexit and __cxa_atexit handlers share one list and
/// run in reverse registration order, including handlers registered while exiting; .fini_array functions
/// (destructor attributes, llvm.global_dtors) run afterwards, in reverse of their ascending-priority registration.
/// </summary>
internal static unsafe class ExitHandlers
{
    private readonly record struct Handler(nint Callback, nint Argument, nint Module, bool TakesArgument);
    private static readonly List<Handler> handlers = [];
    private static readonly List<nint> finalizers = [];
    private static readonly object gate = new();

    internal static void Register(nint callback, nint argument, nint module, bool takesArgument)
    {
        lock (gate) handlers.Add(new Handler(callback, argument, module, takesArgument));
    }

    internal static void RegisterFinalizer(nint callback)
    {
        lock (gate) finalizers.Add(callback);
    }

    /// <summary>Runs handlers for <paramref name="module"/>, or all handlers when it is zero, as __cxa_finalize does.</summary>
    internal static void Run(nint module)
    {
        while (true)
        {
            Handler handler;
            lock (gate)
            {
                int index = handlers.FindLastIndex(item => module == 0 || item.Module == module);
                if (index < 0) return;
                handler = handlers[index];
                handlers.RemoveAt(index);
            }
            Invoke(handler.Callback, handler.Argument, handler.TakesArgument);
        }
    }

    internal static void RunFinalizers()
    {
        while (true)
        {
            nint callback;
            lock (gate)
            {
                if (finalizers.Count == 0) return;
                callback = finalizers[^1];
                finalizers.RemoveAt(finalizers.Count - 1);
            }
            Invoke(callback, 0, false);
        }
    }

    private static void Invoke(nint callback, nint argument, bool takesArgument)
    {
        nint managed = SystemAbi.ResolveCallback(callback);
        if (takesArgument)
        {
            if (managed != 0) ((delegate* managed<nint, void>)managed)(argument);
            else if (AbiContract.SystemCallbacks) ((delegate* unmanaged[Cdecl]<nint, void>)callback)(argument);
            else ((delegate* managed<nint, void>)callback)(argument);
        }
        else
        {
            if (managed != 0) ((delegate* managed<void>)managed)();
            else if (AbiContract.SystemCallbacks) ((delegate* unmanaged[Cdecl]<void>)callback)();
            else ((delegate* managed<void>)callback)();
        }
    }
}

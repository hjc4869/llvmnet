using System.Diagnostics;
using System.Runtime.InteropServices;
using LlvmNet.Runtime;

namespace LlvmNet.UnitTests;

/// <summary>
/// futex(2) as the host layer implements it for musl (PF-02): the value check, timeouts, wake counts, bitsets and
/// requeue, through the system-call entry point musl uses.
/// </summary>
public sealed unsafe class HostFutexTests : IDisposable
{
    private const long SysFutex = 202;
    private const int Wait = 0, Wake = 1, CompareRequeue = 4, WaitBitset = 9, WakeBitset = 10, Private = 128;
    private const long EAGAIN = 11, ETIMEDOUT = 110;
    private readonly int* words = (int*)NativeMemory.AllocZeroed(2, sizeof(int));

    public void Dispose() => NativeMemory.Free(words);

    private static long Futex(int* word, int operation, long value, long timeout = 0, int* second = null, int value3 = 0) =>
        HostSyscalls.Syscall(SysFutex, (nint)word, operation | Private, value, timeout, (nint)second, value3);

    private readonly long[] results = new long[8];

    /// <summary>Starts threads that wait on the word, and returns once all of them are queued.</summary>
    private Thread[] StartWaiters(int* word, int count, int operation = Wait, int bitset = 0)
    {
        nint address = (nint)word;
        Array.Fill(results, -1);
        Thread[] threads = Enumerable.Range(0, count).Select(index => new Thread(() =>
            results[index] = HostSyscalls.Syscall(SysFutex, address, operation | Private, 0, 0, 0, bitset))).ToArray();
        foreach (Thread thread in threads) thread.Start();
        var clock = Stopwatch.StartNew();
        while (HostSyscalls.QueuedFutexWaiters(address) < count)
        {
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(30), "waiters did not queue");
            Thread.Sleep(1);
        }
        return threads;
    }

    /// <summary>Every waiter returns 0: it was woken, not timed out or refused.</summary>
    private void Join(Thread[] threads)
    {
        foreach (Thread thread in threads) Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        Assert.All(results.Take(threads.Length), result => Assert.Equal(0, result));
    }

    [Fact]
    public void WaitReturnsEagainWhenTheWordDiffers()
    {
        words[0] = 1;
        Assert.Equal(-EAGAIN, Futex(words, Wait, 0));
    }

    [Fact]
    public void TimedWaitLastsAtLeastItsTimeout()
    {
        long* timeout = stackalloc long[2] { 0, 30_000_000 };
        var clock = Stopwatch.StartNew();
        Assert.Equal(-ETIMEDOUT, Futex(words, Wait, 0, (long)timeout));
        Assert.True(clock.Elapsed >= TimeSpan.FromMilliseconds(30));
        Assert.Equal(0, HostSyscalls.QueuedFutexWaiters((nint)words));
    }

    [Fact]
    public void WakeCountsTheWaitersItWoke()
    {
        Assert.Equal(0, Futex(words, Wake, 1));
        Thread[] threads = StartWaiters(words, 3);
        Assert.Equal(2, Futex(words, Wake, 2));
        Assert.Equal(1, Futex(words, Wake, int.MaxValue));
        Join(threads);
    }

    [Fact]
    public void WakeBitsetWakesOnlyMatchingWaiters()
    {
        Thread[] threads = StartWaiters(words, 1, WaitBitset, 0b01);
        Assert.Equal(0, Futex(words, WakeBitset, int.MaxValue, value3: 0b10));
        Assert.Equal(1, Futex(words, WakeBitset, int.MaxValue, value3: 0b11));
        Join(threads);
    }

    [Fact]
    public void CompareRequeueMovesWaitersToTheSecondWord()
    {
        Thread[] threads = StartWaiters(words, 2);
        Assert.Equal(-EAGAIN, Futex(words, CompareRequeue, 0, 2, words + 1, value3: 5));
        Assert.Equal(2, Futex(words, CompareRequeue, 0, 2, words + 1, value3: 0));
        Assert.Equal(0, Futex(words, Wake, int.MaxValue));
        Assert.Equal(2, Futex(words + 1, Wake, int.MaxValue));
        Join(threads);
    }
}

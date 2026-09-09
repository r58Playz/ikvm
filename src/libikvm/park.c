/*
  A sub-millisecond wakeup channel for LockSupport.park/unpark.

  WHY THIS EXISTS. IKVM implements park() on Monitor.Wait, whose only timed overload takes an
  int of MILLISECONDS. OpenJDK's Object.wait(long,int) then rounds any non-zero nanos UP
  (Object.java:456, `if (nanos > 0) timeout++`), so parkNanos(100_000) -- 100us -- becomes a
  1ms sleep. Measured on Minecraft 1.16.1 worldgen: 34,736 parks in one run, mean 1075us,
  91.5% landing in the 1-2ms bucket, 37.3s spent parked. The server loop's effective poll
  interval IS the park duration, because MinecraftServer.pollTaskInternal() falls through to
  ServerChunkCache.pollTask(), a queue nobody unparks for. So the 10x overshoot is throughput.

  WHY NOT SPIN. The obvious fix -- spin for sub-ms timeouts -- does not work on this runtime.
  Thread.Yield() compiles to `return TRUE` (mono/utils/mono-threads-wasm.c:169) and
  Thread.SpinWait() is a relaxed-NOP loop (Thread.Mono.cs:206), so a spin occupies the worker
  continuously rather than yielding it. That both burns a core while nominally idle and makes
  the change unmeasurable: the stall metric in use is thread parallelism (1.48 -> 0.73 cores),
  which a busy-wait inflates in exactly the direction that looks like a win.

  WHAT THIS DOES INSTEAD. emscripten_futex_wait() takes a DOUBLE of milliseconds and converts
  with `max_wait_ns = (int64_t)(max_wait_ms*1000*1000)` -- no integer rounding -- then calls
  __builtin_wasm_memory_atomic_wait32. That genuinely deschedules the worker and is woken by
  memory.atomic.notify. It also handles the browser main thread and audio worklets itself
  (_emscripten_thread_supports_atomics_wait()), falling back to a yielding spin there, so this
  file needs no main-thread guard of its own.

  THE WORD IS A CHANNEL, NOT THE STATE. Thread.parkState remains the sole source of truth for
  the permit, CAS'd managed-side exactly as before; the word here only carries the wakeup. That
  keeps the existing permit state machine untouched and means a hash collision between two
  threads is harmless -- it can only cause an early return, and LockSupport.park is explicitly
  permitted to return spuriously.

  Off-wasm (win/linux/osx, where IKVM runs on CoreCLR) every entry point reports unavailable and
  the managed caller keeps the Monitor path, so desktop behaviour is unchanged.
*/
#ifdef WIN32
#define NETEXPORT __declspec(dllexport)
#define NETCALL __stdcall
#else
#define NETEXPORT __attribute__((visibility("default")))
#define NETCALL
#endif

#include <stdint.h>

/*
 * Requires the PTHREAD build, not merely emscripten. The no-pthread variant links
 * library_pthread_stub.c, whose emscripten_futex_wait is a nop that reports success -- claiming
 * support there would make every sub-millisecond park return instantly, turning the caller's retry
 * loop into the busy-wait this file exists to avoid. emcc defines __EMSCRIPTEN_PTHREADS__ only
 * under -pthread, so the ST archive reports unavailable and keeps the Monitor path.
 */
#if defined(__EMSCRIPTEN__) && defined(__EMSCRIPTEN_PTHREADS__)
#define IKVM_PARK_WASM 1
#endif

#ifdef IKVM_PARK_WASM
#include <emscripten/threading.h>
#include <errno.h>

/*
 * One word per thread, indexed by CLR ManagedThreadId. Padded to a cache line because the
 * threads that park here are consecutively numbered: unpadded, tid 5 and tid 6 would share a
 * line and every notify would invalidate the neighbour's.
 *
 * A fixed table (rather than a lookup keyed by thread) is what keeps unpark lock-free. Ids
 * beyond the table wrap, which costs only the spurious wakeups noted above.
 */
#define IKVM_PARK_SLOTS 1024
#define IKVM_PARK_LINE  16 /* int32 units == 64 bytes */

static _Atomic int32_t ikvm_park_words[IKVM_PARK_SLOTS * IKVM_PARK_LINE];

static _Atomic int32_t* ikvm_park_slot(int32_t tid)
{
    uint32_t i = ((uint32_t)tid) & (IKVM_PARK_SLOTS - 1);
    return &ikvm_park_words[(size_t)i * IKVM_PARK_LINE];
}
#endif

/*
 * Reads the current value of tid's wakeup word. The caller must load this BEFORE testing the
 * permit, and pass it back to IKVM_park_wait: that ordering is what closes the lost-wakeup
 * race. If unpark lands in between, it bumps the word, and the wait then returns immediately
 * with "not-equal" rather than sleeping through a permit that has already been granted.
 *
 * Returns 0 off-wasm; the value is meaningless there because IKVM_park_wait refuses anyway.
 */
NETEXPORT int32_t NETCALL IKVM_park_seq(int32_t tid)
{
#ifdef IKVM_PARK_WASM
    return __c11_atomic_load(ikvm_park_slot(tid), __ATOMIC_SEQ_CST);
#else
    return 0;
#endif
}

/*
 * Sleeps until tid's wakeup word changes from seq, or nanos elapse.
 *
 * Returns 1 if the wait was actually performed (whatever its outcome -- woken, timed out or
 * already-changed; the caller re-tests the permit in all three cases and cannot act on the
 * difference), and 0 if this platform has no sub-millisecond wait, which tells the caller to
 * fall back to Monitor.
 */
NETEXPORT int32_t NETCALL IKVM_park_wait(int32_t tid, int32_t seq, int64_t nanos)
{
#ifdef IKVM_PARK_WASM
    if (nanos <= 0)
        return 1;
    emscripten_futex_wait((volatile void*)ikvm_park_slot(tid), (uint32_t)seq, (double)nanos / 1e6);
    return 1;
#else
    (void)tid; (void)seq; (void)nanos;
    return 0;
#endif
}

/*
 * Publishes a wakeup for tid: bumps the word so any waiter's compare fails, then notifies.
 * Must be called AFTER the permit has been stored, so a waiter that wakes sees the permit.
 *
 * Bumping unconditionally (rather than storing a flag) is what makes a wakeup that arrives
 * before the corresponding wait impossible to lose: the waiter's seq is then already stale.
 */
NETEXPORT void NETCALL IKVM_park_post(int32_t tid)
{
#ifdef IKVM_PARK_WASM
    _Atomic int32_t* w = ikvm_park_slot(tid);
    __c11_atomic_fetch_add(w, 1, __ATOMIC_SEQ_CST);
    emscripten_futex_wake((volatile void*)w, INT32_MAX);
#else
    (void)tid;
#endif
}

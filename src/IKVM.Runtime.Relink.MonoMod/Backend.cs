using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;

using MonoMod.Core;
using MonoMod.Core.Platforms;

namespace IKVM.Runtime.Relink
{

    /// <summary>
    /// IKVM's in-place relink (IKVM_LAZY_INPLACE) for CoreCLR, which cannot swap a method's IL body: generation 1's native
    /// code starts with a jump to generation 2's entry point, written with MonoMod.Core. IKVM.Runtime's RelinkBackend loads
    /// this assembly from IKVM_RELINK_BACKEND and calls the two methods below; the contract is documented there.
    /// </summary>
    /// <remarks>
    /// <para>What a relinked call costs, against mono's swap: the caller still calls generation 1, which jumps to generation
    /// 2's precode, which jumps to its current code -- two well-predicted jumps -- and generation 1 can no longer be
    /// inlined (marked NoInlining, since an inlined copy would bypass the jump). Callers that inlined generation 1 before
    /// the relink keep that copy, which is a correct body. The jump is written over live code while other threads run:
    /// the usual detour hazard, which MonoMod (and Harmony) accept.</para>
    /// <para>Two modes (IKVM_RELINK_MONOMOD):</para>
    /// <list type="bullet">
    /// <item><c>lazy</c> (default): MonoMod.Core's primitives, without compiling generation 2. The jump goes to generation
    /// 2's precode, so CoreCLR compiles it at its first call, on the calling thread -- where mono compiles a swapped body
    /// too. A tier-up of generation 1 is re-patched from MonoMod's JIT hook before the new code is published; the method
    /// is registered before its current code is patched, so a tier-up racing the install is patched as well.</item>
    /// <item><c>managed</c>: MonoMod.Core's managed detour (DetourFactory.CreateDetour). It compiles generation 2 on the
    /// relink drain, where IKVM refuses to finish a type (R514), so a body that needs one fails to install
    /// (RelinkDrainRefusal, counted by IKVM as an install failure); and a tier-up that lands while the detour is being
    /// applied keeps generation 1.</item>
    /// </list>
    /// </remarks>
    public static class Backend
    {

        static readonly bool managed = Environment.GetEnvironmentVariable("IKVM_RELINK_MONOMOD") == "managed";

        // A detour's finalizer undoes it: every one stays reachable for the life of the process.
        static readonly List<object> live = new();

        // lazy mode: generation 1's method handle -> generation 2's entry point, for re-patching a recompiled generation 1
        static readonly ConcurrentDictionary<IntPtr, IntPtr> relinked = new();
        static PlatformTriple triple;
        static int repatched;
        static int repatchFailed;
        static int skippedOffWorker;

        [DllImport("libc", EntryPoint = "prctl", SetLastError = false)]
        static extern unsafe int prctl(int option, byte* arg2, IntPtr arg3, IntPtr arg4, IntPtr arg5);

        /// <summary>
        /// True on CoreCLR's tiered compilation background worker (Linux comm ".NET Tiered Com"), or where the thread's
        /// name cannot be read (then this patches every recompile, as MonoMod's managed detour does).
        /// </summary>
        static unsafe string ThreadComm()
        {
            var buf = stackalloc byte[17];
            try
            {
                return prctl(16 /* PR_GET_NAME */, buf, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 0 ? System.Text.Encoding.UTF8.GetString(buf, new ReadOnlySpan<byte>(buf, 16).IndexOf((byte)0) is var n && n >= 0 ? n : 16) : "?";
            }
            catch
            {
                return "?";
            }
        }

        static unsafe bool OnTieringWorker()
        {
            var buf = stackalloc byte[17];
            try
            {
                if (prctl(16 /* PR_GET_NAME */, buf, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) != 0)
                    return true;
            }
            catch
            {
                return true;
            }
            ReadOnlySpan<byte> worker = ".NET Tiered Com"u8;
            return new ReadOnlySpan<byte>(buf, worker.Length).SequenceEqual(worker);
        }

        /// <summary>
        /// Null if detours work on this runtime; else why not. Creating the platform installs MonoMod's JIT hook.
        /// </summary>
        public static string Initialize()
        {
            try
            {
                triple = PlatformTriple.Current;
                if (triple.SupportedFeatures.Has(RuntimeFeature.CompileMethodHook) == false)
                    return "MonoMod has no JIT hook on " + triple.Runtime.GetType().Name + ": a tier-up of generation 1 would drop the jump";
                if (managed)
                {
                    if (DetourFactory.Current == null)
                        return "no MonoMod DetourFactory";
                }
                else
                {
                    if (triple.SupportedFeatures.Has(RuntimeFeature.DisableInlining) == false)
                        return "MonoMod cannot disable inlining on " + triple.Runtime.GetType().Name;
                    triple.Runtime.OnMethodCompiled += OnMethodCompiled;
                }
                Console.Error.WriteLine($"[IKVM relink-monomod] mode={(managed ? "managed" : "lazy")} runtime={triple.Runtime.GetType().Name} arch={triple.Architecture.GetType().Name}");
                return null;
            }
            catch (Exception e)
            {
                return e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>
        /// Makes every later call of <paramref name="target"/> run <paramref name="source"/>. Throws on failure.
        /// </summary>
        public static int Replace(MethodBase target, MethodBase source)
        {
            if (managed)
            {
                var d = DetourFactory.Current.CreateDetour(target, source, applyByDefault: true);
                lock (live)
                    live.Add(d);
                return 1;
            }

            target = triple.GetIdentifiable(target);
            triple.TryDisableInlining(target);

            // Generation 2's multi-callable entry: its precode, which the prestub binds at the first call and tiering
            // backpatches later, so the jump keeps reaching its current code. Nothing is compiled here.
            var to = source.MethodHandle.GetFunctionPointer();

            // Register first: from here a recompile of generation 1 is patched by OnMethodCompiled.
            relinked[target.MethodHandle.Value] = to;

            // Generation 1 ran (its hook queued this relink), so it has code; Compile is a no-op unless only an inlined
            // copy ran, and then compiles generation 1, whose types are those generation 1 already used.
            triple.Compile(target);
            var from = triple.GetNativeMethodBody(target);
            var nd = triple.CreateSimpleDetour(from, to);
            lock (live)
                live.Add(nd);
            return 1;
        }

        static void OnMethodCompiled(RuntimeMethodHandle handle, MethodBase method, IntPtr codeStart, IntPtr codeRw, ulong codeSize)
        {
            if (relinked.TryGetValue(handle.Value, out var to) == false)
                return;

            // Only a tier-up becomes the method's entry. CoreCLR compiles those on its tiering worker; an OSR variant is
            // compiled on the thread whose loop hit the patchpoint and is entered mid-method with that tier-0 frame live,
            // so a jump at its start would run generation 2 on the wrong registers. MonoMod's hook passes no JIT flags,
            // hence the thread test. Unpatched code is generation 1: correct, at generation 1's cost.
            if (OnTieringWorker() == false)
            {
                // Named, so a reader can match each against the perf map's tier tag (an OSR variant reads OptimizedTier1OSR).
                if (Interlocked.Increment(ref skippedOffWorker) <= 24)
                    Console.Error.WriteLine($"[IKVM relink-monomod] skipped recompile off the tiering worker: {method?.DeclaringType}::{method?.Name} at 0x{codeStart:x} ({codeSize} B) on '{ThreadComm()}'");
                return;
            }

            try
            {
                var nd = triple.CreateSimpleDetour(codeStart, to, (int)codeSize, codeRw);
                lock (live)
                    live.Add(nd);
                var n = Interlocked.Increment(ref repatched);
                if ((n & (n - 1)) == 0)
                    Console.Error.WriteLine($"[IKVM relink-monomod] repatched={n} repatchFailed={repatchFailed} skippedOffWorker={skippedOffWorker} relinked={relinked.Count}");
            }
            catch (Exception e)
            {
                // The new code keeps generation 1: a correct body, at generation 1's cost.
                if (Interlocked.Increment(ref repatchFailed) <= 8)
                    Console.Error.WriteLine($"[IKVM relink-monomod] repatch FAILED for {method}: {e.GetType().Name}: {e.Message}");
            }
        }

    }

}

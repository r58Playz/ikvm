/*
  Copyright (C) 2002-2015 Jeroen Frijters

  This software is provided 'as-is', without any express or implied
  warranty.  In no event will the authors be held liable for any damages
  arising from the use of this software.

  Permission is granted to anyone to use this software for any purpose,
  including commercial applications, and to alter it and redistribute it
  freely, subject to the following restrictions:

  1. The origin of this software must not be misrepresented; you must not
     claim that you wrote the original software. If you use this software
     in a product, an acknowledgment in the product documentation would be
     appreciated but is not required.
  2. Altered source versions must be plainly marked as such, and must not be
     misrepresented as being the original software.
  3. This notice may not be removed or altered from any source distribution.

  Jeroen Frijters
  jeroen@frijters.net
  
*/
using System;
using System.Collections.Generic;
using System.Threading;

#if IMPORTER
using IKVM.Reflection.Emit;
#else
using System.Reflection.Emit;
#endif

namespace IKVM.Runtime
{

    /// <summary>
    /// Everything the lazy-body trampoline needs at first execution in order to recompile a method body.
    /// </summary>
    /// <remarks>
    /// The trampoline is emitted code and can only carry immediates, so it carries an integer id into this
    /// table rather than object references. Entries are never removed: the id is baked into a method body that
    /// lives as long as the type, so reusing ids would let one method's trampoline recompile another's body.
    /// </remarks>
    static class LazyBodyRegistry
    {

        internal sealed class Entry
        {

            /// <summary>The ClassFile the first generation was compiled from. Reparse() is called on it,
            /// because MethodAnalyzer has already rewritten this one's opcodes in place.</summary>
            internal ClassFile ClassFile;

            /// <summary>Index into ClassFile.Methods, not a name+signature lookup: overloads that differ only
            /// in erased signature would resolve ambiguously by name.</summary>
            internal int MethodIndex;

            internal RuntimeByteCodeJavaType Wrapper;

            /// <summary>This entry's index in the registry, stamped by <see cref="Add"/>.</summary>
            internal int Id;

            internal RuntimeJavaType Host;

            /// <summary>The already-baked TypeBuilder. Used as the DynamicMethod owner when it is usable as
            /// one, and as the FinishContext's type otherwise.</summary>
            internal TypeBuilder TypeBuilder;

            internal RuntimeJavaMethod Method;

            /// <summary>The declaring types whose unloadability caused this method's generation-1
            /// <c>__dynamic_*</c> field or method instructions.</summary>
            internal string[] Dependencies = Array.Empty<string>();

            /// <summary>0 = waiting for dependencies, 1 = queued for the only recompile, 2 = terminal.</summary>
            internal int RelinkState;

            /// <summary>Number of Gen1 invocations skipped between dependency probes. Grows from 1 through
            /// 4 and 16 to a cap of 64 while a dependency remains unloaded.</summary>
            internal int RetryDelay;

            internal bool DependenciesLoaded()
            {
                for (int i = 0; i < Dependencies.Length; i++)
                    if (!Wrapper.ClassLoader.IsClassPublished(Dependencies[i]))
                        return false;

                return true;
            }

        }

        static readonly List<Entry> entries = new List<Entry>();

        /// <summary>
        /// Registers <paramref name="entry"/> and returns the id to bake into the trampoline.
        /// </summary>
        internal static int Add(Entry entry)
        {
            lock (entries)
            {
                entries.Add(entry);
                entry.Id = entries.Count - 1;
                return entry.Id;
            }
        }

        /// <summary>
        /// The entry for <paramref name="id"/>, or null if the id is out of range.
        /// </summary>
        /// <remarks>
        /// Range-checked rather than asserted: a stale or corrupted id must degrade to "run the first-generation
        /// helper", never to an index out of range on a hot path in a worker thread.
        /// </remarks>
        internal static Entry Get(int id)
        {
            lock (entries)
            {
                return id >= 0 && id < entries.Count ? entries[id] : null;
            }
        }

    }

    /// <summary>
    /// Candidacy census for the lazy-body path.
    /// </summary>
    /// <remarks>
    /// Exists because the gate silently rejecting everything is indistinguishable from the gate not running:
    /// an earlier version of it consulted analyzer-assigned opcode state from inside FinishContext, which runs
    /// BEFORE the analyzer assigns it, and produced zero candidates while looking perfectly healthy. Counting
    /// each rejection reason separately makes that failure mode loud -- "nodyn=15569" is a working gate,
    /// "seen=0" is a gate that never ran.
    /// </remarks>
    static class LazyBodyStats
    {

        internal static int Seen;
        internal static int Accepted;
        internal static int RejShape;             // abstract/native/no instructions, or <init>/<clinit>
        internal static int RejNoDynamic;         // nothing dynamic to gain from a second generation
        internal static int OperandOnly;          // wasm fork (R354): candidates ONLY because of an unloadable class operand
        internal static int RejArity;             // more args than the delegate types cover
        internal static int RejExceptionHandlers; // only with IKVM_LAZY_SKIP_EH=1

        // The signature screen, SPLIT BY CAUSE. These were one bucket, and that hid the whole finding:
        // ghost/remapped/value types need real conversion at the delegate boundary, while an unloadable
        // merely erases to System.Object -- so the second is admissible (IKVM_LAZY_SIG) and the first is
        // not. Measured together as sig=5,583 on 2026-09-07; only the unloadable half is addressable.
        internal static int RejSigAwkward;        // ghost/ghost-array/remapped/non-primitive value type
        internal static int RejSigUnloadable;     // unloadable only, i.e. refused solely for lacking IKVM_LAZY_SIG

        // Recompile OUTCOMES. Counted here because the candidacy decision happens in FinishContext and the
        // action happens in ByteCodeHelper.BuildLazyBody: two files, and until now nothing asserted that the
        // populations agreed. A gate that accepts 4,945 methods and a recompiler that heals 2,000 of them is
        // not a contradiction, but it is only visible if both halves are counted.
        internal static int Gen2Clean;            // recompiled body verified to hold no __dynamic_* references
        internal static int Gen2UnexpectedDynamic;

        /// <summary>Surviving dynamic references tallied by OPCODE + whether the declaring type is still
        /// unloadable. "declTypeUnloadable=False" means the gate did its job and the MEMBER still failed to
        /// resolve -- a different bug from a declaring type the collector never saw.</summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> unexpectedDynKinds =
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>();

        internal static void RecordUnexpectedDynamic(string desc)
        {
            if (string.IsNullOrEmpty(desc))
                desc = "(none described)";
            // Key on the opcode and the unloadable flag, not the member name: the member is unbounded,
            // the shape is what decides the fix.
            var sp = desc.IndexOf(' ');
            var op = sp > 0 ? desc.Substring(0, sp) : desc;
            var unl = desc.Contains("declTypeUnloadable=True") ? "declUnloadable"
                    : desc.Contains("declTypeUnloadable=False") ? "declLOADED-memberFailed"
                    : "declNull";
            unexpectedDynKinds.AddOrUpdate($"{op}/{unl}", 1, (_, v) => v + 1);
        }

        internal static string UnexpectedDynamicSummary()
        {
            if (unexpectedDynKinds.IsEmpty) return "(none)";
            var parts = new System.Collections.Generic.List<string>();
            foreach (var kv in unexpectedDynKinds) parts.Add($"{kv.Key}={kv.Value}");
            parts.Sort();
            return string.Join("  ", parts);
        }// dependency extraction/linking defect; body is not installed
        internal static int Gen2CallerID;         // declined: the body needs a CallerID the baked type cannot supply
        internal static int DependencyWaits;      // generation-1 entries that ran before every dependency loaded
        internal static int MissingBakedSite;     // indy/MH site whose generation-1 bridge could not be found
        internal static int Gen2BakedLambdaSite;  // Gen2 selected Gen1's lambda bridge without reclassification

        // The two CallerID limbs, counted SEPARATELY. Phase 1a changed both at once and shipped with a
        // count for neither, so when it regressed the boot there was no way to say which limb ran -- the
        // decline counter only ever saw the `else` arm. An arm with no counter cannot be cleared OR
        // blamed. These paths are part of Gen2 correctness rather than separately configurable behavior.
        internal static int Gen2CallerIDReal;     // limb (a): took the InternalsVisibleTo arm, Ldtoken of the real type
        internal static int Gen2CallerIDReused;   // limb (b): constructed generation 1's already-baked nested CallerID
        internal static int Gen2CallerIDForeign;  // limb (b) REFUSED: the factory found did not belong to this type
        internal static int Gen2ScratchShared;   // a recompile scratch already carried another context's CallerID field

        // IN-PLACE outcomes. Split from the gen2 counters because the question they answer is different:
        // gen2* says whether the recompile produced a clean body, these say whether it got INSTALLED.
        internal static int InPlaceOk;            // body swapped, or swapped-before-first-transform
        internal static int InPlaceLate;          // runtime refused: already tiered up or already wasm-JITted
        internal static int InPlaceDeclined;      // we refused: no reparse, residual dynamic refs, or CallerID
        internal static int InPlaceFailed;
        /// <summary>Orphaned MethodBuilders given a stub body so one failed member cannot fail the whole
        /// batch at CreateType. Non-zero is the healthy reading: each one is a batch that survived.</summary>
        internal static int InPlaceStubbed;
        internal static int InPlaceStubFailed;

        /// <summary>In-place failures by exception type and phase. Bounded LOGGING (first 8) left 1,905 of
        /// 1,913 failures with no recorded reason at all (R276) -- a 47% failure rate that could not be
        /// attributed. A tally is O(1) per failure, covers every one of them, and is what turns "47% fail"
        /// into "47% fail, for these reasons".</summary>
        static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> inPlaceFailKinds =
            new System.Collections.Concurrent.ConcurrentDictionary<string, int>();

        internal static void RecordInPlaceFailure(string phase, System.Exception e)
        {
            // The message's leading token distinguishes causes that share a type -- a MissingMethodException
            // for a lazySite bridge is a different bug from one for an ordinary member.
            var msg = e.Message ?? "";
            var cut = msg.IndexOf(',');
            var head = cut > 0 ? msg.Substring(0, cut) : msg;
            if (head.Length > 40) head = head.Substring(0, 40);
            var site = msg.Contains("__<>lazySite$") ? " lazySite" : "";
            inPlaceFailKinds.AddOrUpdate($"{phase}/{e.GetType().Name}/{head}{site}", 1, (_, v) => v + 1);
        }

        internal static string InPlaceFailureSummary()
        {
            if (inPlaceFailKinds.IsEmpty)
                return "(none)";
            var parts = new System.Collections.Generic.List<string>();
            foreach (var kv in inPlaceFailKinds)
                parts.Add($"{kv.Key}={kv.Value}");
            parts.Sort();
            return string.Join("  ", parts);
        }        // threw, or the runtime rejected the shape
        internal static int InPlaceUnsupported;   // runtime cannot swap bodies at all (CoreCLR)
        internal static int InPlaceBatches;       // scratch types created, i.e. CreateType() calls paid
        internal static int InPlaceQueued;        // hooks that enqueued a method for a later batch
        internal static int InPlaceDeferrals;     // non-blocking quiet-window retries during class definition
        internal static int InPlaceForced;        // batches run after the bounded deferral deadline

        internal static bool Reject(ref int bucket)
        {
            Interlocked.Increment(ref bucket);
            Tick();
            return false;
        }

        internal static bool Accept()
        {
            Interlocked.Increment(ref Accepted);
            Tick();
            return true;
        }

        static void Tick()
        {
            // Report at the first evaluation and every thousand after: the first line proves the gate is
            // running at all, which is the thing that was silently false before.
            var n = Interlocked.Increment(ref Seen);
            if (n == 1 || n % 1000 == 0)
                Report();
        }

        internal static void Report()
        {
            Console.WriteLine($"[IKVM lazybody] eval seen={Seen} accepted={Accepted} opOnly={OperandOnly} rej: shape={RejShape} nodyn={RejNoDynamic} arity={RejArity} sigAwkward={RejSigAwkward} sigUnloadable={RejSigUnloadable} eh={RejExceptionHandlers}");
            Console.WriteLine($"[IKVM lazybody] gen2 clean={Gen2Clean} unexpectedDynamic={Gen2UnexpectedDynamic} callerID={Gen2CallerID} depWaits={DependencyWaits} missingSite={MissingBakedSite} bakedLambda={Gen2BakedLambdaSite} cidReal={Gen2CallerIDReal} cidReused={Gen2CallerIDReused} cidForeign={Gen2CallerIDForeign} scratchShared={Gen2ScratchShared}");
            if (JVM.LazyInPlace)
                Console.WriteLine($"[IKVM lazybody] inplace ok={InPlaceOk} late={InPlaceLate} declined={InPlaceDeclined} failed={InPlaceFailed} unsupported={InPlaceUnsupported} queued={InPlaceQueued} batches={InPlaceBatches} deferrals={InPlaceDeferrals} forced={InPlaceForced}");
            Console.WriteLine($"[IKVM lazybody] inplaceFail stubbed={InPlaceStubbed} stubFailed={InPlaceStubFailed} {InPlaceFailureSummary()}");
            Console.WriteLine($"[IKVM lazybody] unexpDyn {UnexpectedDynamicSummary()}");
            Console.WriteLine($"[IKVM lazybody] noLoadBootstrapFinds={RuntimeClassLoader.NoLoadFoundInBootstrap}");
        }

    }

}

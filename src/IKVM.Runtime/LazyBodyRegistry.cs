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

            internal RuntimeJavaType Host;

            /// <summary>The already-baked TypeBuilder. Used as the DynamicMethod owner when it is usable as
            /// one, and as the FinishContext's type otherwise.</summary>
            internal TypeBuilder TypeBuilder;

            internal RuntimeJavaMethod Method;

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
                return entries.Count - 1;
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
        internal static int RejMethodHandle;      // indy or MethodHandle call site, with IKVM_LAZY_MH off
        internal static int RejNoDynamic;         // nothing dynamic to gain from a second generation
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
        internal static int Gen2StillDynamic;     // recompiled, but STILL has dynamic refs: the types are not loaded yet
        internal static int Gen2CallerID;         // declined: the body needs a CallerID the baked type cannot supply

        // IN-PLACE outcomes. Split from the gen2 counters because the question they answer is different:
        // gen2* says whether the recompile produced a clean body, these say whether it got INSTALLED.
        internal static int InPlaceOk;            // body swapped, or swapped-before-first-transform
        internal static int InPlaceLate;          // runtime refused: already tiered up or already wasm-JITted
        internal static int InPlaceDeclined;      // we refused: no reparse, an indy site, or a CallerID
        internal static int InPlaceFailed;        // threw, or the runtime rejected the shape
        internal static int InPlaceUnsupported;   // runtime cannot swap bodies at all (CoreCLR)
        internal static int InPlaceBatches;       // scratch types created, i.e. CreateType() calls paid
        internal static int InPlaceQueued;        // hooks that enqueued a method for a later batch

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
            Console.WriteLine($"[IKVM lazybody] eval seen={Seen} accepted={Accepted} rej: shape={RejShape} mh={RejMethodHandle} nodyn={RejNoDynamic} arity={RejArity} sigAwkward={RejSigAwkward} sigUnloadable={RejSigUnloadable} eh={RejExceptionHandlers}");
            Console.WriteLine($"[IKVM lazybody] gen2 clean={Gen2Clean} stillDynamic={Gen2StillDynamic} callerID={Gen2CallerID}");
            if (JVM.LazyInPlace)
                Console.WriteLine($"[IKVM lazybody] inplace ok={InPlaceOk} late={InPlaceLate} declined={InPlaceDeclined} failed={InPlaceFailed} unsupported={InPlaceUnsupported} queued={InPlaceQueued} batches={InPlaceBatches}");
        }

    }

}

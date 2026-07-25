/*
  Copyright (C) 2007-2014 Jeroen Frijters

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

using IKVM.Runtime;

namespace IKVM.Java.Externs.java.lang
{

    static class Runtime
    {

        public static int availableProcessors(object thisRuntime)
        {
            return Environment.ProcessorCount;
        }

        /// <summary>
        /// Reads the managed heap's committed and free sizes from the GC's cached
        /// statistics. This is an O(1) read of the values recorded at the end of the
        /// last collection; it does NOT walk the heap. (GC.GetTotalMemory, previously
        /// used here, resolves on mono/sgen to a full iteration of every object in the
        /// major heap plus a forced concurrent-sweep finish, which is far too expensive
        /// to run on every Runtime.totalMemory()/freeMemory() call.)
        /// </summary>
        static (long total, long free) GetHeapInfo()
        {
#if NETFRAMEWORK
            // Desktop .NET Framework only (never the WASM target): GetTotalMemory(false) is
            // a cheap cached read here — the expensive sgen heap-walk is mono-specific — so
            // the legacy behavior is fine and GetGCMemoryInfo isn't available anyway.
            return (GC.GetTotalMemory(false), 0);
#else
            var info = GC.GetGCMemoryInfo();
            var total = info.HeapSizeBytes;
            var free = info.FragmentedBytes;

            // Before the first GC the cached stats are zero. Nothing has been collected
            // yet, so the cumulative allocated byte count approximates the live set; use
            // it as "used" with no known free headroom. This self-corrects once the first
            // collection runs. GetTotalAllocatedBytes(false) is a cheap counter read.
            if (total <= 0)
                return (GC.GetTotalAllocatedBytes(false), 0);

            return (total, free);
#endif
        }

        public static long freeMemory(object thisRuntime)
        {
            return GetHeapInfo().free;
        }

        public static long totalMemory(object thisRuntime)
        {
            return GetHeapInfo().total;
        }

        public static long maxMemory(object thisRuntime)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            // On WASM this is the linear-memory ceiling (the module's MAXIMUM_MEMORY):
            // how far the heap can grow, the honest analogue of the JVM's -Xmx.
            var max = LibIkvm.Instance.get_heap_max();
            if (max > 0)
                return max;

            // Not a WASM build (or the runtime can't report a ceiling): fall back to the
            // GC's notion of total available memory, else Long.MAX_VALUE. The spec says:
            // if there is no inherent limit then Long.MAX_VALUE is returned.
#if NETFRAMEWORK == false
            var avail = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
            if (avail > 0)
                return avail;
#endif
            return long.MaxValue;
#endif
        }

        public static void gc(object thisRuntime)
        {
            GC.Collect();
        }

        public static void traceInstructions(object thisRuntime, bool on)
        {
        }

        public static void traceMethodCalls(object thisRuntime, bool on)
        {
        }

        public static void runFinalization0()
        {
            GC.WaitForPendingFinalizers();
        }

    }

}

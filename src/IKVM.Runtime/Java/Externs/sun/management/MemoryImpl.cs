/*
  Copyright (C) 2011-2014 Jeroen Frijters

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

namespace IKVM.Java.Externs.sun.management
{

    static class MemoryImpl
    {

        public static object getMemoryPools0()
        {
#if FIRST_PASS
		    return null;
#else
            return new global::java.lang.management.MemoryPoolMXBean[0];
#endif
        }

        public static object getMemoryManagers0()
        {
#if FIRST_PASS
		    return null;
#else
            return new global::java.lang.management.MemoryManagerMXBean[0];
#endif
        }

        public static object getMemoryUsage0(object impl, bool heap)
        {
#if FIRST_PASS
		    return null;
#else
            // The GC's statistics from its last collection, an O(1) read -- the same source Runtime.totalMemory() and
            // freeMemory() use, for the same reason: GC.GetTotalMemory resolves on mono/sgen to a walk of every object in
            // the major heap (sgen-marksweep.c major_get_used_size), and Minecraft's F3 screen (Sodium's line on it, and
            // 26.2's own DebugEntryMemory) asks MemoryMXBean for usage every frame: 38-66 ms a frame at Minecraft's heap
            // sizes (1.16.5 61 -> 17 fps, 26.2 106 -> 12 with F3 open).
#if NETFRAMEWORK
            long mem = System.GC.GetTotalMemory(false);
#else
            var info = System.GC.GetGCMemoryInfo();
            long mem = info.HeapSizeBytes - info.FragmentedBytes;

            // Before the first collection the cached statistics are zero; the cumulative allocation count is the
            // cheap stand-in, as in Runtime.GetHeapInfo.
            if (mem <= 0)
                mem = System.GC.GetTotalAllocatedBytes(false);
#endif
            return new global::java.lang.management.MemoryUsage(-1, mem, mem, -1);
#endif
        }

        public static void setVerboseGC(object impl, bool value)
        {

        }

    }

}
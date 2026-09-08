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

namespace IKVM.Runtime
{

    [Flags]
    enum LoadMode
    {

        // These are the modes that should be used
        Find = ReturnNull,
        LoadOrNull = Load | ReturnNull,
        LoadOrThrow = Load | ThrowClassNotFound,
        Link = Load | ReturnUnloadable | SuppressExceptions,
        // OpenJDK-aligned lazy linking: never invokes user-defined loadClass (no Load bit); classes
        // not resolvable through trusted no-user-code paths become unloadable and are resolved
        // lazily at first execution of the referencing instruction via the __dynamic_* stubs.
        LinkLazy = ReturnUnloadable | SuppressExceptions | TrustedProbe,

        // Link WITHOUT ever loading anything: resolves only what is already loaded and returns unloadable
        // for the rest. No Load bit and, unlike LinkLazy, no TrustedProbe either -- TrustedProbe re-enters
        // with the Load bit for a vouched name, which is still a load.
        //
        // This exists for the in-place second-generation relink, which runs at a method's FIRST EXECUTION.
        // That can be inside a Fabric Mixin transform, and an eager LoadMode.Link there re-enters Knot:
        // `ReEntrantTransformerError`, Mixin bails, and the class is defined WITHOUT its mixins. Measured
        // with LoadMode.Link: 189 re-entrance errors per run against 0-1 in the control, which silently
        // changed the workload and voided the timing. Not loading at all removes the whole class of bug
        // rather than guarding each path into it -- and it costs only the references that genuinely are
        // not loaded yet, which stay __dynamic_* and are counted as Gen2StillDynamic.
        LinkNoLoad = ReturnUnloadable | SuppressExceptions,

        // call into Java class loader
        Load = 0x0001,

        // return value
        DontReturnUnloadable = 0x0002,  // This is used with a bitwise OR to disable returning unloadable
        ReturnUnloadable = 0x0004,
        ReturnNull = 0x0004 | DontReturnUnloadable,
        ThrowClassNotFound = 0x0008 | DontReturnUnloadable,
        MaskReturn = ReturnUnloadable | ReturnNull | ThrowClassNotFound,

        // exceptions (not ClassNotFoundException)
        SuppressExceptions = 0x0010,

        // warnings
        WarnClassNotFound = 0x0020,

        // may resolve through loaders that run no user code (boot image) and through names the
        // initiating loader explicitly vouches for (ikvm.lang.LinkTrustPolicy), but never through
        // an unsolicited user-defined loadClass
        TrustedProbe = 0x0040,

    }

}

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

using IKVM.ByteCode;

namespace IKVM.Runtime
{

    sealed partial class ClassFile
    {

        internal static class SupportedVersions
        {

            /// <summary>
            /// The newest class file major version that can be read: Java 25.
            /// </summary>
            internal const int Maximum = JVM.ClassFileMajorVersion;

            /// <summary>
            /// Whether a class file of the given version can be read: 45.3 through <see cref="Maximum"/>, but not a
            /// preview-features class file (minor version 65535), which the JVM loads only with --enable-preview.
            /// </summary>
            internal static bool IsSupported(ClassFormatVersion version)
            {
                return version >= new ClassFormatVersion(45, 3) && version.Major <= Maximum && (version.Major < 56 || version.Minor != 65535);
            }

        }

    }

}

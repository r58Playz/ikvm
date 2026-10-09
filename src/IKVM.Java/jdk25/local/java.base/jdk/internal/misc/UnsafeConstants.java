/*
 * IKVM: JDK 25's jdk.internal.misc.UnsafeConstants, whose values HotSpot injects after the class is initialized;
 * here the initializer computes them from .NET.
 */
/*
 * Copyright (c) 2019, Oracle and/or its affiliates. All rights reserved.
 * Copyright (c) 2019, Red Hat Inc. All rights reserved.
 * DO NOT ALTER OR REMOVE COPYRIGHT NOTICES OR THIS FILE HEADER.
 *
 * This code is free software; you can redistribute it and/or modify it
 * under the terms of the GNU General Public License version 2 only, as
 * published by the Free Software Foundation.  Oracle designates this
 * particular file as subject to the "Classpath" exception as provided
 * by Oracle in the LICENSE file that accompanied this code.
 *
 * This code is distributed in the hope that it will be useful, but WITHOUT
 * ANY WARRANTY; without even the implied warranty of MERCHANTABILITY or
 * FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License
 * version 2 for more details (a copy is included in the LICENSE file that
 * accompanied this code).
 *
 * You should have received a copy of the GNU General Public License version
 * 2 along with this work; if not, write to the Free Software Foundation,
 * Inc., 51 Franklin St, Fifth Floor, Boston, MA 02110-1301 USA.
 *
 * Please contact Oracle, 500 Oracle Parkway, Redwood Shores, CA 94065 USA
 * or visit www.oracle.com if you need additional information or have any
 * questions.
 */

package jdk.internal.misc;

/**
 * Constants the VM provides to {@link Unsafe}.
 */
final class UnsafeConstants {

    private UnsafeConstants() {}

    /**
     * The size in bytes of a native pointer, as stored via {@link
     * #putAddress}.  This value will be either 4 or 8.
     */
    static final int ADDRESS_SIZE0;

    /**
     * The size in bytes of a native memory page.  This value will always
     * be a power of two.
     */
    static final int PAGE_SIZE;

    /**
     * Flag whose value is true if and only if the native endianness
     * of this platform is big.
     */
    static final boolean BIG_ENDIAN;

    /**
     * Flag whose value is true if and only if the platform can
     * perform unaligned accesses.
     */
    static final boolean UNALIGNED_ACCESS;

    /**
     * The size of an L1 data cache line which will be either a power
     * of two or zero; zero when data cache line writeback is not
     * supported, as here.
     */
    static final int DATA_CACHE_LINE_FLUSH_SIZE;

    static {
        ADDRESS_SIZE0 = cli.System.IntPtr.get_Size();
        PAGE_SIZE = cli.System.Environment.get_SystemPageSize();
        BIG_ENDIAN = !cli.System.BitConverter.IsLittleEndian;
        // the platforms .NET supports (x64, Arm64, WebAssembly) do unaligned accesses
        UNALIGNED_ACCESS = true;
        DATA_CACHE_LINE_FLUSH_SIZE = 0;
    }
}

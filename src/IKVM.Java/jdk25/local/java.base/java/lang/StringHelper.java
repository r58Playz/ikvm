/*
 * Copyright (c) 1994, 2025, Oracle and/or its affiliates. All rights reserved.
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

package java.lang;

import java.io.UnsupportedEncodingException;
import java.lang.foreign.MemorySegment;
import java.lang.invoke.MethodHandles;
import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.Charset;
import java.nio.charset.CharsetDecoder;
import java.nio.charset.CharsetEncoder;
import java.nio.charset.CoderResult;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.IllegalCharsetNameException;
import java.nio.charset.MalformedInputException;
import java.nio.charset.UnmappableCharacterException;
import java.nio.charset.UnsupportedCharsetException;
import java.util.ArrayList;
import java.util.Formatter;
import java.util.List;
import java.util.Locale;
import java.util.Objects;
import java.util.Optional;
import java.util.Spliterator;
import java.util.StringJoiner;
import java.util.function.Consumer;
import java.util.function.Function;
import java.util.function.IntConsumer;
import java.util.regex.Matcher;
import java.util.regex.Pattern;
import java.util.stream.Collectors;
import java.util.stream.IntStream;
import java.util.stream.Stream;
import java.util.stream.StreamSupport;
import jdk.internal.util.Preconditions;
import sun.nio.cs.ISO_8859_1;
import sun.nio.cs.US_ASCII;
import sun.nio.cs.UTF_8;

/**
 * The members of java.lang.String, which map.xml maps onto System.String. Instance methods take the string as their
 * first argument.
 *
 * <p>JDK 25's String keeps its characters in a byte[] with a coder. IKVM's strings are System.String, so to the rest
 * of the class library String presents itself as if it were always UTF-16 (String.COMPACT_STRINGS is false): coder()
 * is UTF16, and the byte[] members (value(), getBytes(byte[], ...), the (byte[], byte) constructor) use the UTF-16
 * layout of StringUTF16, which is the machine byte order.
 *
 * <p>The charset coding below follows JDK 25's String, decoding into char[] rather than UTF-16 bytes.
 */
final class StringHelper {

    private StringHelper() {
    }

    static final byte LATIN1 = 0;
    static final byte UTF16 = 1;

    private static final char REPL = '�';

    //------------------------------ constructors ------------------------------

    static String NewString(int[] codePoints, int offset, int count) {
        Preconditions.checkFromIndexSize(offset, count, codePoints.length, Preconditions.SIOOBE_FORMATTER);
        final int end = offset + count;

        // Pass 1: Compute precise size of char[]
        int n = count;
        for (int i = offset; i < end; i++) {
            int c = codePoints[i];
            if (Character.isBmpCodePoint(c))
                continue;
            else if (Character.isValidCodePoint(c))
                n++;
            else throw new IllegalArgumentException(Integer.toString(c));
        }

        // Pass 2: Allocate and fill in char[]
        final char[] v = new char[n];
        for (int i = offset, j = 0; i < end; i++, j++) {
            int c = codePoints[i];
            if (Character.isBmpCodePoint(c))
                v[j] = (char)c;
            else
                Character.toSurrogates(c, v, j++);
        }

        return new String(v);
    }

    @SuppressWarnings("deprecation")
    static String NewString(byte[] ascii, int hibyte, int offset, int count) {
        Preconditions.checkFromIndexSize(offset, count, ascii.length, Preconditions.SIOOBE_FORMATTER);
        char[] value = new char[count];
        if (hibyte == 0) {
            for (int i = count; i-- > 0;) {
                value[i] = (char)(ascii[i + offset] & 0xff);
            }
        } else {
            hibyte <<= 8;
            for (int i = count; i-- > 0;) {
                value[i] = (char)(hibyte | (ascii[i + offset] & 0xff));
            }
        }
        return new String(value);
    }

    @SuppressWarnings("deprecation")
    static String NewString(byte[] ascii, int hibyte) {
        return NewString(ascii, hibyte, 0, ascii.length);
    }

    static String NewString(byte[] bytes, int offset, int length, String charsetName) throws UnsupportedEncodingException {
        return decode(lookupCharset(charsetName), bytes, checkBoundsOffCount(offset, length, bytes.length), length);
    }

    static String NewString(byte[] bytes, int offset, int length, Charset charset) {
        return decode(Objects.requireNonNull(charset), bytes, checkBoundsOffCount(offset, length, bytes.length), length);
    }

    static String NewString(byte[] bytes, String charsetName) throws UnsupportedEncodingException {
        return decode(lookupCharset(charsetName), bytes, 0, bytes.length);
    }

    static String NewString(byte[] bytes, Charset charset) {
        return decode(Objects.requireNonNull(charset), bytes, 0, bytes.length);
    }

    static String NewString(byte[] bytes, int offset, int length) {
        return decode(Charset.defaultCharset(), bytes, checkBoundsOffCount(offset, length, bytes.length), length);
    }

    static String NewString(byte[] bytes) {
        return decode(Charset.defaultCharset(), bytes, 0, bytes.length);
    }

    static String NewString(StringBuffer buffer) {
        return buffer.toString();
    }

    static String NewString(StringBuilder builder) {
        return builder.toString();
    }

    // String(AbstractStringBuilder, Void): the copy StringBuilder.toString() and StringBuffer.toString() make
    static String NewString(AbstractStringBuilder asb, Void sig) {
        byte[] val = asb.getValue();
        int length = asb.length();
        if (asb.isLatin1()) {
            return fromLatin1(val, 0, length);
        }
        return fromUTF16(val, 0, length);
    }

    // String(byte[], byte): a string of the given coder's bytes
    static String NewString(byte[] value, byte coder) {
        return coder == LATIN1 ? fromLatin1(value, 0, value.length) : fromUTF16(value, 0, value.length >> 1);
    }

    private static String fromLatin1(byte[] val, int off, int len) {
        char[] ca = new char[len];
        for (int i = 0; i < len; i++) {
            ca[i] = (char)(val[off + i] & 0xff);
        }
        return new String(ca);
    }

    private static String fromUTF16(byte[] val, int off, int len) {
        char[] ca = new char[len];
        for (int i = 0; i < len; i++) {
            ca[i] = StringUTF16.getChar(val, off + i);
        }
        return new String(ca);
    }

    //------------------------------ the String internals the class library uses ------------------------------

    static byte coder(String _this) {
        Objects.requireNonNull(_this);
        return UTF16;
    }

    static boolean isLatin1(String _this) {
        Objects.requireNonNull(_this);
        return false;
    }

    // the characters as UTF-16 bytes; a copy, which callers only read
    static byte[] value(String _this) {
        int len = _this.length();
        byte[] val = StringUTF16.newBytesFor(len);
        for (int i = 0; i < len; i++) {
            StringUTF16.putChar(val, i, _this.charAt(i));
        }
        return val;
    }

    // copies the string into dst, which holds characters of the given coder, at character index dstBegin
    static void getBytes(String _this, byte[] dst, int dstBegin, byte coder) {
        getBytes(_this, dst, 0, dstBegin, coder, _this.length());
    }

    static void getBytes(String _this, byte[] dst, int srcPos, int dstBegin, byte coder, int length) {
        if (coder == LATIN1) {
            for (int i = 0; i < length; i++) {
                dst[dstBegin + i] = (byte)_this.charAt(srcPos + i);
            }
        } else {
            for (int i = 0; i < length; i++) {
                StringUTF16.putChar(dst, dstBegin + i, _this.charAt(srcPos + i));
            }
        }
    }

    // whether the string's bytes can be copied as they are into the given charset: never, as they are UTF-16
    static boolean bytesCompatible(String _this, Charset charset) {
        Objects.requireNonNull(_this);
        return false;
    }

    static void copyToSegmentRaw(String _this, MemorySegment segment, long offset) {
        throw new UnsupportedOperationException();
    }

    static void checkIndex(int index, int length) {
        Preconditions.checkIndex(index, length, Preconditions.SIOOBE_FORMATTER);
    }

    static void checkOffset(int offset, int length) {
        Preconditions.checkFromToIndex(offset, length, length, Preconditions.SIOOBE_FORMATTER);
    }

    static int checkBoundsOffCount(int offset, int count, int length) {
        return Preconditions.checkFromIndexSize(offset, count, length, Preconditions.SIOOBE_FORMATTER);
    }

    static void checkBoundsBeginEnd(int begin, int end, int length) {
        Preconditions.checkFromToIndex(begin, end, length, Preconditions.SIOOBE_FORMATTER);
    }

    static String valueOfCodePoint(int codePoint) {
        if (Character.isBmpCodePoint(codePoint)) {
            return String.valueOf((char)codePoint);
        } else if (Character.isSupplementaryCodePoint(codePoint)) {
            return new String(Character.toChars(codePoint));
        }
        throw new IllegalArgumentException(String.format("Not a valid Unicode code point: 0x%X", codePoint));
    }

    // AbstractStringBuilder.indexOf: search src, srcCount characters of the given coder, for tgtStr
    static int indexOf(byte[] src, byte srcCoder, int srcCount, String tgtStr, int fromIndex) {
        fromIndex = Math.clamp(fromIndex, 0, srcCount);
        int tgtCount = tgtStr.length();
        if (tgtCount > srcCount - fromIndex) {
            return -1;
        }
        if (tgtCount == 0) {
            return fromIndex;
        }
        char first = tgtStr.charAt(0);
        int max = srcCount - tgtCount;
        for (int i = fromIndex; i <= max; i++) {
            if (charAt(src, srcCoder, i) != first) {
                while (++i <= max && charAt(src, srcCoder, i) != first);
            }
            if (i <= max) {
                int j = i + 1;
                int end = j + tgtCount - 1;
                for (int k = 1; j < end && charAt(src, srcCoder, j) == tgtStr.charAt(k); j++, k++);
                if (j == end) {
                    return i;
                }
            }
        }
        return -1;
    }

    static int lastIndexOf(byte[] src, byte srcCoder, int srcCount, String tgtStr, int fromIndex) {
        int tgtCount = tgtStr.length();
        int rightIndex = srcCount - tgtCount;
        if (fromIndex > rightIndex) {
            fromIndex = rightIndex;
        }
        if (fromIndex < 0) {
            return -1;
        }
        if (tgtCount == 0) {
            return fromIndex;
        }
        int strLastIndex = tgtCount - 1;
        char strLastChar = tgtStr.charAt(strLastIndex);
        int min = tgtCount - 1;
        int i = min + fromIndex;

    startSearchForLastChar:
        while (true) {
            while (i >= min && charAt(src, srcCoder, i) != strLastChar) {
                i--;
            }
            if (i < min) {
                return -1;
            }
            int j = i - 1;
            int start = j - (tgtCount - 1);
            int k = strLastIndex - 1;
            while (j > start) {
                if (charAt(src, srcCoder, j--) != tgtStr.charAt(k--)) {
                    i--;
                    continue startSearchForLastChar;
                }
            }
            return start + 1;
        }
    }

    private static char charAt(byte[] val, byte coder, int index) {
        return coder == LATIN1 ? (char)(val[index] & 0xff) : StringUTF16.getChar(val, index);
    }

    static void repeatCopyRest(byte[] buffer, int offset, int limit, int copied) {
        // Initial copy is in the buffer.
        for (; copied < limit - copied; copied <<= 1) {
            // Power of two duplicate.
            System.arraycopy(buffer, offset, buffer, offset + copied, copied);
        }
        // Duplicate remainder.
        System.arraycopy(buffer, offset, buffer, offset + copied, limit - copied);
    }

    static String join(String prefix, String suffix, String delimiter, String[] elements, int size) {
        long len = (long)prefix.length() + suffix.length();
        if (size > 1) {
            len += (long)(size - 1) * delimiter.length();
        }
        for (int i = 0; i < size; i++) {
            len += elements[i].length();
        }
        if (len > Integer.MAX_VALUE) {
            throw new OutOfMemoryError("Requested string length exceeds VM limit");
        }
        char[] value = new char[(int)len];
        int off = 0;
        prefix.getChars(0, prefix.length(), value, off); off += prefix.length();
        if (size > 0) {
            String el = elements[0];
            el.getChars(0, el.length(), value, off); off += el.length();
            for (int i = 1; i < size; i++) {
                delimiter.getChars(0, delimiter.length(), value, off); off += delimiter.length();
                el = elements[i];
                el.getChars(0, el.length(), value, off); off += el.length();
            }
        }
        suffix.getChars(0, suffix.length(), value, off);
        return new String(value);
    }

    //------------------------------ the JavaLangAccess coding methods ------------------------------

    static String newStringUTF8NoRepl(byte[] bytes, int offset, int length, boolean noShare) {
        checkBoundsOffCount(offset, length, bytes.length);
        if (length == 0) {
            return "";
        }
        char[] dst = new char[length];
        int dp = decodeUTF8_UTF16(bytes, offset, offset + length, dst, 0, false);
        return new String(dst, 0, dp);
    }

    static String newStringNoRepl(byte[] src, Charset cs) throws CharacterCodingException {
        try {
            return newStringNoRepl1(src, cs);
        } catch (IllegalArgumentException e) {
            //newStringNoRepl1 throws IAE with MalformedInputException or CCE as the cause
            Throwable cause = e.getCause();
            if (cause instanceof MalformedInputException mie) {
                throw mie;
            }
            throw (CharacterCodingException)cause;
        }
    }

    private static String newStringNoRepl1(byte[] src, Charset cs) {
        int len = src.length;
        if (len == 0) {
            return "";
        }
        if (cs == UTF_8.INSTANCE) {
            return newStringUTF8NoRepl(src, 0, src.length, false);
        }
        if (cs == ISO_8859_1.INSTANCE) {
            return fromLatin1(src, 0, len);
        }
        if (cs == US_ASCII.INSTANCE) {
            if (!hasNegatives(src, 0, len)) {
                return fromLatin1(src, 0, len);
            } else {
                throwMalformed(src);
            }
        }

        CharsetDecoder cd = cs.newDecoder();
        int en = scale(len, cd.maxCharsPerByte());
        char[] ca = new char[en];
        int caLen;
        try {
            caLen = decodeWithDecoder(cd, ca, src, 0, src.length);
        } catch (CharacterCodingException x) {
            // throw via IAE
            throw new IllegalArgumentException(x);
        }
        return new String(ca, 0, caLen);
    }

    static byte[] getBytesUTF8NoRepl(String s) {
        return encodeUTF8(s, false);
    }

    static byte[] getBytesNoRepl(String s, Charset cs) throws CharacterCodingException {
        try {
            return getBytesNoRepl1(s, cs);
        } catch (IllegalArgumentException e) {
            //getBytesNoRepl1 throws IAE with UnmappableCharacterException or CCE as the cause
            Throwable cause = e.getCause();
            if (cause instanceof UnmappableCharacterException) {
                throw (UnmappableCharacterException)cause;
            }
            throw (CharacterCodingException)cause;
        }
    }

    private static byte[] getBytesNoRepl1(String s, Charset cs) {
        if (cs == UTF_8.INSTANCE) {
            return encodeUTF8(s, false);
        }
        if (cs == ISO_8859_1.INSTANCE) {
            return encode8859_1(s, false);
        }
        if (cs == US_ASCII.INSTANCE) {
            return encodeASCII(s, false);
        }
        return encodeWithEncoder(cs, s, false);
    }

    static int decodeASCII(byte[] sa, int sp, char[] da, int dp, int len) {
        int count = 0;
        while (count < len) {
            byte b = sa[sp + count];
            if (b < 0) {
                break;
            }
            da[dp + count] = (char)b;
            count++;
        }
        return count;
    }

    //------------------------------ decoding ------------------------------

    private static Charset lookupCharset(String csn) throws UnsupportedEncodingException {
        Objects.requireNonNull(csn);
        try {
            return Charset.forName(csn);
        } catch (UnsupportedCharsetException | IllegalCharsetNameException x) {
            throw new UnsupportedEncodingException(csn);
        }
    }

    private static int scale(int len, float expansionFactor) {
        // We need to perform double, not float, arithmetic; otherwise
        // we lose low order bits when len is larger than 2**24.
        return (int)(len * (double)expansionFactor);
    }

    private static boolean hasNegatives(byte[] ba, int off, int len) {
        for (int i = off, limit = off + len; i < limit; i++) {
            if (ba[i] < 0) {
                return true;
            }
        }
        return false;
    }

    // String(Charset, byte[], int, int)
    private static String decode(Charset charset, byte[] bytes, int offset, int length) {
        if (length == 0) {
            return "";
        }
        if (charset == UTF_8.INSTANCE) {
            char[] dst = new char[length];
            int dp = decodeUTF8_UTF16(bytes, offset, offset + length, dst, 0, true);
            return new String(dst, 0, dp);
        }
        if (charset == ISO_8859_1.INSTANCE) {
            return fromLatin1(bytes, offset, length);
        }
        if (charset == US_ASCII.INSTANCE) {
            char[] dst = new char[length];
            for (int dp = 0; dp < length; dp++) {
                int b = bytes[offset++];
                dst[dp] = (b >= 0) ? (char)b : REPL;
            }
            return new String(dst);
        }

        // decode using CharsetDecoder
        CharsetDecoder cd = charset.newDecoder();
        int en = scale(length, cd.maxCharsPerByte());
        cd.onMalformedInput(CodingErrorAction.REPLACE)
                .onUnmappableCharacter(CodingErrorAction.REPLACE);
        char[] ca = new char[en];
        int caLen;
        try {
            caLen = decodeWithDecoder(cd, ca, bytes, offset, length);
        } catch (CharacterCodingException x) {
            // Substitution is enabled, so this shouldn't happen
            throw new Error(x);
        }
        return new String(ca, 0, caLen);
    }

    private static int decodeWithDecoder(CharsetDecoder cd, char[] dst, byte[] src, int offset, int length)
            throws CharacterCodingException {
        ByteBuffer bb = ByteBuffer.wrap(src, offset, length);
        CharBuffer cb = CharBuffer.wrap(dst, 0, dst.length);
        CoderResult cr = cd.decode(bb, cb, true);
        if (!cr.isUnderflow())
            cr.throwException();
        cr = cd.flush(cb);
        if (!cr.isUnderflow())
            cr.throwException();
        return cb.position();
    }

    private static boolean isNotContinuation(int b) {
        return (b & 0xc0) != 0x80;
    }

    private static boolean isMalformed3(int b1, int b2, int b3) {
        return (b1 == (byte)0xe0 && (b2 & 0xe0) == 0x80) ||
                (b2 & 0xc0) != 0x80 || (b3 & 0xc0) != 0x80;
    }

    private static boolean isMalformed3_2(int b1, int b2) {
        return (b1 == (byte)0xe0 && (b2 & 0xe0) == 0x80) ||
                (b2 & 0xc0) != 0x80;
    }

    private static boolean isMalformed4(int b2, int b3, int b4) {
        return (b2 & 0xc0) != 0x80 || (b3 & 0xc0) != 0x80 ||
                (b4 & 0xc0) != 0x80;
    }

    private static boolean isMalformed4_2(int b1, int b2) {
        return (b1 == 0xf0 && (b2 < 0x90 || b2 > 0xbf)) ||
                (b1 == 0xf4 && (b2 & 0xf0) != 0x80) ||
                (b2 & 0xc0) != 0x80;
    }

    private static boolean isMalformed4_3(int b3) {
        return (b3 & 0xc0) != 0x80;
    }

    private static char decode2(int b1, int b2) {
        return (char)(((b1 << 6) ^ b2) ^
                (((byte) 0xC0 << 6) ^
                        ((byte) 0x80 << 0)));
    }

    private static char decode3(int b1, int b2, int b3) {
        return (char)((b1 << 12) ^
                (b2 <<  6) ^
                (b3 ^
                        (((byte) 0xE0 << 12) ^
                                ((byte) 0x80 <<  6) ^
                                ((byte) 0x80 <<  0))));
    }

    private static int decode4(int b1, int b2, int b3, int b4) {
        return ((b1 << 18) ^
                (b2 << 12) ^
                (b3 <<  6) ^
                (b4 ^
                        (((byte) 0xF0 << 18) ^
                                ((byte) 0x80 << 12) ^
                                ((byte) 0x80 <<  6) ^
                                ((byte) 0x80 <<  0))));
    }

    private static int decodeUTF8_UTF16(byte[] src, int sp, int sl, char[] dst, int dp, boolean doReplace) {
        while (sp < sl) {
            int b1 = src[sp++];
            if (b1 >= 0) {
                dst[dp++] = (char)b1;
            } else if ((b1 >> 5) == -2 && (b1 & 0x1e) != 0) {
                if (sp < sl) {
                    int b2 = src[sp++];
                    if (isNotContinuation(b2)) {
                        if (!doReplace) {
                            throwMalformed(sp - 1, 1);
                        }
                        dst[dp++] = REPL;
                        sp--;
                    } else {
                        dst[dp++] = decode2(b1, b2);
                    }
                    continue;
                }
                if (!doReplace) {
                    throwMalformed(sp, 1);  // underflow()
                }
                dst[dp++] = REPL;
                break;
            } else if ((b1 >> 4) == -2) {
                if (sp + 1 < sl) {
                    int b2 = src[sp++];
                    int b3 = src[sp++];
                    if (isMalformed3(b1, b2, b3)) {
                        if (!doReplace) {
                            throwMalformed(sp - 3, 3);
                        }
                        dst[dp++] = REPL;
                        sp -= 3;
                        sp += malformed3(src, sp);
                    } else {
                        char c = decode3(b1, b2, b3);
                        if (Character.isSurrogate(c)) {
                            if (!doReplace) {
                                throwMalformed(sp - 3, 3);
                            }
                            dst[dp++] = REPL;
                        } else {
                            dst[dp++] = c;
                        }
                    }
                    continue;
                }
                if (sp < sl && isMalformed3_2(b1, src[sp])) {
                    if (!doReplace) {
                        throwMalformed(sp - 1, 2);
                    }
                    dst[dp++] = REPL;
                    continue;
                }
                if (!doReplace) {
                    throwMalformed(sp, 1);
                }
                dst[dp++] = REPL;
                break;
            } else if ((b1 >> 3) == -2) {
                if (sp + 2 < sl) {
                    int b2 = src[sp++];
                    int b3 = src[sp++];
                    int b4 = src[sp++];
                    int uc = decode4(b1, b2, b3, b4);
                    if (isMalformed4(b2, b3, b4) ||
                            !Character.isSupplementaryCodePoint(uc)) { // shortest form check
                        if (!doReplace) {
                            throwMalformed(sp - 4, 4);
                        }
                        dst[dp++] = REPL;
                        sp -= 4;
                        sp += malformed4(src, sp);
                    } else {
                        dst[dp++] = Character.highSurrogate(uc);
                        dst[dp++] = Character.lowSurrogate(uc);
                    }
                    continue;
                }
                b1 &= 0xff;
                if (b1 > 0xf4 || sp < sl && isMalformed4_2(b1, src[sp] & 0xff)) {
                    if (!doReplace) {
                        throwMalformed(sp - 1, 1);  // or 2
                    }
                    dst[dp++] = REPL;
                    continue;
                }
                if (!doReplace) {
                    throwMalformed(sp - 1, 1);
                }
                sp++;
                dst[dp++] = REPL;
                if (sp < sl && isMalformed4_3(src[sp])) {
                    continue;
                }
                break;
            } else {
                if (!doReplace) {
                    throwMalformed(sp - 1, 1);
                }
                dst[dp++] = REPL;
            }
        }
        return dp;
    }

    private static int malformed3(byte[] src, int sp) {
        int b1 = src[sp++];
        int b2 = src[sp];    // no need to lookup b3
        return ((b1 == (byte)0xe0 && (b2 & 0xe0) == 0x80) ||
                isNotContinuation(b2)) ? 1 : 2;
    }

    private static int malformed4(byte[] src, int sp) {
        // we don't care the speed here
        int b1 = src[sp++] & 0xff;
        int b2 = src[sp++] & 0xff;
        if (b1 > 0xf4 ||
                (b1 == 0xf0 && (b2 < 0x90 || b2 > 0xbf)) ||
                (b1 == 0xf4 && (b2 & 0xf0) != 0x80) ||
                isNotContinuation(b2))
            return 1;
        if (isNotContinuation(src[sp]))
            return 2;
        return 3;
    }

    private static void throwMalformed(int off, int nb) {
        String msg = "malformed input off : " + off + ", length : " + nb;
        throw new IllegalArgumentException(msg, new MalformedInputException(nb));
    }

    private static void throwMalformed(byte[] val) {
        int dp = 0;
        while (dp < val.length && val[dp] >= 0) {
            dp++;
        }
        throwMalformed(dp, 1);
    }

    private static void throwUnmappable(int off) {
        String msg = "malformed input off : " + off + ", length : 1";
        throw new IllegalArgumentException(msg, new UnmappableCharacterException(1));
    }

    //------------------------------ encoding ------------------------------

    private static byte[] encode(Charset cs, String s) {
        if (cs == UTF_8.INSTANCE) {
            return encodeUTF8(s, true);
        }
        if (cs == ISO_8859_1.INSTANCE) {
            return encode8859_1(s, true);
        }
        if (cs == US_ASCII.INSTANCE) {
            return encodeASCII(s, true);
        }
        return encodeWithEncoder(cs, s, true);
    }

    private static byte[] encodeWithEncoder(Charset cs, String s, boolean doReplace) {
        CharsetEncoder ce = cs.newEncoder();
        int len = s.length();
        int en = scale(len, ce.maxBytesPerChar());
        byte[] ba = new byte[en];
        if (len == 0) {
            return ba;
        }
        if (doReplace) {
            ce.onMalformedInput(CodingErrorAction.REPLACE)
                    .onUnmappableCharacter(CodingErrorAction.REPLACE);
        }
        ByteBuffer bb = ByteBuffer.wrap(ba);
        CharBuffer cb = CharBuffer.wrap(s.toCharArray(), 0, len);
        try {
            CoderResult cr = ce.encode(cb, bb, true);
            if (!cr.isUnderflow())
                cr.throwException();
            cr = ce.flush(bb);
            if (!cr.isUnderflow())
                cr.throwException();
        } catch (CharacterCodingException x) {
            if (!doReplace) {
                throw new IllegalArgumentException(x);
            } else {
                throw new Error(x);
            }
        }
        return trimArray(ba, bb.position());
    }

    private static byte[] trimArray(byte[] ba, int len) {
        if (len == ba.length) {
            return ba;
        } else {
            return java.util.Arrays.copyOf(ba, len);
        }
    }

    private static byte[] encodeASCII(String s, boolean doReplace) {
        int len = s.length();
        byte[] dst = new byte[len];
        int dp = 0;
        for (int i = 0; i < len; i++) {
            char c = s.charAt(i);
            if (c < 0x80) {
                dst[dp++] = (byte)c;
                continue;
            }
            if (!doReplace) {
                throwUnmappable(i);
            }
            if (Character.isHighSurrogate(c) && i + 1 < len &&
                    Character.isLowSurrogate(s.charAt(i + 1))) {
                i++;
            }
            dst[dp++] = '?';
        }
        return trimArray(dst, dp);
    }

    private static byte[] encode8859_1(String s, boolean doReplace) {
        int len = s.length();
        byte[] dst = new byte[len];
        int dp = 0;
        for (int sp = 0; sp < len; sp++) {
            char c = s.charAt(sp);
            if (c <= 'ÿ') {
                dst[dp++] = (byte)c;
                continue;
            }
            if (!doReplace) {
                throwUnmappable(sp);
            }
            if (Character.isHighSurrogate(c) && sp + 1 < len &&
                    Character.isLowSurrogate(s.charAt(sp + 1))) {
                sp++;
            }
            dst[dp++] = '?';
        }
        return trimArray(dst, dp);
    }

    private static byte[] encodeUTF8(String s, boolean doReplace) {
        int dp = 0;
        int sp = 0;
        int sl = s.length();
        // UTF-8 encoded can be as much as 3 times the string length
        // For very large estimate, (as in overflow of 32 bit int), precompute the exact size
        long allocLen = (sl * 3 < 0) ? computeSizeUTF8_UTF16(s, doReplace) : sl * 3;
        if (allocLen > (long)Integer.MAX_VALUE) {
            throw new OutOfMemoryError("Required length exceeds implementation limit");
        }
        byte[] dst = new byte[(int) allocLen];
        while (sp < sl) {
            // ascii fast loop;
            char c = s.charAt(sp);
            if (c >= '\u0080') {
                break;
            }
            dst[dp++] = (byte)c;
            sp++;
        }
        while (sp < sl) {
            char c = s.charAt(sp++);
            if (c < 0x80) {
                dst[dp++] = (byte)c;
            } else if (c < 0x800) {
                dst[dp++] = (byte)(0xc0 | (c >> 6));
                dst[dp++] = (byte)(0x80 | (c & 0x3f));
            } else if (Character.isSurrogate(c)) {
                int uc = -1;
                char c2;
                if (Character.isHighSurrogate(c) && sp < sl &&
                        Character.isLowSurrogate(c2 = s.charAt(sp))) {
                    uc = Character.toCodePoint(c, c2);
                }
                if (uc < 0) {
                    if (doReplace) {
                        dst[dp++] = '?';
                    } else {
                        throwUnmappable(sp - 1);
                    }
                } else {
                    dst[dp++] = (byte)(0xf0 | ((uc >> 18)));
                    dst[dp++] = (byte)(0x80 | ((uc >> 12) & 0x3f));
                    dst[dp++] = (byte)(0x80 | ((uc >>  6) & 0x3f));
                    dst[dp++] = (byte)(0x80 | (uc & 0x3f));
                    sp++;  // 2 chars
                }
            } else {
                // 3 bytes, 16 bits
                dst[dp++] = (byte)(0xe0 | ((c >> 12)));
                dst[dp++] = (byte)(0x80 | ((c >>  6) & 0x3f));
                dst[dp++] = (byte)(0x80 | (c & 0x3f));
            }
        }
        return trimArray(dst, dp);
    }

    private static long computeSizeUTF8_UTF16(String s, boolean doReplace) {
        long dp = 0L;
        int sp = 0;
        int sl = s.length();
        while (sp < sl) {
            char c = s.charAt(sp++);
            if (c < 0x80) {
                dp++;
            } else if (c < 0x800) {
                dp += 2;
            } else if (Character.isSurrogate(c)) {
                int uc = -1;
                char c2;
                if (Character.isHighSurrogate(c) && sp < sl &&
                        Character.isLowSurrogate(c2 = s.charAt(sp))) {
                    uc = Character.toCodePoint(c, c2);
                }
                if (uc < 0) {
                    if (doReplace) {
                        dp++;
                    } else {
                        throwUnmappable(sp - 1);
                    }
                } else {
                    dp += 4;
                    sp++;  // 2 chars
                }
            } else {
                // 3 bytes, 16 bits
                dp += 3;
            }
        }
        return dp;
    }

    //------------------------------ public instance methods ------------------------------

    static int codePointAt(String _this, int index) {
        checkIndex(index, _this.length());
        char c1 = _this.charAt(index++);
        if (Character.isHighSurrogate(c1)) {
            if (index < _this.length()) {
                char c2 = _this.charAt(index);
                if (Character.isLowSurrogate(c2)) {
                    return Character.toCodePoint(c1, c2);
                }
            }
        }
        return c1;
    }

    static int codePointBefore(String _this, int index) {
        int i = index - 1;
        checkIndex(i, _this.length());
        char c2 = _this.charAt(--index);
        if (Character.isLowSurrogate(c2)) {
            if (index > 0) {
                char c1 = _this.charAt(--index);
                if (Character.isHighSurrogate(c1)) {
                    return Character.toCodePoint(c1, c2);
                }
            }
        }
        return c2;
    }

    static int codePointCount(String _this, int beginIndex, int endIndex) {
        Objects.checkFromToIndex(beginIndex, endIndex, _this.length());
        int n = 0;
        for (int i = beginIndex; i < endIndex; ) {
            n++;
            if (Character.isHighSurrogate(_this.charAt(i++))) {
                if (i < endIndex && Character.isLowSurrogate(_this.charAt(i))) {
                    i++;
                }
            }
        }
        return n;
    }

    static int offsetByCodePoints(String _this, int index, int codePointOffset) {
        return Character.offsetByCodePoints(_this, index, codePointOffset);
    }

    static void getChars(cli.System.String _this, int srcBegin, int srcEnd, char[] dst, int dstBegin) {
        checkBoundsBeginEnd(srcBegin, srcEnd, _this.get_Length());
        checkBoundsOffCount(dstBegin, srcEnd - srcBegin, dst.length);
        _this.CopyTo(srcBegin, dst, dstBegin, srcEnd - srcBegin);
    }

    @SuppressWarnings("deprecation")
    static void getBytes(String _this, int srcBegin, int srcEnd, byte[] dst, int dstBegin) {
        checkBoundsBeginEnd(srcBegin, srcEnd, _this.length());
        Objects.requireNonNull(dst);
        checkBoundsOffCount(dstBegin, srcEnd - srcBegin, dst.length);
        int j = dstBegin;
        for (int i = srcBegin; i < srcEnd; i++) {
            dst[j++] = (byte)_this.charAt(i);
        }
    }

    static byte[] getBytes(String _this, String charsetName) throws UnsupportedEncodingException {
        return encode(lookupCharset(charsetName), _this);
    }

    static byte[] getBytes(String _this, Charset charset) {
        if (charset == null) throw new NullPointerException();
        return encode(charset, _this);
    }

    static byte[] getBytes(String _this) {
        return encode(Charset.defaultCharset(), _this);
    }

    static boolean contentEquals(String _this, StringBuffer sb) {
        return contentEquals(_this, (CharSequence)sb);
    }

    static boolean contentEquals(String _this, CharSequence cs) {
        // Argument is a StringBuffer, StringBuilder
        if (cs instanceof AbstractStringBuilder) {
            if (cs instanceof StringBuffer) {
                synchronized(cs) {
                    return nonSyncContentEquals(_this, cs);
                }
            } else {
                return nonSyncContentEquals(_this, cs);
            }
        }
        // Argument is a String
        if (cs instanceof String) {
            return _this.equals(cs);
        }
        return nonSyncContentEquals(_this, cs);
    }

    private static boolean nonSyncContentEquals(String _this, CharSequence cs) {
        int n = _this.length();
        if (n != cs.length()) {
            return false;
        }
        for (int i = 0; i < n; i++) {
            if (_this.charAt(i) != cs.charAt(i)) {
                return false;
            }
        }
        return true;
    }

    static boolean equalsIgnoreCase(String _this, String anotherString) {
        return (_this == anotherString) ? true
                : (anotherString != null)
                && (anotherString.length() == _this.length())
                && regionMatches(_this, true, 0, anotherString, 0, _this.length());
    }

    static int compareTo(String _this, String anotherString) {
        int len = Math.min(_this.length(), anotherString.length());
        for (int i = 0; i < len; i++) {
            int diff = _this.charAt(i) - anotherString.charAt(i);
            if (diff != 0) {
                return diff;
            }
        }
        return _this.length() - anotherString.length();
    }

    static int compareToIgnoreCase(String _this, String str) {
        return compareToCI(_this, 0, _this.length(), str, 0, str.length());
    }

    static boolean regionMatches(String _this, int toffset, String other, int ooffset, int len) {
        // Note: toffset, ooffset, or len might be near -1>>>1.
        if ((ooffset < 0) || (toffset < 0) ||
                (toffset > (long)_this.length() - len) ||
                (ooffset > (long)other.length() - len)) {
            return false;
        }
        while (len-- > 0) {
            if (_this.charAt(toffset++) != other.charAt(ooffset++)) {
                return false;
            }
        }
        return true;
    }

    static boolean regionMatches(String _this, boolean ignoreCase, int toffset, String other, int ooffset, int len) {
        if (!ignoreCase) {
            return regionMatches(_this, toffset, other, ooffset, len);
        }
        // Note: toffset, ooffset, or len might be near -1>>>1.
        if ((ooffset < 0) || (toffset < 0)
                || (toffset > (long)_this.length() - len)
                || (ooffset > (long)other.length() - len)) {
            return false;
        }
        return compareToCI(_this, toffset, len, other, ooffset, len) == 0;
    }

    // StringUTF16.compareToCIImpl
    private static int compareToCI(String value, int toffset, int tlen, String other, int ooffset, int olen) {
        int tlast = toffset + tlen;
        int olast = ooffset + olen;
        for (int k1 = toffset, k2 = ooffset; k1 < tlast && k2 < olast; k1++, k2++) {
            int cp1 = value.charAt(k1);
            int cp2 = other.charAt(k2);

            if (cp1 == cp2 || compareCodePointCI(cp1, cp2) == 0) {
                continue;
            }

            // Check for supplementary characters case
            cp1 = codePointIncluding(value, cp1, k1, toffset, tlast);
            if (cp1 < 0) {
                k1++;
                cp1 = -cp1;
            }
            cp2 = codePointIncluding(other, cp2, k2, ooffset, olast);
            if (cp2 < 0) {
                k2++;
                cp2 = -cp2;
            }

            int diff = compareCodePointCI(cp1, cp2);
            if (diff != 0) {
                return diff;
            }
        }
        return tlen - olen;
    }

    // Case insensitive comparison of two code points
    private static int compareCodePointCI(int cp1, int cp2) {
        // try converting both characters to uppercase.
        // If the results match, then the comparison scan should
        // continue.
        cp1 = Character.toUpperCase(cp1);
        cp2 = Character.toUpperCase(cp2);
        if (cp1 != cp2) {
            // Unfortunately, conversion to uppercase does not work properly
            // for the Georgian alphabet, which has strange rules about case
            // conversion.  So we need to make one last check before
            // exiting.
            cp1 = Character.toLowerCase(cp1);
            cp2 = Character.toLowerCase(cp2);
            if (cp1 != cp2) {
                return cp1 - cp2;
            }
        }
        return 0;
    }

    // Returns a code point from the code unit pointed by "index". If it is
    // not a surrogate or an unpaired surrogate, then the code unit is
    // returned as is. Otherwise, it is combined with the code unit before
    // or after, depending on the type of the surrogate at index, to make a
    // supplementary code point. The return value will be negated if the code
    // unit pointed by index is a high surrogate, and index + 1 is a low surrogate.
    private static int codePointIncluding(String ba, int cp, int index, int start, int end) {
        // fast check
        if (!Character.isSurrogate((char)cp)) {
            return cp;
        }
        if (Character.isLowSurrogate((char)cp)) {
            if (index > start) {
                char c = ba.charAt(index - 1);
                if (Character.isHighSurrogate(c)) {
                    return Character.toCodePoint(c, (char)cp);
                }
            }
        } else if (index + 1 < end) { // cp == high surrogate
            char c = ba.charAt(index + 1);
            if (Character.isLowSurrogate(c)) {
                // negate the code point
                return - Character.toCodePoint((char)cp, c);
            }
        }
        return cp;
    }

    static int hashCode(cli.System.String _this) {
        int h = 0;
        // NOTE having the get_Length in the for condition is actually faster than hoisting it,
        // the CLR JIT recognizes this pattern and optimizes the array bounds check in get_Chars.
        for (int i = 0; i < _this.get_Length(); i++) {
            h = h * 31 + _this.get_Chars(i);
        }
        return h;
    }

    static int indexOf(cli.System.String _this, int ch) {
        return indexOf(_this, ch, 0);
    }

    static int indexOf(cli.System.String _this, int ch, int fromIndex) {
        return indexOf(_this, ch, Math.max(fromIndex, 0), _this.get_Length());
    }

    // indexOf(int ch, int beginIndex, int endIndex), with checked bounds
    static int indexOfRange(cli.System.String _this, int ch, int beginIndex, int endIndex) {
        checkBoundsBeginEnd(beginIndex, endIndex, _this.get_Length());
        return indexOf(_this, ch, beginIndex, endIndex);
    }

    private static int indexOf(cli.System.String _this, int ch, int fromIndex, int max) {
        if (fromIndex >= max) {
            return -1;
        }
        if (ch < Character.MIN_SUPPLEMENTARY_CODE_POINT) {
            // handle most cases here (ch is a BMP code point or a
            // negative value (invalid code point))
            for (int i = fromIndex; i < max; i++) {
                if (_this.get_Chars(i) == ch) {
                    return i;
                }
            }
            return -1;
        }
        if (Character.isValidCodePoint(ch)) {
            final char hi = Character.highSurrogate(ch);
            final char lo = Character.lowSurrogate(ch);
            for (int i = fromIndex; i < max - 1; i++) {
                if (_this.get_Chars(i) == hi && _this.get_Chars(i + 1) == lo) {
                    return i;
                }
            }
        }
        return -1;
    }

    static int lastIndexOf(cli.System.String _this, int ch) {
        return lastIndexOf(_this, ch, _this.get_Length() - 1);
    }

    static int lastIndexOf(cli.System.String _this, int ch, int fromIndex) {
        if (ch < Character.MIN_SUPPLEMENTARY_CODE_POINT) {
            // handle most cases here (ch is a BMP code point or a
            // negative value (invalid code point))
            int i = Math.min(fromIndex, _this.get_Length() - 1);
            for (; i >= 0; i--) {
                if (_this.get_Chars(i) == ch) {
                    return i;
                }
            }
            return -1;
        }
        if (Character.isValidCodePoint(ch)) {
            char hi = Character.highSurrogate(ch);
            char lo = Character.lowSurrogate(ch);
            int i = Math.min(fromIndex, _this.get_Length() - 2);
            for (; i >= 0; i--) {
                if (_this.get_Chars(i) == hi && _this.get_Chars(i + 1) == lo) {
                    return i;
                }
            }
        }
        return -1;
    }

    static int indexOf(String _this, String str) {
        return indexOf(_this, str, 0);
    }

    static int indexOf(String _this, String str, int fromIndex) {
        return indexOf(_this, str, fromIndex, _this.length());
    }

    // indexOf(String str, int beginIndex, int endIndex), with checked bounds
    static int indexOfRange(String _this, String str, int beginIndex, int endIndex) {
        if (str.length() == 1) {
            return indexOfRange((cli.System.String)(Object)_this, str.charAt(0), beginIndex, endIndex);
        }
        checkBoundsBeginEnd(beginIndex, endIndex, _this.length());
        return indexOf(_this, str, beginIndex, endIndex);
    }

    private static int indexOf(String _this, String str, int fromIndex, int slen) {
        fromIndex = Math.clamp(fromIndex, 0, slen);
        int olen = str.length();
        if (olen > slen - fromIndex) {
            return -1;
        }
        if (olen == 0) {
            return fromIndex;
        }
        char firstChar = str.charAt(0);
        int index = fromIndex;
        int end = slen - olen;
        while (index >= 0 && index <= end) {
            if (cli.System.String.CompareOrdinal(_this, index, str, 0, olen) == 0) {
                return index;
            }
            index = _this.indexOf(firstChar, index + 1);
        }
        return -1;
    }

    static int lastIndexOf(String _this, String str) {
        return lastIndexOf(_this, str, _this.length());
    }

    static int lastIndexOf(String _this, String str, int fromIndex) {
        int slen = _this.length();
        int olen = str.length();
        int rightIndex = slen - olen;
        if (fromIndex > rightIndex) {
            fromIndex = rightIndex;
        }
        if (fromIndex < 0) {
            return -1;
        }
        if (olen == 0) {
            return fromIndex;
        }
        cli.System.String cliStr = (cli.System.String)(Object)_this;
        char firstChar = str.charAt(0);
        int index = fromIndex;
        while (index > 0) {
            if (cli.System.String.CompareOrdinal(_this, index, str, 0, olen) == 0) {
                return index;
            }
            index = cliStr.LastIndexOf(firstChar, index - 1);
        }
        return index == 0 && cli.System.String.CompareOrdinal(_this, 0, str, 0, olen) == 0 ? 0 : -1;
    }

    static String substring(cli.System.String _this, int beginIndex) {
        return substring(_this, beginIndex, _this.get_Length());
    }

    static String substring(cli.System.String _this, int beginIndex, int endIndex) {
        int length = _this.get_Length();
        checkBoundsBeginEnd(beginIndex, endIndex, length);
        if (beginIndex == 0 && endIndex == length) {
            return (String)(Object)_this;
        }
        return _this.Substring(beginIndex, endIndex - beginIndex);
    }

    static String concat(String _this, String str) {
        if (str.isEmpty()) {
            return _this;
        }
        if (_this.isEmpty()) {
            return str;
        }
        return cli.System.String.Concat(_this, str);
    }

    static String replace(String _this, char oldChar, char newChar) {
        if (oldChar != newChar) {
            int len = _this.length();
            int i = _this.indexOf(oldChar);
            if (i >= 0) {
                char[] buf = new char[len];
                _this.getChars(0, i, buf, 0);
                while (i < len) {
                    char c = _this.charAt(i);
                    buf[i] = (c == oldChar) ? newChar : c;
                    i++;
                }
                return new String(buf);
            }
        }
        return _this;
    }

    static boolean contains(String _this, CharSequence s) {
        return indexOf(_this, s.toString()) >= 0;
    }

    static String replace(String _this, CharSequence target, CharSequence replacement) {
        String trgtStr = target.toString();
        String replStr = replacement.toString();
        int thisLen = _this.length();
        int trgtLen = trgtStr.length();
        int replLen = replStr.length();

        if (trgtLen > 0) {
            if (trgtLen == 1 && replLen == 1) {
                return replace(_this, trgtStr.charAt(0), replStr.charAt(0));
            }

            int j = indexOf(_this, trgtStr, 0);
            if (j < 0) {
                return _this;
            }
            StringBuilder sb = new StringBuilder(thisLen + Math.max(0, replLen - trgtLen));
            int i = 0;
            do {
                sb.append(_this, i, j).append(replStr);
                i = j + trgtLen;
            } while (j < thisLen && (j = indexOf(_this, trgtStr, i)) > 0);
            return sb.append(_this, i, thisLen).toString();
        } else { // trgtLen == 0
            long resultLen = thisLen + (thisLen + 1L) * replLen;
            if (resultLen > Integer.MAX_VALUE) {
                throw new OutOfMemoryError("Required length exceeds implementation limit");
            }
            StringBuilder sb = new StringBuilder((int)resultLen);
            sb.append(replStr);
            for (int i = 0; i < thisLen; i++) {
                sb.append(_this.charAt(i)).append(replStr);
            }
            return sb.toString();
        }
    }

    static String[] split(String _this, String regex, int limit) {
        return split(_this, regex, limit, false);
    }

    static String[] splitWithDelimiters(String _this, String regex, int limit) {
        return split(_this, regex, limit, true);
    }

    private static String[] split(String _this, String regex, int limit, boolean withDelimiters) {
        /* fastpath if the regex is a
         * (1) one-char String and this character is not one of the
         *     RegEx's meta characters ".$|()[{^?*+\\", or
         * (2) two-char String and the first char is the backslash and
         *     the second is not the ascii digit or ascii letter.
         */
        char ch = 0;
        if (((regex.length() == 1 &&
                ".$|()[{^?*+\\".indexOf(ch = regex.charAt(0)) == -1) ||
                (regex.length() == 2 &&
                        regex.charAt(0) == '\\' &&
                        (((ch = regex.charAt(1))-'0')|('9'-ch)) < 0 &&
                        ((ch-'a')|('z'-ch)) < 0 &&
                        ((ch-'A')|('Z'-ch)) < 0)) &&
                (ch < Character.MIN_HIGH_SURROGATE ||
                        ch > Character.MAX_LOW_SURROGATE))
        {
            return split(_this, ch, limit, withDelimiters);
        }
        Pattern pattern = Pattern.compile(regex);
        return withDelimiters
                ? pattern.splitWithDelimiters(_this, limit)
                : pattern.split(_this, limit);
    }

    private static String[] split(String _this, char ch, int limit, boolean withDelimiters) {
        int matchCount = 0;
        int off = 0;
        int next;
        boolean limited = limit > 0;
        ArrayList<String> list = new ArrayList<>();
        String del = withDelimiters ? String.valueOf(ch) : null;
        while ((next = _this.indexOf(ch, off)) != -1) {
            if (!limited || matchCount < limit - 1) {
                list.add(_this.substring(off, next));
                if (withDelimiters) {
                    list.add(del);
                }
                off = next + 1;
                ++matchCount;
            } else {    // last one
                int last = _this.length();
                list.add(_this.substring(off, last));
                off = last;
                ++matchCount;
                break;
            }
        }
        // If no match was found, return this
        if (off == 0)
            return new String[] {_this};

        // Add remaining segment
        if (!limited || matchCount < limit)
            list.add(_this.substring(off, _this.length()));

        // Construct result
        int resultSize = list.size();
        if (limit == 0) {
            while (resultSize > 0 && list.get(resultSize - 1).isEmpty()) {
                resultSize--;
            }
        }
        String[] result = new String[resultSize];
        return list.subList(0, resultSize).toArray(result);
    }

    public static String join(CharSequence delimiter, CharSequence... elements) {
        var delim = delimiter.toString();
        var elems = new String[elements.length];
        for (int i = 0; i < elements.length; i++) {
            elems[i] = String.valueOf(elements[i]);
        }
        return join("", "", delim, elems, elems.length);
    }

    public static String join(CharSequence delimiter, Iterable<? extends CharSequence> elements) {
        Objects.requireNonNull(delimiter);
        Objects.requireNonNull(elements);
        var delim = delimiter.toString();
        var elems = new String[8];
        int size = 0;
        for (CharSequence cs: elements) {
            if (size >= elems.length) {
                elems = java.util.Arrays.copyOf(elems, elems.length << 1);
            }
            elems[size++] = String.valueOf(cs);
        }
        return join("", "", delim, elems, size);
    }

    static String toLowerCase(String _this, Locale locale) {
        if (locale == null) {
            throw new NullPointerException();
        }

        int firstUpper;
        final int len = _this.length();

        /* Now check if there are any characters that need to be changed. */
        scan: {
            for (firstUpper = 0 ; firstUpper < len; ) {
                char c = _this.charAt(firstUpper);
                if ((c >= Character.MIN_HIGH_SURROGATE)
                        && (c <= Character.MAX_HIGH_SURROGATE)) {
                    int supplChar = _this.codePointAt(firstUpper);
                    if (supplChar != Character.toLowerCase(supplChar)) {
                        break scan;
                    }
                    firstUpper += Character.charCount(supplChar);
                } else {
                    if (c != Character.toLowerCase(c)) {
                        break scan;
                    }
                    firstUpper++;
                }
            }
            return _this;
        }

        char[] result = new char[len];
        int resultOffset = 0;  /* result may grow, so i+resultOffset
                                * is the write location in result */

        /* Just copy the first few lowerCase characters. */
        _this.getChars(0, firstUpper, result, 0);

        String lang = locale.getLanguage();
        boolean localeDependent =
                (lang == "tr" || lang == "az" || lang == "lt");
        char[] lowerCharArray;
        int lowerChar;
        int srcChar;
        int srcCount;
        for (int i = firstUpper; i < len; i += srcCount) {
            srcChar = (int)_this.charAt(i);
            if ((char)srcChar >= Character.MIN_HIGH_SURROGATE
                    && (char)srcChar <= Character.MAX_HIGH_SURROGATE) {
                srcChar = _this.codePointAt(i);
                srcCount = Character.charCount(srcChar);
            } else {
                srcCount = 1;
            }
            if (localeDependent ||
                srcChar == 'Σ' || // GREEK CAPITAL LETTER SIGMA
                srcChar == 'İ') { // LATIN CAPITAL LETTER I WITH DOT ABOVE
                lowerChar = ConditionalSpecialCasing.toLowerCaseEx(_this, i, locale);
            } else {
                lowerChar = Character.toLowerCase(srcChar);
            }
            if ((lowerChar == Character.ERROR)
                    || (lowerChar >= Character.MIN_SUPPLEMENTARY_CODE_POINT)) {
                if (lowerChar == Character.ERROR) {
                    lowerCharArray =
                            ConditionalSpecialCasing.toLowerCaseCharArray(_this, i, locale);
                } else if (srcCount == 2) {
                    resultOffset += Character.toChars(lowerChar, result, i + resultOffset) - srcCount;
                    continue;
                } else {
                    lowerCharArray = Character.toChars(lowerChar);
                }

                /* Grow result if needed */
                int mapLen = lowerCharArray.length;
                if (mapLen > srcCount) {
                    char[] result2 = new char[result.length + mapLen - srcCount];
                    System.arraycopy(result, 0, result2, 0, i + resultOffset);
                    result = result2;
                }
                for (int x = 0; x < mapLen; ++x) {
                    result[i + resultOffset + x] = lowerCharArray[x];
                }
                resultOffset += (mapLen - srcCount);
            } else {
                result[i + resultOffset] = (char)lowerChar;
            }
        }
        return new String(result, 0, len + resultOffset);
    }

    static String toLowerCase(String _this) {
        return toLowerCase(_this, Locale.getDefault());
    }

    static String toUpperCase(String _this, Locale locale) {
        if (locale == null) {
            throw new NullPointerException();
        }

        int firstLower;
        final int len = _this.length();

        /* Now check if there are any characters that need to be changed. */
        scan: {
            for (firstLower = 0 ; firstLower < len; ) {
                int c = (int)_this.charAt(firstLower);
                int srcCount;
                if ((c >= Character.MIN_HIGH_SURROGATE)
                        && (c <= Character.MAX_HIGH_SURROGATE)) {
                    c = _this.codePointAt(firstLower);
                    srcCount = Character.charCount(c);
                } else {
                    srcCount = 1;
                }
                int upperCaseChar = Character.toUpperCaseEx(c);
                if ((upperCaseChar == Character.ERROR)
                        || (c != upperCaseChar)) {
                    break scan;
                }
                firstLower += srcCount;
            }
            return _this;
        }

        /* result may grow, so i+resultOffset is the write location in result */
        int resultOffset = 0;
        char[] result = new char[len]; /* may grow */

        /* Just copy the first few upperCase characters. */
        _this.getChars(0, firstLower, result, 0);

        String lang = locale.getLanguage();
        boolean localeDependent =
                (lang == "tr" || lang == "az" || lang == "lt");
        char[] upperCharArray;
        int upperChar;
        int srcChar;
        int srcCount;
        for (int i = firstLower; i < len; i += srcCount) {
            srcChar = (int)_this.charAt(i);
            if ((char)srcChar >= Character.MIN_HIGH_SURROGATE &&
                (char)srcChar <= Character.MAX_HIGH_SURROGATE) {
                srcChar = _this.codePointAt(i);
                srcCount = Character.charCount(srcChar);
            } else {
                srcCount = 1;
            }
            if (localeDependent) {
                upperChar = ConditionalSpecialCasing.toUpperCaseEx(_this, i, locale);
            } else {
                upperChar = Character.toUpperCaseEx(srcChar);
            }
            if ((upperChar == Character.ERROR)
                    || (upperChar >= Character.MIN_SUPPLEMENTARY_CODE_POINT)) {
                if (upperChar == Character.ERROR) {
                    if (localeDependent) {
                        upperCharArray =
                                ConditionalSpecialCasing.toUpperCaseCharArray(_this, i, locale);
                    } else {
                        upperCharArray = Character.toUpperCaseCharArray(srcChar);
                    }
                } else if (srcCount == 2) {
                    resultOffset += Character.toChars(upperChar, result, i + resultOffset) - srcCount;
                    continue;
                } else {
                    upperCharArray = Character.toChars(upperChar);
                }

                /* Grow result if needed */
                int mapLen = upperCharArray.length;
                if (mapLen > srcCount) {
                    char[] result2 = new char[result.length + mapLen - srcCount];
                    System.arraycopy(result, 0, result2, 0, i + resultOffset);
                    result = result2;
                }
                for (int x = 0; x < mapLen; ++x) {
                    result[i + resultOffset + x] = upperCharArray[x];
                }
                resultOffset += (mapLen - srcCount);
            } else {
                result[i + resultOffset] = (char)upperChar;
            }
        }
        return new String(result, 0, len + resultOffset);
    }

    static String toUpperCase(String _this) {
        return toUpperCase(_this, Locale.getDefault());
    }

    static String trim(String _this) {
        int len = _this.length();
        int st = 0;
        while ((st < len) && (_this.charAt(st) <= ' ')) {
            st++;
        }
        while ((st < len) && (_this.charAt(len - 1) <= ' ')) {
            len--;
        }
        return ((st > 0) || (len < _this.length())) ? _this.substring(st, len) : _this;
    }

    // StringUTF16.indexOfNonWhitespace
    private static int indexOfNonWhitespace(String _this) {
        int length = _this.length();
        int left = 0;
        while (left < length) {
            int codepoint = _this.codePointAt(left);
            if (codepoint != ' ' && codepoint != '\t' && !Character.isWhitespace(codepoint)) {
                break;
            }
            left += Character.charCount(codepoint);
        }
        return left;
    }

    private static int lastIndexOfNonWhitespace(String _this) {
        int right = _this.length();
        while (0 < right) {
            int codepoint = _this.codePointBefore(right);
            if (codepoint != ' ' && codepoint != '\t' && !Character.isWhitespace(codepoint)) {
                break;
            }
            right -= Character.charCount(codepoint);
        }
        return right;
    }

    static String strip(String _this) {
        int length = _this.length();
        int left = indexOfNonWhitespace(_this);
        if (left == length) {
            return "";
        }
        int right = lastIndexOfNonWhitespace(_this);
        boolean ifChanged = (left > 0) || (right < length);
        return ifChanged ? _this.substring(left, right) : _this;
    }

    static String stripLeading(String _this) {
        int left = indexOfNonWhitespace(_this);
        return (left != 0) ? _this.substring(left) : _this;
    }

    static String stripTrailing(String _this) {
        int right = lastIndexOfNonWhitespace(_this);
        return (right != _this.length()) ? _this.substring(0, right) : _this;
    }

    static boolean isBlank(String _this) {
        return indexOfNonWhitespace(_this) == _this.length();
    }

    static Stream<String> lines(String _this) {
        return StreamSupport.stream(new LinesSpliterator(_this, 0, _this.length()), false);
    }

    // StringUTF16.LinesSpliterator
    private static final class LinesSpliterator implements Spliterator<String> {
        private final String value;
        private int index;        // current index, modified on advance/split
        private final int fence;  // one past last index

        LinesSpliterator(String value, int start, int length) {
            this.value = value;
            this.index = start;
            this.fence = start + length;
        }

        private int indexOfLineSeparator(int start) {
            for (int current = start; current < fence; current++) {
                char ch = value.charAt(current);
                if (ch == '\n' || ch == '\r') {
                    return current;
                }
            }
            return fence;
        }

        private int skipLineSeparator(int start) {
            if (start < fence) {
                if (value.charAt(start) == '\r') {
                    int next = start + 1;
                    if (next < fence && value.charAt(next) == '\n') {
                        return next + 1;
                    }
                }
                return start + 1;
            }
            return fence;
        }

        private String next() {
            int start = index;
            int end = indexOfLineSeparator(start);
            index = skipLineSeparator(end);
            return value.substring(start, end);
        }

        @Override
        public boolean tryAdvance(Consumer<? super String> action) {
            if (action == null) {
                throw new NullPointerException("tryAdvance action missing");
            }
            if (index != fence) {
                action.accept(next());
                return true;
            }
            return false;
        }

        @Override
        public void forEachRemaining(Consumer<? super String> action) {
            if (action == null) {
                throw new NullPointerException("forEachRemaining action missing");
            }
            while (index != fence) {
                action.accept(next());
            }
        }

        @Override
        public Spliterator<String> trySplit() {
            int half = (fence + index) >>> 1;
            int mid = skipLineSeparator(indexOfLineSeparator(half));
            if (mid < fence) {
                int start = index;
                index = mid;
                return new LinesSpliterator(value, start, mid - start);
            }
            return null;
        }

        @Override
        public long estimateSize() {
            return fence - index + 1;
        }

        @Override
        public int characteristics() {
            return Spliterator.ORDERED | Spliterator.IMMUTABLE | Spliterator.NONNULL;
        }
    }

    static String indent(String _this, int n) {
        if (_this.isEmpty()) {
            return "";
        }
        Stream<String> stream = lines(_this);
        if (n > 0) {
            final String spaces = " ".repeat(n);
            stream = stream.map(s -> spaces + s);
        } else if (n == Integer.MIN_VALUE) {
            stream = stream.map(s -> s.stripLeading());
        } else if (n < 0) {
            stream = stream.map(s -> s.substring(Math.min(-n, indexOfNonWhitespace(s))));
        }
        return stream.collect(Collectors.joining("\n", "", "\n"));
    }

    static String stripIndent(String _this) {
        int length = _this.length();
        if (length == 0) {
            return "";
        }
        char lastChar = _this.charAt(length - 1);
        boolean optOut = lastChar == '\n' || lastChar == '\r';
        List<String> lines = lines(_this).toList();
        final int outdent = optOut ? 0 : outdent(lines);
        return lines.stream()
            .map(line -> {
                int firstNonWhitespace = indexOfNonWhitespace(line);
                int lastNonWhitespace = lastIndexOfNonWhitespace(line);
                int incidentalWhitespace = Math.min(outdent, firstNonWhitespace);
                return firstNonWhitespace > lastNonWhitespace
                    ? "" : line.substring(incidentalWhitespace, lastNonWhitespace);
            })
            .collect(Collectors.joining("\n", "", optOut ? "\n" : ""));
    }

    private static int outdent(List<String> lines) {
        // Note: outdent is guaranteed to be zero or positive number.
        // If there isn't a non-blank line then the last must be blank
        int outdent = Integer.MAX_VALUE;
        for (String line : lines) {
            int leadingWhitespace = indexOfNonWhitespace(line);
            if (leadingWhitespace != line.length()) {
                outdent = Integer.min(outdent, leadingWhitespace);
            }
        }
        String lastLine = lines.get(lines.size() - 1);
        if (lastLine.isBlank()) {
            outdent = Integer.min(outdent, lastLine.length());
        }
        return outdent;
    }

    static String translateEscapes(String _this) {
        if (_this.isEmpty()) {
            return "";
        }
        char[] chars = _this.toCharArray();
        int length = chars.length;
        int from = 0;
        int to = 0;
        while (from < length) {
            char ch = chars[from++];
            if (ch == '\\') {
                ch = from < length ? chars[from++] : '\0';
                switch (ch) {
                case 'b':
                    ch = '\b';
                    break;
                case 'f':
                    ch = '\f';
                    break;
                case 'n':
                    ch = '\n';
                    break;
                case 'r':
                    ch = '\r';
                    break;
                case 's':
                    ch = ' ';
                    break;
                case 't':
                    ch = '\t';
                    break;
                case '\'':
                case '\"':
                case '\\':
                    // as is
                    break;
                case '0': case '1': case '2': case '3':
                case '4': case '5': case '6': case '7':
                    int limit = Integer.min(from + (ch <= '3' ? 2 : 1), length);
                    int code = ch - '0';
                    while (from < limit) {
                        ch = chars[from];
                        if (ch < '0' || '7' < ch) {
                            break;
                        }
                        from++;
                        code = (code << 3) | (ch - '0');
                    }
                    ch = (char)code;
                    break;
                case '\n':
                    continue;
                case '\r':
                    if (from < length && chars[from] == '\n') {
                        from++;
                    }
                    continue;
                default: {
                    String msg = String.format(
                        "Invalid escape sequence: \\%c \\\\u%04X",
                        ch, (int)ch);
                    throw new IllegalArgumentException(msg);
                }
                }
            }

            chars[to++] = ch;
        }

        return new String(chars, 0, to);
    }

    static <R> R transform(String _this, Function<? super String, ? extends R> f) {
        return f.apply(_this);
    }

    static String formatted(String _this, Object... args) {
        return new Formatter().format(_this, args).toString();
    }

    static String repeat(String _this, int count) {
        if (count < 0) {
            throw new IllegalArgumentException("count is negative: " + count);
        }
        if (count == 1) {
            return _this;
        }
        final int len = _this.length();
        if (len == 0 || count == 0) {
            return "";
        }
        if (Integer.MAX_VALUE / count < len) {
            throw new OutOfMemoryError("Required length exceeds implementation limit");
        }
        final int limit = len * count;
        final char[] multiple = new char[limit];
        _this.getChars(0, len, multiple, 0);
        int copied = len;
        for (; copied < limit - copied; copied <<= 1) {
            System.arraycopy(multiple, 0, multiple, copied, copied);
        }
        System.arraycopy(multiple, 0, multiple, copied, limit - copied);
        return new String(multiple);
    }

    static Optional<String> describeConstable(String _this) {
        return Optional.of(_this);
    }

    static String resolveConstantDesc(String _this, MethodHandles.Lookup lookup) {
        Objects.requireNonNull(_this);
        return _this;
    }

    static IntStream chars(String _this) {
        return StreamSupport.intStream(
                new CharArraySpliterator(_this, Spliterator.IMMUTABLE),
                false);
    }

    static IntStream codePoints(String _this) {
        return StreamSupport.intStream(
                new CodePointsSpliterator(_this, Spliterator.IMMUTABLE),
                false);
    }

    private static final class CharArraySpliterator implements Spliterator.OfInt {
        private final String value;
        private int index;        // current index, modified on advance/split
        private final int fence;  // one past last index
        private final int cs;

        CharArraySpliterator(String value, int acs) {
            this(value, 0, value.length(), acs);
        }

        CharArraySpliterator(String value, int origin, int fence, int acs) {
            this.value = value;
            this.index = origin;
            this.fence = fence;
            this.cs = acs | Spliterator.ORDERED | Spliterator.SIZED
                      | Spliterator.SUBSIZED;
        }

        @Override
        public OfInt trySplit() {
            int lo = index, mid = (lo + fence) >>> 1;
            return (lo >= mid)
                   ? null
                   : new CharArraySpliterator(value, lo, index = mid, cs);
        }

        @Override
        public void forEachRemaining(IntConsumer action) {
            String a; int i, hi; // hoist accesses and checks from loop
            if (action == null)
                throw new NullPointerException();
            if ((i = index) >= 0 && i < (hi = fence)) {
                a = value;
                index = hi;
                do { action.accept(a.charAt(i)); } while (++i < hi);
            }
        }

        @Override
        public boolean tryAdvance(IntConsumer action) {
            if (action == null)
                throw new NullPointerException();
            int i = index;
            if (i >= 0 && i < fence) {
                action.accept(value.charAt(i));
                index++;
                return true;
            }
            return false;
        }

        @Override
        public long estimateSize() { return (long)(fence - index); }

        @Override
        public int characteristics() {
            return cs;
        }
    }

    private static final class CodePointsSpliterator implements Spliterator.OfInt {
        private final String value;
        private int index;        // current index, modified on advance/split
        private final int fence;  // one past last index
        private final int cs;

        CodePointsSpliterator(String value, int acs) {
            this(value, 0, value.length(), acs);
        }

        CodePointsSpliterator(String value, int origin, int fence, int acs) {
            this.value = value;
            this.index = origin;
            this.fence = fence;
            this.cs = acs | Spliterator.ORDERED;
        }

        @Override
        public OfInt trySplit() {
            int lo = index, mid = (lo + fence) >>> 1;
            if (lo >= mid)
                return null;

            int midOneLess;
            // If the mid-point intersects a surrogate pair
            if (Character.isLowSurrogate(value.charAt(mid)) &&
                Character.isHighSurrogate(value.charAt(midOneLess = (mid -1)))) {
                // If there is only one pair it cannot be split
                if (lo >= midOneLess)
                    return null;
                // Shift the mid-point to align with the surrogate pair
                return new CodePointsSpliterator(value, lo, index = midOneLess, cs);
            }
            return new CodePointsSpliterator(value, lo, index = mid, cs);
        }

        @Override
        public void forEachRemaining(IntConsumer action) {
            String a; int i, hi; // hoist accesses and checks from loop
            if (action == null)
                throw new NullPointerException();
            if ((i = index) >= 0 && i < (hi = fence)) {
                a = value;
                index = hi;
                do {
                    i = advance(a, i, hi, action);
                } while (i < hi);
            }
        }

        // Advance one code point from the index, i, and return the next
        // index to advance from
        private static int advance(String a, int i, int hi, IntConsumer action) {
            char c1 = a.charAt(i++);
            int cp = c1;
            if (Character.isHighSurrogate(c1) && i < hi) {
                char c2 = a.charAt(i);
                if (Character.isLowSurrogate(c2)) {
                    i++;
                    cp = Character.toCodePoint(c1, c2);
                }
            }
            action.accept(cp);
            return i;
        }

        @Override
        public boolean tryAdvance(IntConsumer action) {
            if (action == null)
                throw new NullPointerException();
            if (index < fence) {
                index = advance(value, index, fence, action);
                return true;
            }
            return false;
        }

        @Override
        public long estimateSize() {
            // Since this is a lazy check, the size is the upper bound
            return (long)(fence - index);
        }

        @Override
        public int characteristics() {
            return cs;
        }
    }

    //------------------------------ static methods ------------------------------

    public static String format(String format, Object... args) {
        return new Formatter().format(format, args).toString();
    }

    public static String format(Locale l, String format, Object... args) {
        return new Formatter(l).format(format, args).toString();
    }

    public static String valueOf(Object obj) {
        return (obj == null) ? "null" : obj.toString();
    }

    public static String valueOf(char[] data) {
        return new String(data);
    }

    public static String valueOf(char[] data, int offset, int count) {
        return new String(data, offset, count);
    }

    public static String valueOf(boolean b) {
        return b ? "true" : "false";
    }

    public static String valueOf(int i) {
        return Integer.toString(i);
    }

    public static String valueOf(long l) {
        return Long.toString(l);
    }

    public static String valueOf(float f) {
        return Float.toString(f);
    }

    public static String valueOf(double d) {
        return Double.toString(d);
    }

    // String.CASE_INSENSITIVE_ORDER's compare
    static int compareIgnoreCase(String s1, String s2) {
        return compareToCI(s1, 0, s1.length(), s2, 0, s2.length());
    }

}

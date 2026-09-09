using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;

using IKVM.ByteCode;
using IKVM.Runtime;

namespace IKVM.Java.Externs.sun.misc
{

    static class Unsafe
    {

        /// <summary>
        /// Holds a reference to the various delegates that facilitate unsafe operations for arrays. References are kept
        /// alive and associated with the element type of the array in a conditional weak table.
        /// </summary>
        class ArrayDelegateRef
        {

            readonly Lazy<Delegate> volatileGetter;
            readonly Lazy<Delegate> volatilePutter;
            readonly Lazy<Delegate> compareExchange;

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            /// <param name="type"></param>
            public ArrayDelegateRef(RuntimeJavaType type)
            {
                volatileGetter = new Lazy<Delegate>(() => CreateGetArrayVolatileDelegate(type), true);
                volatilePutter = new Lazy<Delegate>(() => CreatePutArrayVolatileDelegate(type), true);
                compareExchange = new Lazy<Delegate>(() => CreateCompareExchangeArrayDelegate(type), true);
            }

            /// <summary>
            /// Gets a delegate capable of implementing the volatile get logic. This value is a <see cref="Func{object, long, object}" />
            /// </summary>
            public Delegate VolatileGetter => volatileGetter.Value;

            /// <summary>
            /// Gets a delegate capable of implemetning the volatile put logic. This value is an <see cref="Action{object, long, object}" />
            /// </summary>
            public Delegate VolatilePutter => volatilePutter.Value;

            /// <summary>
            /// Gets a delegate capable of implemetning the compare and exchange logic. This value is an <see cref="Func{object[], long, object, object, object}" />
            /// </summary>
            public Delegate CompareExchange => compareExchange.Value;

        }

        /// <summary>
        /// Cache of delegates for array operations.
        /// </summary>
        static readonly ConditionalWeakTable<RuntimeJavaType, ArrayDelegateRef> arrayRefCache = new ConditionalWeakTable<RuntimeJavaType, ArrayDelegateRef>();

        /// <summary>
        /// Generic CompareExchange method.
        /// </summary>
        static readonly MethodInfo compareAndSwapArrayMethodInfo = typeof(Unsafe).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(i => i.Name == nameof(CompareAndSwapArray) && i.IsGenericMethodDefinition && i.GetGenericArguments().Length == 1)
            .FirstOrDefault();

        const int ObjectFieldOffsetTag = 0x40000000;
        const int ObjectFieldOffsetTagMask = 0x40000000;
        const int ObjectFieldOffsetTypeIdShift = 16;
        const int ObjectFieldOffsetTypeIdMask = 0x3FFF;
        const int ObjectFieldOffsetValueMask = 0xFFFF;
        const int ObjectFieldOffsetMaxValue = ObjectFieldOffsetValueMask;
        const int ObjectFieldOffsetMaxTypeId = ObjectFieldOffsetTypeIdMask;

        sealed class ObjectFieldOffsetInfo
        {

            readonly Dictionary<RuntimeJavaField, int> fieldOffsets;
            readonly Dictionary<int, RuntimeJavaField> offsetFields;
            readonly int[] fieldStartOffsets;
            readonly int[] fieldSizes;

            /// <summary>
            /// Initializes a new instance.
            /// </summary>
            /// <param name="typeId"></param>
            /// <param name="fields"></param>
            public ObjectFieldOffsetInfo(int typeId, RuntimeJavaField[] fields)
            {
                TypeId = typeId;
                Fields = fields;

                fieldOffsets = new Dictionary<RuntimeJavaField, int>(fields.Length);
                offsetFields = new Dictionary<int, RuntimeJavaField>(fields.Length);

                var offset = 0;
                fieldStartOffsets = new int[fields.Length];
                fieldSizes = new int[fields.Length];
                for (var i = 0; i < fields.Length; i++)
                {
                    var field = fields[i];
                    var size = GetObjectFieldStorageSize(field);
                    if ((uint)offset > ObjectFieldOffsetMaxValue)
                        throw new InvalidOperationException("Object field offset space exhausted.");

                    fieldOffsets[field] = offset;
                    offsetFields[offset] = field;
                    fieldStartOffsets[i] = offset;
                    fieldSizes[i] = size;
                    offset = checked(offset + size);
                }
            }

            /// <summary>
            /// Gets the identifier for the declaring type.
            /// </summary>
            public int TypeId { get; }

            /// <summary>
            /// Gets the instance fields in offset order.
            /// </summary>
            public RuntimeJavaField[] Fields { get; }

            /// <summary>
            /// Tries to get the encoded byte offset for the specified field.
            /// </summary>
            /// <param name="field"></param>
            /// <param name="offset"></param>
            /// <returns></returns>
            public bool TryGetOffset(RuntimeJavaField field, out int offset)
            {
                return fieldOffsets.TryGetValue(field, out offset);
            }

            /// <summary>
            /// Tries to get the field for the specified encoded byte offset.
            /// </summary>
            /// <param name="offset"></param>
            /// <param name="field"></param>
            /// <returns></returns>
            public bool TryGetField(int offset, out RuntimeJavaField field)
            {
                return offsetFields.TryGetValue(offset, out field);
            }

            public bool TryGetFieldSegment(int offset, out RuntimeJavaField field, out int fieldOffset, out int fieldSize)
            {
                var index = FindFieldSegmentIndex(offset);
                if (index >= 0)
                {
                    field = Fields[index];
                    fieldOffset = fieldStartOffsets[index];
                    fieldSize = fieldSizes[index];
                    return true;
                }

                field = null;
                fieldOffset = 0;
                fieldSize = 0;
                return false;
            }

            int FindFieldSegmentIndex(int offset)
            {
                var lo = 0;
                var hi = fieldStartOffsets.Length - 1;

                while (lo <= hi)
                {
                    var mid = lo + ((hi - lo) >> 1);
                    var start = fieldStartOffsets[mid];
                    var end = start + fieldSizes[mid];

                    if (offset < start)
                    {
                        hi = mid - 1;
                    }
                    else if (offset >= end)
                    {
                        lo = mid + 1;
                    }
                    else
                    {
                        return mid;
                    }
                }

                return -1;
            }

        }

        static int nextObjectFieldTypeId;
        static readonly object objectFieldOffsetSync = new object();
        static readonly ConditionalWeakTable<RuntimeJavaType, ObjectFieldOffsetInfo> objectFieldOffsetsByType = new ConditionalWeakTable<RuntimeJavaType, ObjectFieldOffsetInfo>();
        // typeId -> info, indexed directly. typeIds are dense and monotonic (Interlocked.Increment) and
        // capped at ObjectFieldOffsetMaxTypeId (0x3FFF), so one fixed array (<=16K refs, ~128KB) covers
        // every possible id. A plain array load replaces the per-field-access ConcurrentDictionary<int,_>
        // probe, which the trace showed was ~10% of CPU under the wasm interpreter (the probe machinery
        // dominated, not the stored value). Written once per type under objectFieldOffsetSync (whose
        // release, plus the safe publication of the encoded offset through Java fields, makes the slot
        // visible to lock-free readers); read as a bare bounds-checked index on the hot path.
        static readonly ObjectFieldOffsetInfo[] objectFieldOffsetsByTypeId = new ObjectFieldOffsetInfo[ObjectFieldOffsetMaxTypeId + 1];

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static bool IsEncodedObjectFieldOffset(long offset)
        {
            if (offset < 0 || offset > int.MaxValue)
                return false;

            return (((int)offset) & ObjectFieldOffsetTagMask) == ObjectFieldOffsetTag;
        }

        static ObjectFieldOffsetInfo GetObjectFieldOffsetInfo(RuntimeJavaType type)
        {
            if (objectFieldOffsetsByType.TryGetValue(type, out var info))
                return info;

            lock (objectFieldOffsetSync)
            {
                if (objectFieldOffsetsByType.TryGetValue(type, out info))
                    return info;

                var typeId = checked(Interlocked.Increment(ref nextObjectFieldTypeId));
                if (typeId > ObjectFieldOffsetMaxTypeId)
                    throw new InvalidOperationException("Object field offset type id space exhausted.");

                var fields = type.GetFields().Where(i => i.IsStatic == false).ToArray();
                info = new ObjectFieldOffsetInfo(typeId, fields);

                objectFieldOffsetsByType.Add(type, info);
                objectFieldOffsetsByTypeId[typeId] = info;

                return info;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long EncodeObjectFieldOffset(int typeId, int offset)
        {
            return ObjectFieldOffsetTag | (typeId << ObjectFieldOffsetTypeIdShift) | offset;
        }

        static int GetObjectFieldStorageSize(RuntimeJavaField field)
        {
            field.Link();

            var ft = field.FieldTypeWrapper;
            var p = field.DeclaringType.Context.PrimitiveJavaTypeFactory;

            if (ft == p.BYTE || ft == p.BOOLEAN)
                return 1;
            if (ft == p.CHAR || ft == p.SHORT)
                return 2;
            if (ft == p.INT || ft == p.FLOAT)
                return 4;
            if (ft == p.LONG || ft == p.DOUBLE)
                return 8;
            // unloadable field types are reference fields (erased to object)
            if (ft.IsPrimitive == false && ft.IsUnloadable == false && ft.IsNonPrimitiveValueType)
                return Marshal.SizeOf(ft.TypeAsTBD);

            return IntPtr.Size;
        }

        static RuntimeJavaField GetFieldByOffset(object target, long offset)
        {
            if (IsEncodedObjectFieldOffset(offset))
            {
                if (target == null)
                    return RuntimeJavaField.FromCookie((IntPtr)offset);

                var value = (int)offset;
                var typeId = (value >> ObjectFieldOffsetTypeIdShift) & ObjectFieldOffsetTypeIdMask;
                var fieldOffset = value & ObjectFieldOffsetValueMask;

                var info = objectFieldOffsetsByTypeId[typeId];
                if (info != null && info.TryGetField(fieldOffset, out var field))
                    return field;

                throw new global::java.lang.IllegalArgumentException();
            }

            return RuntimeJavaField.FromCookie((IntPtr)offset);
        }

#if !FIRST_PASS
        static bool TryGetEncodedObjectFieldOffsetInfo(object target, long offset, out ObjectFieldOffsetInfo info, out int fieldOffset)
        {
            info = null;
            fieldOffset = 0;

            if (target == null || IsEncodedObjectFieldOffset(offset) == false)
                return false;

            var value = (int)offset;
            var typeId = (value >> ObjectFieldOffsetTypeIdShift) & ObjectFieldOffsetTypeIdMask;
            fieldOffset = value & ObjectFieldOffsetValueMask;

            info = objectFieldOffsetsByTypeId[typeId];
            if (info != null)
                return true;

            throw new global::java.lang.IllegalArgumentException();
        }

        static bool IsPrimitiveField(RuntimeJavaField field)
        {
            field.Link();
            return field.FieldTypeWrapper.IsPrimitive;
        }

        static void GetPrimitiveFieldBytes(RuntimeJavaField field, object target, Span<byte> bytes)
        {
            field.Link();
            var ft = field.FieldTypeWrapper;
            var p = field.DeclaringType.Context.PrimitiveJavaTypeFactory;

            if (ft == p.BOOLEAN)
                bytes[0] = field.UnsafeGetValue<bool>(target) ? (byte)1 : (byte)0;
            else if (ft == p.BYTE)
                bytes[0] = field.UnsafeGetValue<byte>(target);
            else if (ft == p.CHAR)
            {
                var v = field.UnsafeGetValue<char>(target);
                if (BitConverter.IsLittleEndian)
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes, v);
                else
                    BinaryPrimitives.WriteUInt16BigEndian(bytes, v);
            }
            else if (ft == p.SHORT)
            {
                var v = field.UnsafeGetValue<short>(target);
                if (BitConverter.IsLittleEndian)
                    BinaryPrimitives.WriteInt16LittleEndian(bytes, v);
                else
                    BinaryPrimitives.WriteInt16BigEndian(bytes, v);
            }
            else if (ft == p.INT)
            {
                var v = field.UnsafeGetValue<int>(target);
                if (BitConverter.IsLittleEndian)
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, v);
                else
                    BinaryPrimitives.WriteInt32BigEndian(bytes, v);
            }
            else if (ft == p.FLOAT)
            {
                var v = global::java.lang.Float.floatToRawIntBits(field.UnsafeGetValue<float>(target));
                if (BitConverter.IsLittleEndian)
                    BinaryPrimitives.WriteInt32LittleEndian(bytes, v);
                else
                    BinaryPrimitives.WriteInt32BigEndian(bytes, v);
            }
            else if (ft == p.LONG)
            {
                var v = field.UnsafeGetValue<long>(target);
                if (BitConverter.IsLittleEndian)
                    BinaryPrimitives.WriteInt64LittleEndian(bytes, v);
                else
                    BinaryPrimitives.WriteInt64BigEndian(bytes, v);
            }
            else if (ft == p.DOUBLE)
            {
                var v = global::java.lang.Double.doubleToRawLongBits(field.UnsafeGetValue<double>(target));
                if (BitConverter.IsLittleEndian)
                    BinaryPrimitives.WriteInt64LittleEndian(bytes, v);
                else
                    BinaryPrimitives.WriteInt64BigEndian(bytes, v);
            }
            else
            {
                throw new global::java.lang.IllegalArgumentException();
            }
        }

        static void SetPrimitiveFieldBytes(RuntimeJavaField field, object target, ReadOnlySpan<byte> bytes)
        {
            field.Link();
            var ft = field.FieldTypeWrapper;
            var p = field.DeclaringType.Context.PrimitiveJavaTypeFactory;

            if (ft == p.BOOLEAN)
            {
                field.UnsafeSetValue(target, bytes[0] != 0);
            }
            else if (ft == p.BYTE)
            {
                field.UnsafeSetValue(target, bytes[0]);
            }
            else if (ft == p.CHAR)
            {
                var v = BitConverter.IsLittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(bytes) : BinaryPrimitives.ReadUInt16BigEndian(bytes);
                field.UnsafeSetValue(target, (char)v);
            }
            else if (ft == p.SHORT)
            {
                var v = BitConverter.IsLittleEndian ? BinaryPrimitives.ReadInt16LittleEndian(bytes) : BinaryPrimitives.ReadInt16BigEndian(bytes);
                field.UnsafeSetValue(target, v);
            }
            else if (ft == p.INT)
            {
                var v = BitConverter.IsLittleEndian ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : BinaryPrimitives.ReadInt32BigEndian(bytes);
                field.UnsafeSetValue(target, v);
            }
            else if (ft == p.FLOAT)
            {
                var v = BitConverter.IsLittleEndian ? BinaryPrimitives.ReadInt32LittleEndian(bytes) : BinaryPrimitives.ReadInt32BigEndian(bytes);
                field.UnsafeSetValue(target, global::java.lang.Float.intBitsToFloat(v));
            }
            else if (ft == p.LONG)
            {
                var v = BitConverter.IsLittleEndian ? BinaryPrimitives.ReadInt64LittleEndian(bytes) : BinaryPrimitives.ReadInt64BigEndian(bytes);
                field.UnsafeSetValue(target, v);
            }
            else if (ft == p.DOUBLE)
            {
                var v = BitConverter.IsLittleEndian ? BinaryPrimitives.ReadInt64LittleEndian(bytes) : BinaryPrimitives.ReadInt64BigEndian(bytes);
                field.UnsafeSetValue(target, global::java.lang.Double.longBitsToDouble(v));
            }
            else
            {
                throw new global::java.lang.IllegalArgumentException();
            }
        }

        static bool TryReadEncodedObjectPrimitive(object target, long offset, Span<byte> destination)
        {
            if (TryGetEncodedObjectFieldOffsetInfo(target, offset, out var info, out var readOffset) == false)
                return false;

            var remaining = destination.Length;
            var destinationOffset = 0;
            Span<byte> fieldBytes = stackalloc byte[8];

            while (remaining > 0)
            {
                if (info.TryGetFieldSegment(readOffset, out var field, out var fieldStartOffset, out var fieldSize) == false || IsPrimitiveField(field) == false)
                    throw new global::java.lang.IllegalArgumentException();

                var withinFieldOffset = readOffset - fieldStartOffset;
                var bytesToCopy = Math.Min(fieldSize - withinFieldOffset, remaining);

                GetPrimitiveFieldBytes(field, target, fieldBytes.Slice(0, fieldSize));
                fieldBytes.Slice(withinFieldOffset, bytesToCopy).CopyTo(destination.Slice(destinationOffset, bytesToCopy));

                readOffset += bytesToCopy;
                destinationOffset += bytesToCopy;
                remaining -= bytesToCopy;
            }

            return true;
        }

        static bool TryWriteEncodedObjectPrimitive(object target, long offset, ReadOnlySpan<byte> source)
        {
            if (TryGetEncodedObjectFieldOffsetInfo(target, offset, out var info, out var writeOffset) == false)
                return false;

            var remaining = source.Length;
            var sourceOffset = 0;
            Span<byte> fieldBytes = stackalloc byte[8];

            while (remaining > 0)
            {
                if (info.TryGetFieldSegment(writeOffset, out var field, out var fieldStartOffset, out var fieldSize) == false || IsPrimitiveField(field) == false)
                    throw new global::java.lang.IllegalArgumentException();

                var withinFieldOffset = writeOffset - fieldStartOffset;
                var bytesToCopy = Math.Min(fieldSize - withinFieldOffset, remaining);

                GetPrimitiveFieldBytes(field, target, fieldBytes.Slice(0, fieldSize));
                source.Slice(sourceOffset, bytesToCopy).CopyTo(fieldBytes.Slice(withinFieldOffset, bytesToCopy));
                SetPrimitiveFieldBytes(field, target, fieldBytes.Slice(0, fieldSize));

                writeOffset += bytesToCopy;
                sourceOffset += bytesToCopy;
                remaining -= bytesToCopy;
            }

            return true;
        }
#endif

        internal static long GetObjectFieldOffset(RuntimeJavaField field)
        {
            var info = GetObjectFieldOffsetInfo(field.DeclaringType);
            if (info.TryGetOffset(field, out var offset))
                return EncodeObjectFieldOffset(info.TypeId, offset);

            throw new global::java.lang.IllegalArgumentException();
        }

        /// <summary>
        /// Emits the appropriate ldind opcode for the given type.
        /// </summary>
        /// <param name="il"></param>
        /// <param name="t"></param>
        /// <exception cref="InvalidOperationException"></exception>
        static void EmitLdind(ILGenerator il, Type t)
        {
            if (t == typeof(bool))
                il.Emit(OpCodes.Ldind_U1);
            else if (t == typeof(byte))
                il.Emit(OpCodes.Ldind_U1);
            else if (t == typeof(char))
                il.Emit(OpCodes.Ldind_U2);
            else if (t == typeof(short))
                il.Emit(OpCodes.Ldind_I2);
            else if (t == typeof(int))
                il.Emit(OpCodes.Ldind_I4);
            else if (t == typeof(long))
                il.Emit(OpCodes.Ldind_I8);
            else if (t == typeof(float))
                il.Emit(OpCodes.Ldind_R4);
            else if (t == typeof(double))
                il.Emit(OpCodes.Ldind_R8);
            else
                il.Emit(OpCodes.Ldind_Ref);
        }

        /// <summary>
        /// Emits the appropriate ldind opcode for the given type.
        /// </summary>
        /// <param name="il"></param>
        /// <param name="t"></param>
        /// <exception cref="InvalidOperationException"></exception>
        static void EmitStind(ILGenerator il, Type t)
        {
            if (t == typeof(bool))
                il.Emit(OpCodes.Stind_I1);
            else if (t == typeof(byte))
                il.Emit(OpCodes.Stind_I1);
            else if (t == typeof(char))
                il.Emit(OpCodes.Stind_I2);
            else if (t == typeof(short))
                il.Emit(OpCodes.Stind_I2);
            else if (t == typeof(int))
                il.Emit(OpCodes.Stind_I4);
            else if (t == typeof(long))
                il.Emit(OpCodes.Stind_I8);
            else if (t == typeof(float))
                il.Emit(OpCodes.Stind_R4);
            else if (t == typeof(double))
                il.Emit(OpCodes.Stind_R8);
            else
                il.Emit(OpCodes.Stind_Ref);
        }

        /// <summary>
        /// Implementation of native method 'registerNatives'.
        /// </summary>
        public static void registerNatives()
        {

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static long IntPtrToJLongAddress(IntPtr value)
        {
            if (IntPtr.Size == sizeof(long))
                return value.ToInt64();

            return unchecked((long)(uint)value.ToInt32());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static IntPtr JLongAddressToIntPtr(long value)
        {
            if (IntPtr.Size == sizeof(long))
                return (IntPtr)value;

            return unchecked((IntPtr)(int)(uint)value);
        }

        /// <summary>
        /// Implements the logic to get a field by offset.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        /// <exception cref="global::java.lang.InternalError"></exception>
        static T GetField<T>(object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var f = GetFieldByOffset(o, offset);
            if (o is RuntimeJavaType w)
            {
                if (w != f.DeclaringType)
                    throw new global::java.lang.IllegalArgumentException();

                return f.UnsafeGetValue<T>(null);
            }

            return f.UnsafeGetValue<T>(o);
#endif
        }

        /// <summary>
        /// Implements the logic to set a field by offset.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        /// <exception cref="global::java.lang.InternalError"></exception>
        static void PutField<T>(object o, long offset, T value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var f = GetFieldByOffset(o, offset);
            if (o is RuntimeJavaType w)
            {
                if (w != f.DeclaringType)
                    throw new global::java.lang.IllegalArgumentException();

                f.UnsafeSetValue<T>(null, value);
            }

            f.UnsafeSetValue<T>(o, value);
#endif
        }

        /// <summary>
        /// Implementation of native method 'getObject'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static object getObject(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    RuntimeJavaType w => GetField<object>(null, offset),
                    object[] array => array[offset / IntPtr.Size],
                    object obj => GetField<object>(obj, offset),
                    _ => throw new global::java.lang.IllegalArgumentException(),
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putObject'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static void putObject(object self, object o, long offset, object x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                switch (o)
                {
                    case RuntimeJavaType w:
                        PutField(w, offset, x);
                        break;
                    case object[] array:
                        array[offset / IntPtr.Size] = x;
                        break;
                    case object obj:
                        PutField(obj, offset, x);
                        break;
                    default:
                        throw new global::java.lang.IllegalArgumentException();
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getBoolean'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static bool getBoolean(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    null => getByte(self, offset) != 0,
                    RuntimeJavaType w => GetField<bool>(null, offset),
                    Array array => Buffer.GetByte(array, (int)offset) != 0,
                    _ => GetField<bool>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putBoolean'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static void putBoolean(object self, object o, long offset, bool x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                switch (o)
                {
                    case null:
                        putByte(self, offset, x ? (byte)1 : (byte)0);
                        break;
                    case RuntimeJavaType w:
                        PutField(w, offset, x);
                        break;
                    case Array array:
                        Buffer.SetByte(array, (int)offset, x ? (byte)1 : (byte)0);
                        break;
                    default:
                        PutField(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getByte'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static byte getByte(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    null => getByte(self, offset),
                    RuntimeJavaType w => GetField<byte>(null, offset),
                    Array array => Buffer.GetByte(array, (int)offset),
                    _ => GetField<byte>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putByte'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static void putByte(object self, object o, long offset, byte x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                switch (o)
                {
                    case null:
                        putByte(self, offset, x);
                        break;
                    case RuntimeJavaType w:
                        PutField(w, offset, x);
                        break;
                    case Array array:
                        Buffer.SetByte(array, (int)offset, x);
                        break;
                    default:
                        PutField(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getShort'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static short getShort(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o != null)
                {
                    Span<byte> raw = stackalloc byte[sizeof(short)];
                    if (TryReadEncodedObjectPrimitive(o, offset, raw))
                        return BitConverter.IsLittleEndian ? BinaryPrimitives.ReadInt16LittleEndian(raw) : BinaryPrimitives.ReadInt16BigEndian(raw);
                }

                return o switch
                {
                    null => getShort(self, offset),
                    RuntimeJavaType w => GetField<short>(null, offset),
                    Array array => ReadInt16(array, offset),
                    _ => GetField<short>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putShort'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static void putShort(object self, object o, long offset, short x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o != null)
                {
                    Span<byte> raw = stackalloc byte[sizeof(short)];
                    if (BitConverter.IsLittleEndian)
                        BinaryPrimitives.WriteInt16LittleEndian(raw, x);
                    else
                        BinaryPrimitives.WriteInt16BigEndian(raw, x);

                    if (TryWriteEncodedObjectPrimitive(o, offset, raw))
                        return;
                }

                switch (o)
                {
                    case null:
                        putShort(self, offset, x);
                        break;
                    case RuntimeJavaType w:
                        PutField(w, offset, x);
                        break;
                    case Array array:
                        WriteInt16(array, offset, x);
                        break;
                    default:
                        PutField(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getChar'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static char getChar(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o != null)
                {
                    Span<byte> raw = stackalloc byte[sizeof(char)];
                    if (TryReadEncodedObjectPrimitive(o, offset, raw))
                    {
                        var v = BitConverter.IsLittleEndian ? BinaryPrimitives.ReadUInt16LittleEndian(raw) : BinaryPrimitives.ReadUInt16BigEndian(raw);
                        return (char)v;
                    }
                }

                return o switch
                {
                    null => getChar(self, offset),
                    RuntimeJavaType w => GetField<char>(null, offset),
                    Array array => (char)ReadInt16(array, offset),
                    _ => GetField<char>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putChar'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static void putChar(object self, object o, long offset, char x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o != null)
                {
                    Span<byte> raw = stackalloc byte[sizeof(char)];
                    if (BitConverter.IsLittleEndian)
                        BinaryPrimitives.WriteUInt16LittleEndian(raw, x);
                    else
                        BinaryPrimitives.WriteUInt16BigEndian(raw, x);

                    if (TryWriteEncodedObjectPrimitive(o, offset, raw))
                        return;
                }

                switch (o)
                {
                    case null:
                        putChar(self, offset, x);
                        break;
                    case RuntimeJavaType w:
                        PutField(w, offset, x);
                        break;
                    case Array array:
                        WriteInt16(array, offset, (short)x);
                        break;
                    default:
                        PutField(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getInt'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static int getInt(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o != null)
                {
                    Span<byte> raw = stackalloc byte[sizeof(int)];
                    if (TryReadEncodedObjectPrimitive(o, offset, raw))
                        return BitConverter.IsLittleEndian ? BinaryPrimitives.ReadInt32LittleEndian(raw) : BinaryPrimitives.ReadInt32BigEndian(raw);
                }

                return o switch
                {
                    null => getInt(self, offset),
                    RuntimeJavaType w => GetField<int>(null, offset),
                    Array array => ReadInt32(array, offset),
                    _ => GetField<int>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putInt'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static void putInt(object self, object o, long offset, int x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o != null)
                {
                    Span<byte> raw = stackalloc byte[sizeof(int)];
                    if (BitConverter.IsLittleEndian)
                        BinaryPrimitives.WriteInt32LittleEndian(raw, x);
                    else
                        BinaryPrimitives.WriteInt32BigEndian(raw, x);

                    if (TryWriteEncodedObjectPrimitive(o, offset, raw))
                        return;
                }

                switch (o)
                {
                    case null:
                        putInt(self, offset, x);
                        break;
                    case RuntimeJavaType w:
                        PutField(w, offset, x);
                        break;
                    case Array array:
                        WriteInt32(array, offset, x);
                        break;
                    default:
                        PutField(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getLong'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static long getLong(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o != null)
                {
                    Span<byte> raw = stackalloc byte[sizeof(long)];
                    if (TryReadEncodedObjectPrimitive(o, offset, raw))
                        return BitConverter.IsLittleEndian ? BinaryPrimitives.ReadInt64LittleEndian(raw) : BinaryPrimitives.ReadInt64BigEndian(raw);
                }

                return o switch
                {
                    null => getLong(self, offset),
                    RuntimeJavaType w => GetField<long>(null, offset),
                    Array array => ReadInt64(array, offset),
                    _ => GetField<long>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putLong'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static void putLong(object self, object o, long offset, long x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o != null)
                {
                    Span<byte> raw = stackalloc byte[sizeof(long)];
                    if (BitConverter.IsLittleEndian)
                        BinaryPrimitives.WriteInt64LittleEndian(raw, x);
                    else
                        BinaryPrimitives.WriteInt64BigEndian(raw, x);

                    if (TryWriteEncodedObjectPrimitive(o, offset, raw))
                        return;
                }

                switch (o)
                {
                    case null:
                        putLong(self, offset, x);
                        break;
                    case RuntimeJavaType w:
                        PutField(w, offset, x);
                        break;
                    case Array array:
                        WriteInt64(array, offset, x);
                        break;
                    default:
                        PutField(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getFloat'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static float getFloat(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o != null)
                {
                    Span<byte> raw = stackalloc byte[sizeof(int)];
                    if (TryReadEncodedObjectPrimitive(o, offset, raw))
                    {
                        var bits = BitConverter.IsLittleEndian ? BinaryPrimitives.ReadInt32LittleEndian(raw) : BinaryPrimitives.ReadInt32BigEndian(raw);
                        return global::java.lang.Float.intBitsToFloat(bits);
                    }
                }

                return o switch
                {
                    null => getFloat(self, offset),
                    RuntimeJavaType w => GetField<float>(null, offset),
                    Array array => global::java.lang.Float.intBitsToFloat(ReadInt32(array, offset)),
                    _ => GetField<float>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putFloat'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static void putFloat(object self, object o, long offset, float x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o != null)
                {
                    Span<byte> raw = stackalloc byte[sizeof(int)];
                    var bits = global::java.lang.Float.floatToRawIntBits(x);
                    if (BitConverter.IsLittleEndian)
                        BinaryPrimitives.WriteInt32LittleEndian(raw, bits);
                    else
                        BinaryPrimitives.WriteInt32BigEndian(raw, bits);

                    if (TryWriteEncodedObjectPrimitive(o, offset, raw))
                        return;
                }

                switch (o)
                {
                    case null:
                        putFloat(self, offset, x);
                        break;
                    case RuntimeJavaType w:
                        PutField(w, offset, x);
                        break;
                    case Array array:
                        WriteInt32(array, offset, global::java.lang.Float.floatToRawIntBits(x));
                        break;
                    default:
                        PutField(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getDouble'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static double getDouble(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o != null)
                {
                    Span<byte> raw = stackalloc byte[sizeof(long)];
                    if (TryReadEncodedObjectPrimitive(o, offset, raw))
                    {
                        var bits = BitConverter.IsLittleEndian ? BinaryPrimitives.ReadInt64LittleEndian(raw) : BinaryPrimitives.ReadInt64BigEndian(raw);
                        return global::java.lang.Double.longBitsToDouble(bits);
                    }
                }

                return o switch
                {
                    null => getDouble(self, offset),
                    RuntimeJavaType w => GetField<double>(null, offset),
                    Array array => global::java.lang.Double.longBitsToDouble(ReadInt64(array, offset)),
                    _ => GetField<double>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putDouble'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static void putDouble(object self, object o, long offset, double x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o != null)
                {
                    Span<byte> raw = stackalloc byte[sizeof(long)];
                    var bits = global::java.lang.Double.doubleToRawLongBits(x);
                    if (BitConverter.IsLittleEndian)
                        BinaryPrimitives.WriteInt64LittleEndian(raw, bits);
                    else
                        BinaryPrimitives.WriteInt64BigEndian(raw, bits);

                    if (TryWriteEncodedObjectPrimitive(o, offset, raw))
                        return;
                }

                switch (o)
                {
                    case null:
                        putDouble(self, offset, x);
                        break;
                    case RuntimeJavaType w:
                        PutField(w, offset, x);
                        break;
                    case Array array:
                        WriteInt64(array, offset, global::java.lang.Double.doubleToRawLongBits(x));
                        break;
                    default:
                        PutField(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getByte'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <returns></returns>
        public static byte getByte(object self, long address)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return Marshal.ReadByte(JLongAddressToIntPtr(address));
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putByte'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <param name="x"></param>
        public static void putByte(object self, long address, byte x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                Marshal.WriteByte(JLongAddressToIntPtr(address), x);
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getShort'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <returns></returns>
        public static short getShort(object self, long address)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return Marshal.ReadInt16(JLongAddressToIntPtr(address));
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putShort'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <param name="x"></param>
        public static void putShort(object self, long address, short x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                Marshal.WriteInt16(JLongAddressToIntPtr(address), x);
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getChar'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <returns></returns>
        public static char getChar(object self, long address)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return (char)Marshal.ReadInt16(JLongAddressToIntPtr(address));
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putChar'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <param name="x"></param>
        public static void putChar(object self, long address, char x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                Marshal.WriteInt16(JLongAddressToIntPtr(address), (short)x);
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getInt'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <returns></returns>
        public static int getInt(object self, long address)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return Marshal.ReadInt32(JLongAddressToIntPtr(address));
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putInt'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <param name="x"></param>
        public static void putInt(object self, long address, int x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                Marshal.WriteInt32(JLongAddressToIntPtr(address), x);
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getLong'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <returns></returns>
        public static long getLong(object self, long address)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return Marshal.ReadInt64(JLongAddressToIntPtr(address));
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putLong'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <param name="x"></param>
        public static void putLong(object self, long address, long x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                Marshal.WriteInt64(JLongAddressToIntPtr(address), x);
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getFloat'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <returns></returns>
        public static float getFloat(object self, long address)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return global::java.lang.Float.intBitsToFloat(getInt(self, address));
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putFloat'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <param name="x"></param>
        public static void putFloat(object self, long address, float x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            putInt(self, address, global::java.lang.Float.floatToIntBits(x));
#endif
        }

        /// <summary>
        /// Implementation of native method 'getDouble'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <returns></returns>
        public static double getDouble(object self, long address)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return global::java.lang.Double.longBitsToDouble(getLong(self, address));
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putDouble'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <param name="x"></param>
        public static void putDouble(object self, long address, double x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            putLong(self, address, global::java.lang.Double.doubleToLongBits(x));
#endif
        }

        /// <summary>
        /// Implementation of native method 'getAddress'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <returns></returns>
        public static long getAddress(object self, long address)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return IntPtrToJLongAddress(Marshal.ReadIntPtr(JLongAddressToIntPtr(address)));
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putAddress'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <param name="x"></param>
        public static void putAddress(object self, long address, long x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                Marshal.WriteIntPtr(JLongAddressToIntPtr(address), JLongAddressToIntPtr(x));
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'allocateMemory'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="bytes"></param>
        /// <returns></returns>
        /// <exception cref="global::java.lang.OutOfMemoryError"></exception>
        public static unsafe long allocateMemory(object self, long bytes)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (bytes == 0)
                    return 0;

                var len = (IntPtr)bytes + sizeof(IntPtr);
                var ptr = Marshal.AllocHGlobal(len);
                Marshal.WriteIntPtr(ptr, len);
                System.GC.AddMemoryPressure((long)len);
                return IntPtrToJLongAddress(IntPtr.Add(ptr, IntPtr.Size));
            }
            catch (OutOfMemoryException e)
            {
                throw new global::java.lang.OutOfMemoryError(e.Message).initCause(e);
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'reallocateMemory'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        /// <param name="bytes"></param>
        /// <returns></returns>
        /// <exception cref="global::java.lang.OutOfMemoryError"></exception>
        public static unsafe long reallocateMemory(object self, long address, long bytes)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (bytes == 0)
                {
                    freeMemory(self, address);
                    return 0;
                }

                // remove memory pressure
                var ptr = IntPtr.Subtract(JLongAddressToIntPtr(address), IntPtr.Size);
                var len = Marshal.ReadIntPtr(ptr);
                System.GC.RemoveMemoryPressure((long)len);

                // reallocate and add memory pressure
                len = (IntPtr)bytes + sizeof(IntPtr);
                ptr = Marshal.ReAllocHGlobal(ptr, len);
                Marshal.WriteIntPtr(ptr, len);
                System.GC.AddMemoryPressure((long)len);
                return IntPtrToJLongAddress(IntPtr.Add(ptr, IntPtr.Size));
            }
            catch (OutOfMemoryException e)
            {
                throw new global::java.lang.OutOfMemoryError(e.Message).initCause(e);
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'setMemory'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="bytes"></param>
        /// <param name="value"></param>
        /// <exception cref="global::java.lang.IllegalArgumentException"></exception>
        public static unsafe void setMemory(object self, object o, long offset, long bytes, byte value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o == null)
                    new Span<byte>((void*)JLongAddressToIntPtr(offset), (int)bytes).Fill(value);
                else if (o is byte[] array)
                    ((Span<byte>)array).Slice((int)offset, (int)bytes).Fill(value);
                else if (o is Array array2)
                {
                    GCHandle h = new();

                    try
                    {
                        h = GCHandle.Alloc(array2, GCHandleType.Pinned);
                        new Span<byte>((byte*)h.AddrOfPinnedObject(), Buffer.ByteLength(array2)).Slice((int)offset, (int)bytes).Fill(value);
                    }
                    finally
                    {
                        if (h.IsAllocated)
                            h.Free();
                    }
                }
                else
                    throw new global::java.lang.IllegalArgumentException();
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'copyMemory'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="srcBase"></param>
        /// <param name="srcOffset"></param>
        /// <param name="destBase"></param>
        /// <param name="destOffset"></param>
        /// <param name="bytes"></param>
        /// <exception cref="global::java.lang.IllegalArgumentException"></exception>
        public static unsafe void copyMemory(object self, object srcBase, long srcOffset, object destBase, long destOffset, long bytes)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (srcBase == null)
                {
                    if (destBase is byte[] byteArray)
                    {
                        Marshal.Copy(JLongAddressToIntPtr(srcOffset), byteArray, (int)destOffset, (int)bytes);
                    }
                    else if (destBase is bool[])
                    {
                        var tmp = new byte[(int)bytes];
                        copyMemory(self, srcBase, srcOffset, tmp, 0, bytes);
                        copyMemory(self, tmp, 0, destBase, destOffset, bytes);
                    }
                    else if (destBase is short[] shortArray)
                    {
                        Marshal.Copy(JLongAddressToIntPtr(srcOffset), shortArray, (int)(destOffset >> 1), (int)(bytes >> 1));
                    }
                    else if (destBase is char[] charArray)
                    {
                        Marshal.Copy(JLongAddressToIntPtr(srcOffset), charArray, (int)(destOffset >> 1), (int)(bytes >> 1));
                    }
                    else if (destBase is int[] intArray)
                    {
                        Marshal.Copy(JLongAddressToIntPtr(srcOffset), intArray, (int)(destOffset >> 2), (int)(bytes >> 2));
                    }
                    else if (destBase is float[] floatArray)
                    {
                        Marshal.Copy(JLongAddressToIntPtr(srcOffset), floatArray, (int)(destOffset >> 2), (int)(bytes >> 2));
                    }
                    else if (destBase is long[] longArray)
                    {
                        Marshal.Copy(JLongAddressToIntPtr(srcOffset), longArray, (int)(destOffset >> 3), (int)(bytes >> 3));
                    }
                    else if (destBase is double[] doubleArray)
                    {
                        Marshal.Copy(JLongAddressToIntPtr(srcOffset), doubleArray, (int)(destOffset >> 3), (int)(bytes >> 3));
                    }
                    else if (destBase == null)
                    {
                        Buffer.MemoryCopy((void*)JLongAddressToIntPtr(srcOffset), (void*)JLongAddressToIntPtr(destOffset), long.MaxValue, bytes);
                    }
                    else
                    {
                        throw new global::java.lang.IllegalArgumentException();
                    }
                }
                else if (srcBase is Array && destBase is Array)
                {
                    Buffer.BlockCopy((Array)srcBase, (int)srcOffset, (Array)destBase, (int)destOffset, (int)bytes);
                }
                else
                {
                    if (srcBase is byte[] byteArray)
                    {
                        Marshal.Copy(byteArray, (int)srcOffset, JLongAddressToIntPtr(destOffset), (int)bytes);
                    }
                    else if (srcBase is bool[])
                    {
                        var tmp = new byte[(int)bytes];
                        copyMemory(self, srcBase, srcOffset, tmp, 0, bytes);
                        copyMemory(self, tmp, 0, destBase, destOffset, bytes);
                    }
                    else if (srcBase is short[] shortArray)
                    {
                        Marshal.Copy(shortArray, (int)(srcOffset >> 1), JLongAddressToIntPtr(destOffset), (int)(bytes >> 1));
                    }
                    else if (srcBase is char[] charArray)
                    {
                        Marshal.Copy(charArray, (int)(srcOffset >> 1), JLongAddressToIntPtr(destOffset), (int)(bytes >> 1));
                    }
                    else if (srcBase is int[] intArray)
                    {
                        Marshal.Copy(intArray, (int)(srcOffset >> 2), JLongAddressToIntPtr(destOffset), (int)(bytes >> 2));
                    }
                    else if (srcBase is float[] floatArray)
                    {
                        Marshal.Copy(floatArray, (int)(srcOffset >> 2), JLongAddressToIntPtr(destOffset), (int)(bytes >> 2));
                    }
                    else if (srcBase is long[] longArray)
                    {
                        Marshal.Copy(longArray, (int)(srcOffset >> 3), JLongAddressToIntPtr(destOffset), (int)(bytes >> 3));
                    }
                    else if (srcBase is double[] doubleArray)
                    {
                        Marshal.Copy(doubleArray, (int)(srcOffset >> 3), JLongAddressToIntPtr(destOffset), (int)(bytes >> 3));
                    }
                    else
                    {
                        throw new global::java.lang.IllegalArgumentException();
                    }
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'freeMemory'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="address"></param>
        public static unsafe void freeMemory(object self, long address)
        {
            var adr = JLongAddressToIntPtr(address);
            if (adr == IntPtr.Zero)
                return;

            var ptr = IntPtr.Subtract(adr, IntPtr.Size);
            var len = Marshal.ReadIntPtr(ptr);
            Marshal.FreeHGlobal(ptr);
            System.GC.RemoveMemoryPressure((long)len);
        }

        /// <summary>
        /// Implementation of native method 'staticFieldBase'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="f"></param>
        /// <returns></returns>
        public static object staticFieldBase(object self, global::java.lang.reflect.Field f)
        {
            var w = RuntimeJavaField.FromField(f);
            if (w.IsStatic == false)
                throw new global::java.lang.IllegalArgumentException();

            return w.DeclaringType;
        }

        /// <summary>
        /// Implementation of native method 'staticFieldOffset'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="f"></param>
        /// <returns></returns>
        public static long staticFieldOffset(object self, global::java.lang.reflect.Field f)
        {
            var w = RuntimeJavaField.FromField(f);
            if (w.IsStatic == false)
                throw new global::java.lang.IllegalArgumentException();

            return (long)w.Cookie;
        }

        /// <summary>
        /// Implementation of native method 'objectFieldOffset'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="f"></param>
        /// <returns></returns>
        public static long objectFieldOffset(object self, global::java.lang.reflect.Field f)
        {
            var w = RuntimeJavaField.FromField(f);
            if (w.IsStatic)
                throw new global::java.lang.IllegalArgumentException();

            return GetObjectFieldOffset(w);
        }

        /// <summary>
        /// Implementation of native method 'shouldBeInitialized'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="c"></param>
        /// <returns></returns>
        public static bool shouldBeInitialized(object self, global::java.lang.Class c)
        {
            return RuntimeJavaType.FromClass(c).HasStaticInitializer;
        }

        /// <summary>
        /// Implementation of native method 'ensureClassInitialized'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="c"></param>
        public static void ensureClassInitialized(object self, global::java.lang.Class c)
        {
            var tw = RuntimeJavaType.FromClass(c);
            if (tw.IsArray == false)
            {
                try
                {
                    tw.Finish();
                }
                catch (RetargetableJavaException x)
                {
                    throw x.ToJava();
                }

                tw.RunClassInit();
            }
        }

        /// <summary>
        /// Implementation of native method 'arrayBaseOffset'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="arrayClass"></param>
        /// <returns></returns>
        public static int arrayBaseOffset(object self, global::java.lang.Class arrayClass)
        {
            return 0;
        }

        /// <summary>
        /// Determines the index scale for the specified array type.
        /// </summary>
        /// <param name="tw"></param>
        /// <returns></returns>
        static int ArrayIndexScale(RuntimeJavaType tw)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var context = tw.Context;

            var et = tw.ElementTypeWrapper;
            if (et == context.PrimitiveJavaTypeFactory.BYTE || et == context.PrimitiveJavaTypeFactory.BOOLEAN)
                return 1;
            else if (et == context.PrimitiveJavaTypeFactory.CHAR || et == context.PrimitiveJavaTypeFactory.SHORT)
                return 2;
            else if (et == context.PrimitiveJavaTypeFactory.INT || et == context.PrimitiveJavaTypeFactory.FLOAT)
                return 4;
            else if (et == context.PrimitiveJavaTypeFactory.LONG || et == context.PrimitiveJavaTypeFactory.DOUBLE)
                return 8;
            // unloadable element types are reference elements (erased to object)
            else if (et.IsPrimitive == false && et.IsUnloadable == false && et.IsNonPrimitiveValueType)
                return Marshal.SizeOf(et.TypeAsTBD);
            else if (et.IsPrimitive == false)
                return IntPtr.Size;
            else
                return 1;
#endif
        }

        /// <summary>
        /// Implementation of native method 'arrayIndexScale'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="arrayClass"></param>
        /// <returns></returns>
        public static int arrayIndexScale(object self, global::java.lang.Class arrayClass)
        {
            return ArrayIndexScale(RuntimeJavaType.FromClass(arrayClass));
        }

        /// <summary>
        /// Implementation of native method 'addressSize'.
        /// </summary>
        /// <param name="self"></param>
        /// <returns></returns>
        public static int addressSize(object self)
        {
            return IntPtr.Size;
        }

        /// <summary>
        /// Implementation of native method 'pageSize'.
        /// </summary>
        /// <param name="self"></param>
        /// <returns></returns>
        public static int pageSize(object self)
        {
            return Environment.SystemPageSize;
        }

        /// <summary>
        /// Implementation of native method 'defineClass'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="name"></param>
        /// <param name="b"></param>
        /// <param name="off"></param>
        /// <param name="len"></param>
        /// <param name="loader"></param>
        /// <param name="protectionDomain"></param>
        /// <returns></returns>
        public static global::java.lang.Class defineClass(object self, string name, byte[] b, int off, int len, global::java.lang.ClassLoader loader, global::java.security.ProtectionDomain protectionDomain)
        {
            return IKVM.Java.Externs.java.lang.ClassLoader.defineClass1(loader, name.Replace('/', '.'), b, off, len, protectionDomain, null);
        }

        /// <summary>
        /// Implementation of native method 'defineAnonymousClass'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="hostClass"></param>
        /// <param name="data"></param>
        /// <param name="cpPatches"></param>
        /// <returns></returns>
        /// <exception cref="global::java.lang.ClassFormatError"></exception>
        public static global::java.lang.Class defineAnonymousClass(object self, global::java.lang.Class hostClass, byte[] data, object[] cpPatches)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                var tw = RuntimeJavaType.FromClass(hostClass);
                var cl = tw.ClassLoader;
                var cf = new IKVM.Runtime.ClassFile(JVM.Context, JVM.Context.Diagnostics, ReadClass(data), "<Unknown>", cl.ClassFileParseOptions, cpPatches);

                // if this happens, the OpenJDK is probably trying to load an OpenJDK class file as a resource,
                // make sure the build process includes the original class file as a resource in that case
                if (cf.IKVMAssemblyAttribute != null)
                    throw new global::java.lang.ClassFormatError("Trying to define anonymous class based on stub class: " + cf.Name);

                return cl.GetTypeWrapperFactory().DefineClassImpl(null, tw, cf, cl, hostClass.pd).ClassObject;
            }
            catch (RetargetableJavaException e)
            {
                throw e.ToJava();
            }
#endif
        }

#if FIRST_PASS == false

        /// <summary>
        /// Attempts to read the class, handling exceptions.
        /// </summary>
        /// <param name="buffer"></param>
        /// <returns></returns>
        static IKVM.ByteCode.Decoding.ClassFile ReadClass(byte[] buffer)
        {
            try
            {
                return IKVM.ByteCode.Decoding.ClassFile.Read(buffer);
            }
            catch (InvalidClassMagicException)
            {
                throw new global::java.lang.ClassFormatError("Incompatible magic value");
            }
            catch (ByteCodeException e)
            {
                throw new global::java.lang.ClassFormatError(e.Message);
            }
        }

#endif

        /// <summary>
        /// Implementation of native method 'allocateInstance'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="cls"></param>
        /// <returns></returns>
        public static object allocateInstance(object self, global::java.lang.Class cls)
        {
            var wrapper = RuntimeJavaType.FromClass(cls);
            try
            {
                wrapper.Finish();
            }
            catch (RetargetableJavaException x)
            {
                throw x.ToJava();
            }

#if NETFRAMEWORK
            return FormatterServices.GetUninitializedObject(wrapper.TypeAsBaseType);
#else
            return RuntimeHelpers.GetUninitializedObject(wrapper.TypeAsBaseType);
#endif
        }

        /// <summary>
        /// Implementation of native method 'monitorEnter'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        public static void monitorEnter(object self, object o)
        {
            Monitor.Enter(o);
        }

        /// <summary>
        /// Implementation of native method 'monitorExit'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        public static void monitorExit(object self, object o)
        {
            Monitor.Exit(o);
        }

        /// <summary>
        /// Implementation of native method 'tryMonitorEnter'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <returns></returns>
        public static bool tryMonitorEnter(object self, object o)
        {
            return Monitor.TryEnter(o);
        }

        /// <summary>
        /// Implementation of native method 'throwException'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="ee"></param>
        public static void throwException(object self, Exception ee)
        {
            throw ee;
        }

        /// <summary>
        /// Implements the logic to get a field by offset using volatile.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        /// <exception cref="global::java.lang.InternalError"></exception>
        static T GetFieldVolatile<T>(object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var f = GetFieldByOffset(o, offset);
            if (o is RuntimeJavaType w)
            {
                if (w != f.DeclaringType)
                    throw new global::java.lang.IllegalArgumentException();

                return f.UnsafeVolatileGet<T>(null);
            }

            return f.UnsafeVolatileGet<T>(o);
#endif
        }

        /// <summary>
        /// Implements the logic to set a field by offset using volatile.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        /// <exception cref="global::java.lang.InternalError"></exception>
        static void PutFieldVolatile<T>(object o, long offset, T value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var f = GetFieldByOffset(o, offset);
            if (o is RuntimeJavaType w)
            {
                if (w != f.DeclaringType)
                    throw new global::java.lang.IllegalArgumentException();

                f.UnsafeVolatileSet<T>(null, value);
            }

            f.UnsafeVolatileSet<T>(o, value);
#endif
        }

        /// <summary>
        /// Creates a delegate capable of accessing an index of a specific type.
        /// </summary>
        /// <param name="tw"></param>
        /// <returns></returns>
        static Delegate CreateGetArrayVolatileDelegate(RuntimeJavaType tw)
        {
            var et = tw.IsPrimitive ? tw.TypeAsTBD : typeof(object);
            var dm = DynamicMethodUtil.Create($"__<UnsafeGetArrayVolatile>__{tw.Name.Replace(".", "_")}", tw.TypeAsTBD, true, et, new[] { typeof(object[]), typeof(long) });
            var il = dm.GetILGenerator();

            // load reference to element
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Conv_Ovf_I);
            il.Emit(OpCodes.Ldc_I4, ArrayIndexScale(tw.MakeArrayType(1)));
            il.Emit(OpCodes.Div);
            il.Emit(OpCodes.Conv_Ovf_I);
            il.Emit(OpCodes.Ldelema, tw.TypeAsLocalOrStackType);

            if (tw.IsWidePrimitive == false)
            {
                il.Emit(OpCodes.Volatile);
                EmitLdind(il, tw.TypeAsLocalOrStackType);
            }
            else
            {
                // Java volatile semantics require atomicity, CLR volatile semantics do not
                var mi = typeof(Unsafe).GetMethod(nameof(InterlockedRead), BindingFlags.NonPublic | BindingFlags.Static, null, new[] { tw.TypeAsTBD.MakeByRefType() }, null);
                il.Emit(OpCodes.Call, mi);
            }

            il.Emit(OpCodes.Ret);
            return dm.CreateDelegate(typeof(Func<,,>).MakeGenericType(typeof(object[]), typeof(long), et));
        }

        /// <summary>
        /// Implements the logic to get an object array by offset using volatile.
        /// </summary>
        /// <param name="array"></param>
        /// <param name="offset"></param>
        /// <exception cref="global::java.lang.InternalError"></exception>
        static object GetArrayObjectVolatile(object[] array, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return ((Func<object[], long, object>)arrayRefCache.GetValue(JVM.Context.ClassLoaderFactory.GetJavaTypeFromType(array.GetType().GetElementType()), _ => new ArrayDelegateRef(_)).VolatileGetter)(array, offset);
#endif
        }

        /// <summary>
        /// Creates a delegate capable of accessing an index of a specific type.
        /// </summary>
        /// <param name="tw"></param>
        /// <returns></returns>
        static Delegate CreatePutArrayVolatileDelegate(RuntimeJavaType tw)
        {
            var et = tw.IsPrimitive ? tw.TypeAsTBD : typeof(object);
            var dm = DynamicMethodUtil.Create($"__<UnsafePutArrayVolatile>__{tw.Name.Replace(".", "_")}", tw.TypeAsTBD, true, typeof(void), new[] { typeof(object[]), typeof(long), et });
            var il = dm.GetILGenerator();

            // load reference to element
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Conv_Ovf_I);
            il.Emit(OpCodes.Ldc_I4, ArrayIndexScale(tw.MakeArrayType(1)));
            il.Emit(OpCodes.Div);
            il.Emit(OpCodes.Conv_Ovf_I);
            il.Emit(OpCodes.Ldelema, tw.TypeAsLocalOrStackType);

            il.Emit(OpCodes.Ldarg_2);

            if (tw.IsWidePrimitive == false)
            {
                il.Emit(OpCodes.Volatile);
                EmitStind(il, tw.TypeAsLocalOrStackType);
            }
            else
            {
                // Java volatile semantics require atomicity, CLR volatile semantics do not
                var mi = typeof(Interlocked).GetMethod(nameof(Interlocked.Exchange), new[] { tw.TypeAsTBD.MakeByRefType() });
                il.Emit(OpCodes.Call, mi);
            }

            il.Emit(OpCodes.Ret);
            return dm.CreateDelegate(typeof(Action<,,>).MakeGenericType(typeof(object[]), typeof(long), et));
        }

        /// <summary>
        /// Implements the logic to set an object array by offset using volatile.
        /// </summary>
        /// <param name="array"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        /// <exception cref="global::java.lang.InternalError"></exception>
        static void PutArrayObjectVolatile(object[] array, long offset, object value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            ((Action<object[], long, object>)arrayRefCache.GetValue(JVM.Context.ClassLoaderFactory.GetJavaTypeFromType(array.GetType().GetElementType()), _ => new ArrayDelegateRef(_)).VolatilePutter)(array, offset, value);
#endif
        }

        /// <summary>
        /// Implementation of native method 'getObjectVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static object getObjectVolatile(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    RuntimeJavaType w => GetField<object>(null, offset),
                    object[] array when array.GetType() == typeof(object[]) => Volatile.Read(ref array[offset / IntPtr.Size]),
                    object[] array => GetArrayObjectVolatile(array, offset),
                    object obj => GetFieldVolatile<object>(obj, offset),
                    _ => throw new global::java.lang.IllegalArgumentException(),
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putObjectVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static void putObjectVolatile(object self, object o, long offset, object x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                switch (o)
                {
                    case RuntimeJavaType w:
                        PutField(w, offset, x);
                        break;
                    case object[] array when array.GetType() == typeof(object[]):
                        Volatile.Write(ref array[offset / IntPtr.Size], x);
                        break;
                    case object[] array:
                        PutArrayObjectVolatile(array, offset, x);
                        break;
                    case object obj:
                        PutFieldVolatile(o, offset, x);
                        break;
                    default:
                        throw new global::java.lang.IllegalArgumentException();
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getIntVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static unsafe int getIntVolatile(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    null => Volatile.Read(ref System.Runtime.CompilerServices.Unsafe.AsRef<int>((void*)JLongAddressToIntPtr(offset))),
                    RuntimeJavaType w => GetFieldVolatile<int>(null, offset),
                    Array array => ReadInt32Volatile(array, offset),
                    _ => GetFieldVolatile<int>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putIntVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static unsafe void putIntVolatile(object self, object o, long offset, int x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                switch (o)
                {
                    case null:
                        Volatile.Write(ref System.Runtime.CompilerServices.Unsafe.AsRef<int>((void*)JLongAddressToIntPtr(offset)), x);
                        break;
                    case RuntimeJavaType w:
                        PutFieldVolatile(w, offset, x);
                        break;
                    case Array array:
                        WriteInt32Volatile(array, offset, x);
                        break;
                    default:
                        PutFieldVolatile(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getBooleanVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static unsafe bool getBooleanVolatile(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    null => Volatile.Read(ref System.Runtime.CompilerServices.Unsafe.AsRef<byte>((void*)JLongAddressToIntPtr(offset))) != 0,
                    RuntimeJavaType w => GetFieldVolatile<bool>(null, offset),
                    Array array => ReadByteVolatile(array, offset) != 0,
                    _ => GetFieldVolatile<bool>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putBooleanVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static unsafe void putBooleanVolatile(object self, object o, long offset, bool x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                switch (o)
                {
                    case null:
                        Volatile.Write(ref System.Runtime.CompilerServices.Unsafe.AsRef<byte>((void*)JLongAddressToIntPtr(offset)), x ? (byte)1 : (byte)0);
                        break;
                    case RuntimeJavaType w:
                        PutFieldVolatile(w, offset, x);
                        break;
                    case Array array:
                        WriteByteVolatile(array, offset, x ? (byte)1 : (byte)0);
                        break;
                    default:
                        PutFieldVolatile(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getByteVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static unsafe byte getByteVolatile(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    null => Volatile.Read(ref System.Runtime.CompilerServices.Unsafe.AsRef<byte>((void*)JLongAddressToIntPtr(offset))),
                    RuntimeJavaType w => GetFieldVolatile<byte>(null, offset),
                    Array array => ReadByteVolatile(array, offset),
                    _ => GetFieldVolatile<byte>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putByteVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static unsafe void putByteVolatile(object self, object o, long offset, byte x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                switch (o)
                {
                    case null:
                        Volatile.Write(ref System.Runtime.CompilerServices.Unsafe.AsRef<byte>((void*)JLongAddressToIntPtr(offset)), x);
                        break;
                    case RuntimeJavaType w:
                        PutFieldVolatile(w, offset, x);
                        break;
                    case Array array:
                        WriteByteVolatile(array, offset, x);
                        break;
                    default:
                        PutFieldVolatile(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getShortVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static short getShortVolatile(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    Array array => ReadInt16Volatile(array, offset),
                    _ => GetFieldVolatile<short>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putShortVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static unsafe void putShortVolatile(object self, object o, long offset, short x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                switch (o)
                {
                    case null:
                        Volatile.Write(ref System.Runtime.CompilerServices.Unsafe.AsRef<short>((void*)JLongAddressToIntPtr(offset)), x);
                        break;
                    case RuntimeJavaType w:
                        PutFieldVolatile(w, offset, x);
                        break;
                    case Array array:
                        WriteInt16Volatile(array, offset, x);
                        break;
                    default:
                        PutFieldVolatile(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getCharVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static char getCharVolatile(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    Array array => (char)ReadInt16Volatile(array, offset),
                    _ => GetFieldVolatile<char>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putCharVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static unsafe void putCharVolatile(object self, object o, long offset, char x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                switch (o)
                {
                    case null:
                        Volatile.Write(ref System.Runtime.CompilerServices.Unsafe.AsRef<short>((void*)JLongAddressToIntPtr(offset)), (short)x);
                        break;
                    case RuntimeJavaType w:
                        PutFieldVolatile(w, offset, x);
                        break;
                    case Array array:
                        WriteInt16Volatile(array, offset, (short)x);
                        break;
                    default:
                        PutFieldVolatile(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getLongVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        public static long getLongVolatile(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    Array array => ReadInt64Volatile(array, offset),
                    _ => GetFieldVolatile<long>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putLongVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static unsafe void putLongVolatile(object self, object o, long offset, long x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                switch (o)
                {
                    case null:
                        Volatile.Write(ref System.Runtime.CompilerServices.Unsafe.AsRef<long>((void*)JLongAddressToIntPtr(offset)), x);
                        break;
                    case RuntimeJavaType w:
                        PutFieldVolatile(w, offset, x);
                        break;
                    case Array array:
                        WriteInt64Volatile(array, offset, x);
                        break;
                    default:
                        PutFieldVolatile(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getFloatVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        /// <exception cref="global::java.lang.NullPointerException"></exception>
        public static float getFloatVolatile(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    Array array => global::java.lang.Float.intBitsToFloat(ReadInt32Volatile(array, offset)),
                    _ => GetFieldVolatile<float>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putFloatVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static unsafe void putFloatVolatile(object self, object o, long offset, float x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                switch (o)
                {
                    case null:
                        Volatile.Write(ref System.Runtime.CompilerServices.Unsafe.AsRef<float>((void*)JLongAddressToIntPtr(offset)), x);
                        break;
                    case RuntimeJavaType w:
                        PutFieldVolatile(w, offset, x);
                        break;
                    case Array array:
                        WriteInt32Volatile(array, offset, global::java.lang.Float.floatToRawIntBits(x));
                        break;
                    default:
                        PutFieldVolatile(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'getDoubleVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        /// <exception cref="global::java.lang.NullPointerException"></exception>
        public static double getDoubleVolatile(object self, object o, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    Array array => global::java.lang.Double.longBitsToDouble(ReadInt64Volatile(array, offset)),
                    _ => GetFieldVolatile<double>(o, offset)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'putDoubleVolatile'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="x"></param>
        public static unsafe void putDoubleVolatile(object self, object o, long offset, double x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                switch (o)
                {
                    case null:
                        Volatile.Write(ref System.Runtime.CompilerServices.Unsafe.AsRef<double>((void*)JLongAddressToIntPtr(offset)), x);
                        break;
                    case RuntimeJavaType w:
                        PutFieldVolatile(w, offset, x);
                        break;
                    case Array array:
                        WriteInt64Volatile(array, offset, global::java.lang.Double.doubleToRawLongBits(x));
                        break;
                    default:
                        PutFieldVolatile(o, offset, x);
                        break;
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        public static void putOrderedObject(object self, object o, long offset, object x)
        {
            putObjectVolatile(self, o, offset, x);
        }

        public static void putOrderedInt(object self, object o, long offset, int x)
        {
            putIntVolatile(self, o, offset, x);
        }

        public static void putOrderedLong(object self, object o, long offset, long x)
        {
            putLongVolatile(self, o, offset, x);
        }

        /// <summary>
        /// Implements the logic to compare and swap an object.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        /// <param name="expected"></param>
        /// <returns></returns>
        /// <exception cref="global::java.lang.InternalError"></exception>
        static bool CompareAndSwapField<T>(object o, long offset, T expected, T value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return GetFieldByOffset(o, offset).UnsafeCompareAndSwap(o, expected, value);
#endif
        }

        /// <summary>
        /// Creates a delegate capable of accessing an index of a specific type.
        /// </summary>
        /// <param name="t"></param>
        /// <returns></returns>
        static Delegate CreateCompareExchangeArrayDelegate(RuntimeJavaType tw)
        {
            var p = Expression.Parameter(typeof(object[]));
            var i = Expression.Parameter(typeof(long));
            var v = Expression.Parameter(typeof(object));
            var e = Expression.Parameter(typeof(object));
            return Expression.Lambda<Func<object[], long, object, object, object>>(
                Expression.Call(
                    compareAndSwapArrayMethodInfo.MakeGenericMethod(tw.TypeAsTBD),
                    Expression.Convert(p, tw.MakeArrayType(1).TypeAsTBD),
                    i,
                    Expression.Convert(v, tw.TypeAsTBD),
                    Expression.Convert(e, tw.TypeAsTBD)),
                p, i, v, e)
                .Compile();
        }

        /// <summary>
        /// Implements CompareAndSwap for a typed array.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        /// <param name="comparand"></param>
        /// <returns></returns>
        static object CompareAndSwapArray<T>(T[] o, long offset, T value, T comparand)
            where T : class
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return Interlocked.CompareExchange<T>(ref o[offset / ArrayIndexScale(JVM.Context.ClassLoaderFactory.GetJavaTypeFromType(o.GetType()))], value, comparand);
#endif
        }

        /// <summary>
        /// Implements the logic to compare and swap an object.
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        /// <param name="expected"></param>
        /// <returns></returns>
        /// <exception cref="global::java.lang.InternalError"></exception>
        static object CompareAndSwapObjectArray(object[] o, long offset, object value, object expected)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            return ((Func<object[], long, object, object, object>)arrayRefCache.GetValue(JVM.Context.ClassLoaderFactory.GetJavaTypeFromType(o.GetType().GetElementType()), _ => new ArrayDelegateRef(_)).CompareExchange)(o, offset, value, expected);
#endif
        }

        /// <summary>
        /// Implementation of native method 'compareAndSwapObject'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="expected"></param>
        /// <param name="x"></param>
        /// <returns></returns>
        public static bool compareAndSwapObject(object self, object o, long offset, object expected, object x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return o switch
                {
                    object[] array when array.GetType() == typeof(object[]) => Interlocked.CompareExchange(ref array[offset / IntPtr.Size], x, expected) == expected,
                    object[] array => CompareAndSwapObjectArray(array, offset, x, expected) == expected,
                    _ => CompareAndSwapField(o, offset, expected, x)
                };
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'compareAndSwapInt'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="expected"></param>
        /// <param name="x"></param>
        /// <returns></returns>
        public static bool compareAndSwapInt(object self, object o, long offset, int expected, int x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o is int[] array && (offset % sizeof(int)) == 0)
                {
                    return Interlocked.CompareExchange(ref array[offset / sizeof(int)], x, expected) == expected;
                }
                else if (o is Array array1 && (offset % sizeof(int)) == 0)
                {
                    return CompareExchangeInt32(array1, offset, x, expected) == expected;
                }
                else if (o is Array array2)
                {
                    return CompareExchangeInt32Unaligned(array2, offset, x, expected) == expected;
                }
                else
                {
                    return CompareAndSwapField(o, offset, expected, x);
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implementation of native method 'compareAndSwapLong'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="o"></param>
        /// <param name="offset"></param>
        /// <param name="expected"></param>
        /// <param name="x"></param>
        /// <returns></returns>
        public static bool compareAndSwapLong(object self, object o, long offset, long expected, long x)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                if (o is long[] array && (offset % sizeof(long)) == 0)
                {
                    return Interlocked.CompareExchange(ref array[offset / sizeof(long)], x, expected) == expected;
                }
                else if (o is Array array1 && (offset % sizeof(long)) == 0)
                {
                    return CompareExchangeInt64(array1, offset, x, expected) == expected;
                }
                else if (o is Array array2)
                {
                    return CompareExchangeInt64Unaligned(array2, offset, x, expected) == expected;
                }
                else
                {
                    return CompareAndSwapField(o, offset, expected, x);
                }
            }
            catch (Exception e) when (e is not global::java.lang.Throwable)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        const int PARK_STATE_RUNNING = 0;
        const int PARK_STATE_PERMIT = 1;
        const int PARK_STATE_PARKED = 2;

        /// <summary>
        /// Implements the native method 'unpark'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="thread"></param>
        public static void unpark(object self, object thread)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var currentThread = (global::java.lang.Thread)thread;
            if (currentThread == null)
                throw new global::java.lang.IllegalStateException();

            if (currentThread != null)
            {
                var parkStateWas = Interlocked.CompareExchange(ref currentThread.parkState, PARK_STATE_PERMIT, PARK_STATE_RUNNING);

                // Wake anything sleeping in FutexPark. Ordered AFTER the permit store so a waiter that
                // wakes is guaranteed to see it. Unconditional because a post with no waiter is an atomic
                // add plus a notify that finds nothing -- cheaper than tracking whether to send it.
                if (LibIkvm.CanParkNanos)
                    LibIkvm.ParkPost((int)currentThread.getId());

                if (parkStateWas == PARK_STATE_PARKED)
                {
                    if (Interlocked.CompareExchange(ref currentThread.parkState, PARK_STATE_RUNNING, PARK_STATE_PARKED) == PARK_STATE_PARKED)
                    {
                        // initialize lock
                        Interlocked.CompareExchange(ref currentThread.parkLock, new global::java.lang.Object(), null);

                        // thread is currently blocking, so we have to release it
                        lock (currentThread.parkLock)
                            Monitor.Pulse(currentThread.parkLock);
                    }
                }
            }
#endif
        }

        /// <summary>
        /// Waits up to <paramref name="nanos"/> nanoseconds for an unpark, for requests too short to
        /// express through Object.wait(long, int). See the long note in <see cref="park"/> for why this
        /// path exists.
        /// </summary>
        /* Guarded like every other body in this file that touches java.lang.Thread's package-private
         * fields: FIRST_PASS compiles against a stub IKVM.Java in which `parkState` does not exist. */
#if !FIRST_PASS
        static void FutexPark(global::java.lang.Thread thread, long nanos)
        {
            // Keyed by the Java thread id rather than the OS thread, because park and unpark address a
            // Thread OBJECT. Truncating to int is safe against the native table, which masks into range;
            // a collision there can only cause a spurious early return, which park is allowed to do.
            var key = (int)thread.getId();

            // Read the wakeup sequence BEFORE testing the permit. That order is the entire race argument:
            // if an unpark lands between this load and the wait, it has already bumped the word, so the
            // wait compares unequal and returns at once rather than sleeping through a granted permit.
            var seq = LibIkvm.ParkSeq(key);

            if (Interlocked.CompareExchange(ref thread.parkState, PARK_STATE_RUNNING, PARK_STATE_PERMIT) == PARK_STATE_PERMIT)
                return;

            LibIkvm.ParkWait(key, seq, nanos);

            // Consume a permit that arrived while we slept. This must run on EVERY exit path including the
            // timeout: a permit left set would make the next park return immediately, which is the same
            // lost-wakeup bug in the opposite direction.
            Interlocked.CompareExchange(ref thread.parkState, PARK_STATE_RUNNING, PARK_STATE_PERMIT);
        }
#endif

        /// <summary>
        /// Implements the native method 'park'.
        /// </summary>
        /// <param name="self"></param>
        /// <param name="isAbsolute"></param>
        /// <param name="time"></param>
        public static void park(object self, bool isAbsolute, long time)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var currentThread = global::java.lang.Thread.currentThread();
            if (currentThread == null)
                throw new global::java.lang.IllegalStateException();

            if (Interlocked.CompareExchange(ref currentThread.parkState, PARK_STATE_RUNNING, PARK_STATE_PERMIT) == PARK_STATE_PERMIT)
                return;

            // A SUB-MILLISECOND RELATIVE PARK CANNOT GO THROUGH Object.wait(long, int), AND SILENTLY
            // BECOMES A 1 ms PARK IF IT DOES.
            //
            // The blocking path below calls parkLock.wait(time / 1000000, time % 1000000). For any
            // request under a millisecond the integer division makes the millisecond argument 0, and
            // OpenJDK's Object.wait(long, int) contract then rounds the leftover nanoseconds UP to a whole
            // millisecond:
            //
            //     if (nanos > 0) { timeout++; }   // java/lang/Object.java
            //     wait(timeout);
            //
            // so parkNanos(100_000) -- 100 us -- waits 1 ms. Measured on Minecraft 1.16 under IKVM/wasm:
            // 34,736 parks in one run, mean 1075 us, 91.5% landing in the 1-2 ms bucket, for a requested
            // total of 3.5 s that cost 37.3 s of wall. That is a ~12x overshoot on every call.
            //
            // WHY IT MATTERS RATHER THAN JUST IDLING. Callers use a short parkNanos as "check again very
            // soon" for work that arrives WITHOUT an unpark. Minecraft's BlockableEventLoop is the case
            // that exposed it: tell() does unpark the thread, but the server thread also drains chunk
            // tasks via ServerChunkCache.pollTask(), a poll of a queue nobody unparks it for. So the
            // effective poll interval is the park duration, and a 12x overshoot is a 12x latency penalty
            // on every handoff in a pipeline that crosses that boundary repeatedly.
            //
            // THE FIX: honour sub-millisecond requests with a real nanosecond-resolution wait instead of
            // a rounded-up block. On wasm that is memory.atomic.wait32, reached through emscripten's
            // futex wrapper, which takes a DOUBLE of milliseconds and converts with
            // `max_wait_ns = (int64_t)(max_wait_ms*1000*1000)` -- no integer rounding, so 100 us survives.
            //
            // WHY NOT A SPIN, which is the obvious alternative and was tried first: neither of the two
            // primitives a spin would rest on does anything on this runtime. Thread.Yield() lowers to
            // `return TRUE` (mono/utils/mono-threads-wasm.c:169) and Thread.SpinWait() is a relaxed-NOP
            // loop (Thread.Mono.cs:206), so a spin occupies its worker continuously instead of yielding
            // it. That converts park from a blocking operation into a CPU-delay loop -- and it also makes
            // the change unmeasurable, because the metric this was diagnosed with is thread parallelism
            // (1.48 -> 0.73 cores across the stall), which a busy-wait inflates in exactly the direction
            // that looks like a fix.
            //
            // Three properties make the futex path safe:
            //   * It runs OUTSIDE the parkLock. Blocking while holding it would block unpark(), which takes
            //     the same lock to Monitor.Pulse.
            //   * It never sets PARK_STATE_PARKED, so a concurrent unpark() takes its cheap path and simply
            //     stores PARK_STATE_PERMIT -- which FutexPark observes and consumes -- then posts the
            //     wakeup word. No pulse, and no lock is touched on either side.
            //   * Returning when the timeout expires is spec-legal: LockSupport.park is explicitly
            //     permitted to return spuriously, and every caller re-checks its own condition in a loop.
            //
            // Gated on CanParkNanos so the desktop CoreCLR targets (net472/net8.0), which have no such
            // wait available, keep the Monitor path they have always used.
            const long NanosPerMillisecond = 1_000_000;
            if (!isAbsolute && time > 0 && time < NanosPerMillisecond && LibIkvm.CanParkNanos)
            {
                FutexPark(currentThread, time);
                return;
            }

            // initialize lock
            Interlocked.CompareExchange(ref currentThread.parkLock, new global::java.lang.Object(), null);

            // lock thread
            lock (currentThread.parkLock)
            {
                if (Interlocked.CompareExchange(ref currentThread.parkState, PARK_STATE_PARKED, PARK_STATE_RUNNING) == PARK_STATE_PERMIT)
                {
                    Interlocked.CompareExchange(ref currentThread.parkState, PARK_STATE_RUNNING, PARK_STATE_PERMIT);
                    return;
                }

                if (isAbsolute)
                {
                    time *= 1000000;
                    time -= global::java.lang.System.currentTimeMillis() * 1000000;
                }

                if (time >= 0)
                {
                    try
                    {
                        ((global::java.lang.Object)currentThread.parkLock).wait(time / 1000000, (int)(time % 1000000));
                    }
                    catch (global::java.lang.InterruptedException)
                    {
                        currentThread.interrupt();
                    }
                }

                Interlocked.CompareExchange(ref currentThread.parkState, PARK_STATE_RUNNING, PARK_STATE_PARKED);
            }
#endif
        }

        public static int getLoadAverage(object self, double[] loadavg, int nelems)
        {
            return -1;
        }

        public static void loadFence(object self)
        {
            Thread.MemoryBarrier();
        }

        public static void storeFence(object self)
        {
            Thread.MemoryBarrier();
        }

        public static void fullFence(object self)
        {
            Thread.MemoryBarrier();
        }

        /// <summary>
        /// Reads an <see cref="byte"/> from the array at the specific byte offset.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        static unsafe byte ReadByte(Array obj, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                var v = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<byte>((byte*)h.AddrOfPinnedObject() + offset);
                return v;
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Reads an <see cref="byte"/> from the array at the specific byte offset.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        static unsafe byte ReadByteVolatile(Array obj, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                ref var r = ref System.Runtime.CompilerServices.Unsafe.AsRef<byte>((byte*)h.AddrOfPinnedObject() + offset);
                var v = Volatile.Read(ref r);
                return v;
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Reads an <see cref="short"/> from the array at the specific byte offset.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        static unsafe short ReadInt16(Array obj, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                var v = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<short>((byte*)h.AddrOfPinnedObject() + offset);
                return v;
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Reads an <see cref="short"/> from the array at the specific byte offset.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        static unsafe short ReadInt16Volatile(Array obj, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                ref var r = ref System.Runtime.CompilerServices.Unsafe.AsRef<short>((byte*)h.AddrOfPinnedObject() + offset);
                var v = Volatile.Read(ref r);
                return v;
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Reads an <see cref="int"/> from the array at the specific byte offset.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        static unsafe int ReadInt32(Array obj, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                var v = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<int>((byte*)h.AddrOfPinnedObject() + offset);
                return v;
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Reads an <see cref="int"/> from the array at the specific byte offset.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        static unsafe int ReadInt32Volatile(Array obj, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                ref var r = ref System.Runtime.CompilerServices.Unsafe.AsRef<int>((byte*)h.AddrOfPinnedObject() + offset);
                var v = Volatile.Read(ref r);
                return v;
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Reads an <see cref="long"/> from the array at the specific byte offset.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        static unsafe long ReadInt64(Array obj, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                var v = System.Runtime.CompilerServices.Unsafe.ReadUnaligned<long>((byte*)h.AddrOfPinnedObject() + offset);
                return v;
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Reads an <see cref="long"/> from the array at the specific byte offset.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <returns></returns>
        static unsafe long ReadInt64Volatile(Array obj, long offset)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                ref var r = ref System.Runtime.CompilerServices.Unsafe.AsRef<long>((byte*)h.AddrOfPinnedObject() + offset);
                var v = Interlocked.Read(ref r); // java considers volatile to be atomic
                return v;
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Writes a <see cref="byte"/> to the specified byte offset within the array.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        static unsafe void WriteByte(Array obj, long offset, byte value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned((byte*)h.AddrOfPinnedObject() + offset, value);
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Writes a <see cref="byte"/> to the specified byte offset within the array.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        static unsafe void WriteByteVolatile(Array obj, long offset, byte value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                Volatile.Write(ref System.Runtime.CompilerServices.Unsafe.AsRef<byte>((byte*)h.AddrOfPinnedObject() + offset), value);
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Writes a <see cref="short"/> to the specified byte offset within the array.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        static unsafe void WriteInt16(Array obj, long offset, short value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned((byte*)h.AddrOfPinnedObject() + offset, value);
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Writes a <see cref="short"/> to the specified byte offset within the array.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        static unsafe void WriteInt16Volatile(Array obj, long offset, short value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                Volatile.Write(ref System.Runtime.CompilerServices.Unsafe.AsRef<short>((byte*)h.AddrOfPinnedObject() + offset), value);
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Writes a <see cref="int"/> to the specified byte offset within the array.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        static unsafe void WriteInt32(Array obj, long offset, int value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned((byte*)h.AddrOfPinnedObject() + offset, value);
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Writes a <see cref="int"/> to the specified byte offset within the array.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        static unsafe void WriteInt32Volatile(Array obj, long offset, int value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                Volatile.Write(ref System.Runtime.CompilerServices.Unsafe.AsRef<int>((byte*)h.AddrOfPinnedObject() + offset), value);
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Writes a <see cref="long"/> to the specified byte offset within the array.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        static unsafe void WriteInt64(Array obj, long offset, long value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                System.Runtime.CompilerServices.Unsafe.WriteUnaligned((byte*)h.AddrOfPinnedObject() + offset, value);
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Writes a <see cref="long"/> to the specified byte offset within the array.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        static unsafe void WriteInt64Volatile(Array obj, long offset, long value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                Interlocked.Exchange(ref System.Runtime.CompilerServices.Unsafe.AsRef<long>((byte*)h.AddrOfPinnedObject() + offset), value);
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Executes CompareExchange against the specified byte offset with the array.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        /// <param name="expected"></param>
        /// <returns></returns>
        static unsafe int CompareExchangeInt32(Array obj, long offset, int value, int expected)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                var r = Interlocked.CompareExchange(ref System.Runtime.CompilerServices.Unsafe.AsRef<int>((byte*)h.AddrOfPinnedObject() + offset), value, expected);
                return r;
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Executes CompareExchange against the specified byte offset with the array.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        /// <param name="expected"></param>
        /// <returns></returns>
        static unsafe int CompareExchangeInt32Unaligned(Array obj, long offset, int value, int expected)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                var r = Interlocked.CompareExchange(ref System.Runtime.CompilerServices.Unsafe.AsRef<int>((byte*)h.AddrOfPinnedObject() + offset), value, expected);
                return r;
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Executes CompareExchange against the specified byte offset with the array.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        /// <param name="expected"></param>
        /// <returns></returns>
        static unsafe long CompareExchangeInt64(Array obj, long offset, long value, long expected)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                var r = Interlocked.CompareExchange(ref System.Runtime.CompilerServices.Unsafe.AsRef<long>((byte*)h.AddrOfPinnedObject() + offset), value, expected);
                return r;
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Executes CompareExchange against the specified byte offset with the array.
        /// </summary>
        /// <param name="obj"></param>
        /// <param name="offset"></param>
        /// <param name="value"></param>
        /// <param name="expected"></param>
        /// <returns></returns>
        static unsafe long CompareExchangeInt64Unaligned(Array obj, long offset, long value, long expected)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            GCHandle h = new();

            try
            {
                h = GCHandle.Alloc(obj, GCHandleType.Pinned);
                var r = Interlocked.CompareExchange(ref System.Runtime.CompilerServices.Unsafe.AsRef<long>((byte*)h.AddrOfPinnedObject() + offset), value, expected);
                return r;
            }
            finally
            {
                if (h.IsAllocated)
                    h.Free();
            }
#endif
        }

        /// <summary>
        /// Implements an Interlocked.Read method for long.
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        static long InterlockedRead(ref long location)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return Interlocked.Read(ref location);
            }
            catch (Exception e)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implements an Interlocked.Read method for double.
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        static double InterlockedRead(ref double location)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                return Interlocked.CompareExchange(ref location, 0, 0);
            }
            catch (Exception e)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implements an Interlocked.Read method for long.
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        static void InterlockedWrite(ref long location, long value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                Interlocked.Exchange(ref location, value);
            }
            catch (Exception e)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

        /// <summary>
        /// Implements an Interlocked.Read method for double.
        /// </summary>
        /// <param name="location"></param>
        /// <returns></returns>
        static void InterlockedWrite(ref double location, double value)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            try
            {
                Interlocked.Exchange(ref location, value);
            }
            catch (Exception e)
            {
                throw new global::java.lang.InternalError(e);
            }
#endif
        }

    }

}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

using IKVM.Attributes;
using IKVM.Runtime;

namespace IKVM.Java.Externs.java.lang
{

#if FIRST_PASS == false

    /// <summary>
    /// The runtime's side of JDK 25's StackWalker (HotSpot's stackwalk.cpp): the Java frames of the current thread's
    /// .NET stack, which a walk hands to StackStreamFactory in batches of ClassFrameInfo or StackFrameInfo objects.
    /// </summary>
    static class StackWalk
    {

        // StackStreamFactory's modes
        internal const int CLASS_INFO_ONLY = 0x2;
        internal const int SHOW_HIDDEN_FRAMES = 0x20;

        const int MN_CALLER_SENSITIVE = global::java.lang.invoke.MethodHandleNatives.Constants.MN_CALLER_SENSITIVE;
        const int MN_HIDDEN_MEMBER = global::java.lang.invoke.MethodHandleNatives.Constants.MN_HIDDEN_MEMBER;

        /// <summary>
        /// A Java frame.
        /// </summary>
        internal sealed class Frame
        {

            internal RuntimeJavaType Type;
            internal MethodBase Method;
            internal RuntimeJavaMethod JavaMethod;
            internal int ILOffset;
            internal bool IsHidden;

            internal string Name => JavaMethod?.Name ?? Method.Name;

            internal string Descriptor => (JavaMethod?.Signature ?? "()V").Replace('.', '/');

            internal int Modifiers => JavaMethod != null ? (int)JavaMethod.Modifiers : 0;

            internal bool IsNative => (Modifiers & (int)global::IKVM.Attributes.Modifiers.Native) != 0;

            internal int LineNumber => IsNative ? -2 : ILOffset != StackFrame.OFFSET_UNKNOWN ? Type.GetSourceLineNumber(Method, ILOffset) : -1;

            internal string FileName => Type.GetSourceFileName();

        }

        /// <summary>
        /// A walk in progress: the frames, and the next one to hand out.
        /// </summary>
        sealed class Walk
        {

            internal List<Frame> Frames;
            internal int Next;

        }

        [ThreadStatic]
        static Dictionary<long, Walk> walks;

        static long nextAnchor;

        // the frames a walk fills in, for StackFrameInfo.expandStackFrameInfo and StackTraceElement.of
        static readonly ConditionalWeakTable<object, Frame> frameInfos = new();

        static readonly FieldInfo stackFrameInfoName = typeof(global::java.lang.StackFrameInfo).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo stackFrameInfoType = typeof(global::java.lang.StackFrameInfo).GetField("type", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo stackFrameInfoBci = typeof(global::java.lang.StackFrameInfo).GetField("bci", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo resolvedMethodNameHolder = typeof(global::java.lang.invoke.ResolvedMethodName).GetField("vmholder", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly MethodInfo doStackWalk = typeof(global::java.lang.StackStreamFactory.AbstractStackWalker).GetMethod("doStackWalk", BindingFlags.Instance | BindingFlags.NonPublic);

        /// <summary>
        /// Gets the Java frames of the current thread's stack, below the stack walker's own (the frames of
        /// StackWalker and of AbstractStackWalker and its subclasses), as HotSpot's fetchFirstBatch.
        /// </summary>
        static List<Frame> GetFrames(int mode, int skipFrames)
        {
            var context = JVM.Context;
            var stackWalker = context.ClassLoaderFactory.GetJavaTypeFromType(typeof(global::java.lang.StackWalker));
            var abstractStackWalker = context.ClassLoaderFactory.GetJavaTypeFromType(typeof(global::java.lang.StackStreamFactory.AbstractStackWalker));

            var frames = new List<Frame>();
            var trace = new StackTrace(1, false);
            var walker = true;
            for (int i = 0; i < trace.FrameCount; i++)
            {
                var sf = trace.GetFrame(i);
                var mb = sf.GetMethod();
                if (mb == null || mb.DeclaringType == null || StackWalkUtil.IsHideFromStackWalk(mb) || IsHideFromJava(mb))
                    continue;

                var tw = context.ClassLoaderFactory.GetJavaTypeFromType(mb.DeclaringType);
                if (tw == null || tw.IsArray || tw.IsPrimitive)
                    continue;

                if (walker)
                {
                    if (tw == stackWalker || tw == abstractStackWalker || tw.BaseTypeWrapper == abstractStackWalker)
                        continue;

                    walker = false;
                }

                var frame = new Frame() { Type = tw, Method = mb, JavaMethod = FindMethod(tw, mb), ILOffset = sf.GetILOffset() };
                frame.IsHidden = tw.IsHidden;
                if ((mode & SHOW_HIDDEN_FRAMES) == 0 && frame.IsHidden)
                    continue;

                if (skipFrames > 0)
                {
                    skipFrames--;
                    continue;
                }

                frames.Add(frame);
            }

            return frames;
        }

        static bool IsHideFromJava(MethodBase mb)
        {
            return (StackWalkUtil.GetHideFromJavaFlags(mb) & HideFromJavaFlags.StackTrace) != 0;
        }

        /// <summary>
        /// Finds the Java method of a .NET method.
        /// </summary>
        static RuntimeJavaMethod FindMethod(RuntimeJavaType tw, MethodBase mb)
        {
            foreach (var mw in tw.GetMethods())
            {
                try
                {
                    if (mw.GetMethod() is MethodBase m && m.MetadataToken == mb.MetadataToken && m.Module == mb.Module)
                        return mw;
                }
                catch (Exception)
                {
                    // a method that does not link is not this frame's
                }
            }

            return null;
        }

        /// <summary>
        /// Fills the frame buffer from the walk's next frame, up to the buffer size, and returns the number filled.
        /// </summary>
        static int Fill(Walk walk, int mode, int bufferSize, int startIndex, object[] frames)
        {
            var end = startIndex;
            while (walk.Next < walk.Frames.Count && end < bufferSize && end < frames.Length)
            {
                var frame = walk.Frames[walk.Next++];
                var info = (global::java.lang.ClassFrameInfo)frames[end++];
                var flags = frame.Modifiers;
                if (frame.JavaMethod != null && frame.JavaMethod.HasCallerID)
                    flags |= MN_CALLER_SENSITIVE;
                if (frame.IsHidden)
                    flags |= MN_HIDDEN_MEMBER;

                info.flags |= flags;
                if ((mode & CLASS_INFO_ONLY) != 0)
                {
                    info.classOrMemberName = frame.Type.ClassObject;
                }
                else
                {
                    var rmn = new global::java.lang.invoke.ResolvedMethodName();
                    resolvedMethodNameHolder.SetValue(rmn, frame.Type.ClassObject);
                    info.classOrMemberName = rmn;
                    stackFrameInfoBci.SetValue(info, 0);
                    frameInfos.AddOrUpdate(info, frame);
                }
            }

            return end - startIndex;
        }

        internal static object CallStackWalk(object self, int mode, int skipframes, int bufferSize, int startIndex, object[] frames)
        {
            var walk = new Walk() { Frames = GetFrames(mode, skipframes) };
            var anchor = Interlocked.Increment(ref nextAnchor);
            walks ??= new Dictionary<long, Walk>();
            walks[anchor] = walk;
            try
            {
                var numFrames = Fill(walk, mode, bufferSize, startIndex, frames);
                return InvokeDoStackWalk(self, anchor, skipframes, numFrames, startIndex, startIndex + numFrames);
            }
            finally
            {
                walks.Remove(anchor);
            }
        }

        static object InvokeDoStackWalk(object self, long anchor, int skipFrames, int numFrames, int start, int end)
        {
            try
            {
                return doStackWalk.Invoke(self, [anchor, skipFrames, numFrames, start, end]);
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                global::System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();
                throw;
            }
        }

        internal static int FetchStackFrames(int mode, long anchor, int bufferSize, int startIndex, object[] frames)
        {
            if (walks == null || walks.TryGetValue(anchor, out var walk) == false)
                throw new global::java.lang.InternalError("doStackWalk: corrupted buffers");

            if (bufferSize <= startIndex)
                return 0;

            return Fill(walk, mode, bufferSize, startIndex, frames);
        }

        internal static void ExpandStackFrameInfo(object self)
        {
            if (frameInfos.TryGetValue(self, out var frame))
            {
                stackFrameInfoName.SetValue(self, frame.Name);
                stackFrameInfoType.SetValue(self, frame.Descriptor);
            }
        }

        internal static Frame GetFrame(object stackFrameInfo)
        {
            return frameInfos.TryGetValue(stackFrameInfo, out var frame) ? frame : null;
        }

    }

#endif

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.lang.StackStreamFactory'.
    /// </summary>
    static class StackStreamFactory
    {

        public static bool checkStackWalkModes()
        {
            return true;
        }

        /// <summary>
        /// Implements the native methods of JDK 25's 'java.lang.StackStreamFactory$AbstractStackWalker'.
        /// </summary>
        public static class AbstractStackWalker
        {

            public static object callStackWalk(object self, int mode, int skipframes, object contScope, object continuation, int bufferSize, int startIndex, object[] frames)
            {
#if FIRST_PASS
                throw new NotImplementedException();
#else
                return StackWalk.CallStackWalk(self, mode, skipframes, bufferSize, startIndex, frames);
#endif
            }

            public static int fetchStackFrames(object self, int mode, long anchor, int lastBatchFrameCount, int bufferSize, int startIndex, object[] frames)
            {
#if FIRST_PASS
                throw new NotImplementedException();
#else
                return StackWalk.FetchStackFrames(mode, anchor, bufferSize, startIndex, frames);
#endif
            }

            public static void setContinuation(object self, long anchor, object[] frames, object cont)
            {
                // the runtime has no continuations: a virtual thread is a thread
            }

        }

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.lang.StackFrameInfo'.
    /// </summary>
    static class StackFrameInfo
    {

        public static void expandStackFrameInfo(object self)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            StackWalk.ExpandStackFrameInfo(self);
#endif
        }

    }

    /// <summary>
    /// Implements the native methods of JDK 25's 'java.lang.StackTraceElement' that the StackWalker uses.
    /// </summary>
    static class StackTraceElement
    {

#if FIRST_PASS == false

        static readonly FieldInfo declaringClassObject = typeof(global::java.lang.StackTraceElement).GetField("declaringClassObject", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo declaringClass = typeof(global::java.lang.StackTraceElement).GetField("declaringClass", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo methodName = typeof(global::java.lang.StackTraceElement).GetField("methodName", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo fileName = typeof(global::java.lang.StackTraceElement).GetField("fileName", BindingFlags.Instance | BindingFlags.NonPublic);
        static readonly FieldInfo lineNumber = typeof(global::java.lang.StackTraceElement).GetField("lineNumber", BindingFlags.Instance | BindingFlags.NonPublic);

#endif

        public static void initStackTraceElement(object element, object sfi)
        {
#if FIRST_PASS
            throw new NotImplementedException();
#else
            var frame = StackWalk.GetFrame(sfi) ?? throw new global::java.lang.InternalError("stack frame not filled in by the stack walk");
            declaringClassObject.SetValue(element, frame.Type.ClassObject);
            declaringClass.SetValue(element, frame.Type.Name);
            methodName.SetValue(element, frame.Name);
            fileName.SetValue(element, frame.FileName);
            lineNumber.SetValue(element, frame.LineNumber);
#endif
        }

    }

}

using System;

namespace IKVM.Runtime.Accessors.Java.Lang
{

#if FIRST_PASS == false && EXPORTER == false && IMPORTER == false

    /// <summary>
    /// Provides runtime access to the 'java.lang.Thread$FieldHolder' type (JDK 25): the fields of a platform thread.
    /// </summary>
    internal sealed class ThreadFieldHolderAccessor : Accessor<object>
    {

        FieldAccessor<object, int> threadStatus;
        FieldAccessor<object, bool> daemon;
        FieldAccessor<object, int> priority;
        FieldAccessor<object, long> stackSize;

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="resolver"></param>
        public ThreadFieldHolderAccessor(AccessorTypeResolver resolver) :
            base(resolver, "java.lang.Thread+FieldHolder")
        {

        }

        /// <summary>
        /// Gets the value of the 'threadStatus' field.
        /// </summary>
        public int GetThreadStatus(object self) => GetField(ref threadStatus, nameof(threadStatus)).GetValue(self);

        /// <summary>
        /// Sets the value of the 'threadStatus' field.
        /// </summary>
        public void SetThreadStatus(object self, int value) => GetField(ref threadStatus, nameof(threadStatus)).SetValue(self, value);

        /// <summary>
        /// Gets the value of the 'daemon' field.
        /// </summary>
        public bool GetDaemon(object self) => GetField(ref daemon, nameof(daemon)).GetValue(self);

        /// <summary>
        /// Sets the value of the 'daemon' field.
        /// </summary>
        public void SetDaemon(object self, bool value) => GetField(ref daemon, nameof(daemon)).SetValue(self, value);

        /// <summary>
        /// Gets the value of the 'priority' field.
        /// </summary>
        public int GetPriority(object self) => GetField(ref priority, nameof(priority)).GetValue(self);

        /// <summary>
        /// Gets the value of the 'stackSize' field.
        /// </summary>
        public long GetStackSize(object self) => GetField(ref stackSize, nameof(stackSize)).GetValue(self);

    }

#endif

}

using System.Diagnostics;

namespace IKVM.Java.Externs.sun.rmi.transport
{

    /// <summary>
    /// Implements the native methods of JDK 25's 'sun.rmi.transport.GC', which librmi answers with HotSpot's
    /// JVM_MaxObjectInspectionAge. RMI's distributed GC uses it to decide when to force a collection for a latency
    /// request (a remote reference's lease).
    /// </summary>
    static class GC
    {

        static readonly object syncRoot = new object();
        static readonly Stopwatch clock = Stopwatch.StartNew();
        static int lastCollectionCount = -1;
        static long lastCollectionTime;

        /// <summary>
        /// The milliseconds since the oldest generation was last collected, as HotSpot answers for a generational
        /// collector. .NET does not say when a collection happened, only how many there have been, so this is the time
        /// since the count was first seen to change: never more than the true age, and back to zero after the collection
        /// the daemon forces once the age reaches the latency target.
        /// </summary>
        public static long maxObjectInspectionAge()
        {
            var count = global::System.GC.CollectionCount(global::System.GC.MaxGeneration);
            var now = clock.ElapsedMilliseconds;

            lock (syncRoot)
            {
                if (count != lastCollectionCount)
                {
                    lastCollectionCount = count;
                    lastCollectionTime = now;
                }

                return now - lastCollectionTime;
            }
        }

    }

}

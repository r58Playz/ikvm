// Polyfill for Mono's MonoPInvokeCallbackAttribute, required by the Mono AOT compiler
// (used for WebAssembly AOT) to identify managed methods that are called back from
// native code via Marshal.GetFunctionPointerForDelegate. Without this attribute the
// AOT compiler cannot generate the necessary trampolines and the callback will crash.
//
// The attribute is normally provided by Xamarin/Mono SDKs. When targeting
// net472/net6.0/net8.0 without those SDKs we declare a local copy so that the same
// source compiles on all target frameworks.

using System;

namespace Mono.Interop
{

    /// <summary>
    /// Marks a static managed method as a callback target from native/unmanaged code.
    /// Required by the Mono AOT compiler when building for WebAssembly.
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    internal sealed class MonoPInvokeCallbackAttribute : Attribute
    {

        /// <summary>
        /// Initializes a new instance.
        /// </summary>
        /// <param name="delegateType">The delegate type whose signature this method implements.</param>
        public MonoPInvokeCallbackAttribute(Type delegateType)
        {
            DelegateType = delegateType;
        }

        /// <summary>
        /// Gets the delegate type.
        /// </summary>
        public Type DelegateType { get; }

    }

}

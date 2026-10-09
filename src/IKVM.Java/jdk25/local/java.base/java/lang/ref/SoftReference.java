/*
  Copyright (C) 2014 Jeroen Frijters

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
package java.lang.ref;

/**
 * IKVM: a soft reference keeps its referent strongly from the time it is read until the next garbage collection, so
 * that a referent in use is not collected. Ported from IKVM's JDK 8 version.
 */
public non-sealed class SoftReference<T> extends Reference<T> {

    public SoftReference(T referent) {
        super(referent);
        setStrongRef(referent);
    }

    public SoftReference(T referent, ReferenceQueue<? super T> q) {
        super(referent, q);
        setStrongRef(referent);
    }

    public T get() {
        T o = strongRef;
        if (o != null) {
            return o;
        }
        o = super.get();
        setStrongRef(o);
        return o;
    }

    private void setStrongRef(T referent) {
        if (referent != null) {
            strongRef = referent;
            if (!(referent instanceof Class)) {
                new Object() {
                    @SuppressWarnings("removal")
                    protected void finalize() {
                        strongRef = null;
                    }
                };
            }
        }
    }

}

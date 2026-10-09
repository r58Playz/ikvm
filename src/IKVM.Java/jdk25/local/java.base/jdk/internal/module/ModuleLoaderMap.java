/*
 * IKVM: JDK 25's jdk.internal.module.ModuleLoaderMap as the JDK's make generates it, with every system module defined
 * to the boot loader.
 */
/*
 * Copyright (c) 2015, 2024, Oracle and/or its affiliates. All rights reserved.
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

package jdk.internal.module;

import java.lang.module.Configuration;
import java.lang.module.ResolvedModule;
import java.util.HashMap;
import java.util.Map;
import java.util.Set;
import java.util.function.Function;

import jdk.internal.loader.ClassLoaders;

/**
 * Supports the mapping of modules to class loaders. The set of modules mapped
 * to the boot and platform class loaders is generated at build time from
 * this source file.
 */
public final class ModuleLoaderMap {

    /**
     * Maps the system modules to the built-in class loaders.
     */
    private static final class Mapper implements Function<String, ClassLoader> {

        private static final ClassLoader PLATFORM_CLASSLOADER =
                ClassLoaders.platformClassLoader();
        private static final ClassLoader APP_CLASSLOADER =
                ClassLoaders.appClassLoader();

        private static final String PLATFORM_LOADER_NAME = "PLATFORM";
        private static final String APP_LOADER_NAME      = "APP";

        /**
         * Map from module name to class loader name. The name is resolved to the
         * actual class loader in {@code apply}.
         */
        private final Map<String, String> map;

        /**
         * Creates a Mapper to map module names in the given Configuration to
         * built-in classloaders.
         *
         * As a proxy for the actual classloader, we store an easily archiveable
         * loader name in the internal map.
         */
        Mapper(Configuration cf) {
            var map = new HashMap<String, String>();
            for (ResolvedModule resolvedModule : cf.modules()) {
                String mn = resolvedModule.name();
                // IKVM: the boot loader loads the IKVM.Java assembly, which holds all of the system modules
                if (!isSystemModule(resolvedModule)) {
                    map.put(mn, APP_LOADER_NAME);
                }
            }
            this.map = map;
        }

        /**
         * IKVM: Returns whether a module is a system module, which the run-time image (jrt) holds.
         */
        private static boolean isSystemModule(ResolvedModule resolvedModule) {
            return resolvedModule.reference().location()
                    .map(uri -> "jrt".equalsIgnoreCase(uri.getScheme()))
                    .orElse(false);
        }

        @Override
        public ClassLoader apply(String name) {
            String loader = map.get(name);
            if (APP_LOADER_NAME.equals(loader)) {
                return APP_CLASSLOADER;
            } else if (PLATFORM_LOADER_NAME.equals(loader)) {
                return PLATFORM_CLASSLOADER;
            } else {
                return null;
            }
        }
    }

    /**
     * Returns the names of the modules defined to the boot loader.
     */
    public static Set<String> bootModules() {
        return Modules.bootModules;
    }

    /**
     * Returns the names of the modules defined to the platform loader.
     */
    public static Set<String> platformModules() {
        return Modules.platformModules;
    }

    /**
     * Returns the names of the modules defined to the application loader which perform native access.
     */
    public static Set<String> nativeAccessModules() {
        return Modules.nativeAccessModules;
    }

    private static class Modules {
        // list of boot modules is generated at build time.
        private static final Set<String> bootModules =
                Set.of(new String[] { "java.base",
            "java.datatransfer",
            "java.desktop",
            "java.instrument",
            "java.logging",
            "java.management",
            "java.management.rmi",
            "java.naming",
            "java.prefs",
            "java.rmi",
            "java.security.sasl",
            "java.xml",
            "jdk.incubator.vector",
            "jdk.internal.vm.ci",
            "jdk.jfr",
            "jdk.management",
            "jdk.management.agent",
            "jdk.management.jfr",
            "jdk.naming.rmi",
            "jdk.net",
            "jdk.nio.mapmode",
            "jdk.sctp",
            "jdk.unsupported" });

        // list of platform modules is generated at build time.
        private static final Set<String> platformModules =
                Set.of(new String[] { "java.compiler",
            "java.net.http",
            "java.scripting",
            "java.se",
            "java.security.jgss",
            "java.smartcardio",
            "java.sql",
            "java.sql.rowset",
            "java.transaction.xa",
            "java.xml.crypto",
            "jdk.accessibility",
            "jdk.charsets",
            "jdk.crypto.cryptoki",
            "jdk.dynalink",
            "jdk.graal.compiler",
            "jdk.graal.compiler.management",
            "jdk.httpserver",
            "jdk.jsobject",
            "jdk.localedata",
            "jdk.naming.dns",
            "jdk.security.auth",
            "jdk.security.jgss",
            "jdk.xml.dom",
            "jdk.zipfs" });

        // list of jdk modules is generated at build time.
        private static final Set<String> nativeAccessModules =
                Set.of(new String[] { "java.base",
            "java.desktop",
            "java.instrument",
            "java.management",
            "java.prefs",
            "java.rmi",
            "java.security.jgss",
            "java.smartcardio",
            "jdk.accessibility",
            "jdk.attach",
            "jdk.compiler",
            "jdk.crypto.cryptoki",
            "jdk.crypto.mscapi",
            "jdk.hotspot.agent",
            "jdk.internal.le",
            "jdk.internal.vm.ci",
            "jdk.jdi",
            "jdk.jfr",
            "jdk.jpackage",
            "jdk.management",
            "jdk.management.agent",
            "jdk.net",
            "jdk.sctp",
            "jdk.security.auth" });
    }

    /**
     * Returns a function to map modules in the given configuration to the
     * built-in class loaders.
     */
    static Function<String, ClassLoader> mappingFunction(Configuration cf) {
        return new Mapper(cf);
    }

    /**
     * When defining modules for a configuration, we only allow defining modules
     * to the boot or platform classloader if the ClassLoader mapping function
     * originate from here.
     */
    public static boolean isBuiltinMapper(Function<String, ClassLoader> clf) {
        return clf instanceof Mapper;
    }
}

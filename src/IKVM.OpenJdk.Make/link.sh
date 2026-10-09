#!/usr/bin/env bash
#
# Links the modules the JDK's make built into a run-time image and extracts it, as the class library IKVM.Java is
# made of: jlink adds what the JDK's own images have and its exploded build does not, the module graph resolved
# ahead of time (jdk.internal.module.SystemModules$all), module descriptors with their packages, version and target
# platform, and the LambdaForm holder classes and BoundMethodHandle species generated ahead of time. The image's conf
# and lib directories, from the modules' configuration and data files, are what JAVA_HOME needs.
#
# link.sh <boot JDK> <JDK build directory> <output directory> <version> <module>...
#
set -euo pipefail

jdk=$1 build=$2 out=$3 version=$4
shift 4

work="$out.tmp"
rm -rf "$work"
mkdir -p "$work/classes" "$work/jmods"

# jlink only takes java.base from a jmod, with a version and a target platform. The make output has the build's own
# _the.* files next to a module's classes.
for module in "$@"; do
    mkdir -p "$work/classes/$module"
    (cd "$build/jdk/modules/$module" && find . -type f ! -name '_the.*' -print0 | cpio -0 -pdm --quiet "$work/classes/$module")
    options=()
    [ -d "$build/support/modules_conf/$module" ] && options+=(--config "$build/support/modules_conf/$module")
    [ -d "$build/support/modules_libs/$module" ] && options+=(--libs "$build/support/modules_libs/$module")
    "$jdk/bin/jmod" create --class-path "$work/classes/$module" "${options[@]}" --module-version "$version" --target-platform linux-amd64 "$work/jmods/$module.jmod"
done

"$jdk/bin/jlink" --module-path "$work/jmods" --add-modules "$(IFS=,; echo "$*")" --output "$work/image"
mkdir -p "$work/modules"
"$jdk/bin/jimage" extract --dir "$work/modules" "$work/image/lib/modules"

# <out>/modules has the classes and resources of each module; <out>/home is the image without lib/modules, which is
# JAVA_HOME: conf/, and the data files in lib/. (A modules directory in JAVA_HOME would mean an exploded build.)
rm "$work/image/lib/modules"
rm -rf "$out"
mkdir -p "$out"
mv "$work/image" "$out/home"
mv "$work/modules" "$out/modules"
rm -rf "$work"

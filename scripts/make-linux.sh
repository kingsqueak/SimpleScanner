#!/bin/sh
# SPDX-License-Identifier: GPL-3.0-or-later
# SPDX-FileCopyrightText: 2026 KC2RGW Chris
# Build the Linux packages of SimpleScanner for this machine's architecture (x86-64 or arm64, the
# latter for a Raspberry Pi 4 or 5 with 64-bit Raspberry Pi OS). Self-contained: no .NET install is
# needed. The SDRplay API is not included: it comes from SDRplay's own installer (sdrplay.com/api).
# Run on Ubuntu or Debian; the .rpm and Arch packages need fpm (gem install fpm) and are skipped without it.
#
# NO_AMBE=1 leaves the AMBE speech decoders out (see the README).
# Writes dist/simplescanner_<version>-1_<arch>.deb, an .rpm and a .pkg.tar.zst
set -eu
cd "$(dirname "$0")/.."

VERSION=$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' src/SimpleScanner/SimpleScanner.csproj)
ARCH=$(dpkg --print-architecture)
case "$ARCH" in
  amd64) RID=linux-x64; LINUX_ARCH=x86_64 ;;
  arm64) RID=linux-arm64; LINUX_ARCH=aarch64 ;;
  *) echo "Unsupported architecture $ARCH" >&2; exit 1 ;;
esac
AMBE=true
[ -n "${NO_AMBE:-}" ] && AMBE=false

STAGE=$(mktemp -d)
trap 'rm -rf "$STAGE"' EXIT
APP="$STAGE/usr/lib/simplescanner"
mkdir -p "$STAGE/DEBIAN" "$APP" "$STAGE/usr/bin" "$STAGE/usr/share/applications" \
    "$STAGE/usr/share/icons/hicolor/512x512/apps" "$STAGE/usr/share/doc/simplescanner" dist

echo "== native libraries"
LIBS="audio digital $([ "$AMBE" = true ] && echo ambe)"
if [ -n "${NATIVE_DOCKER_IMAGE:-}" ]; then
  # Built in an older Linux (CI: Debian 12) so that they need no newer glibc than the systems the
  # package is for: built on a new Ubuntu they would not load on Debian 12 or 13 or Raspberry Pi OS.
  mkdir -p native/audio/out native/digital/out native/ambe/out
  docker run --rm -v "$PWD:/src" -w /src "$NATIVE_DOCKER_IMAGE" sh -c "
    apt-get update -qq && apt-get install -y -qq --no-install-recommends gcc libc6-dev > /dev/null &&
    for lib in $LIBS; do sh native/\$lib/build.sh > /dev/null; done &&
    chown -R $(id -u):$(id -g) native/audio/out native/digital/out native/ambe/out"
else
  for lib in $LIBS; do sh "native/$lib/build.sh" > /dev/null; done
fi
for lib in native/*/out/*.so; do
  echo "$lib needs glibc $(objdump -T "$lib" | grep -o 'GLIBC_[0-9.]*' | sort -uV | tail -1)"
done

echo "== app ($RID)"
# RadioReference's application key (see SimpleScanner.csproj): say whether this build carries it.
if [ -n "${RADIOREFERENCE_KEY:-}" ] || [ -s radioreference.key ]; then
  echo "RadioReference application key: built in"
else
  echo "${GITHUB_ACTIONS:+::warning::}No RadioReference application key (RADIOREFERENCE_KEY or radioreference.key): the RadioReference browser will not work in this build" >&2
fi
# DebugType=none: no debug record in the program, which would carry the build folder's path.
rm -rf "src/SimpleScanner/obj/Release/net10.0/$RID" "src/SimpleScanner/bin/Release/net10.0/$RID"
dotnet publish src/SimpleScanner/SimpleScanner.csproj -c Release -r "$RID" --self-contained true \
    -p:DebugType=none -p:DebugSymbols=false -p:IncludeAmbe="$AMBE" -o "$APP"
find "$APP" -name '*.pdb' -delete
for lib in libss_audio.so libss_digital.so $([ "$AMBE" = true ] && echo libss_ambe.so); do
  [ -f "$APP/$lib" ] || { echo "$lib is missing from the package" >&2; exit 1; }
done

install -m 0644 src/SimpleScanner/Assets/icon.png "$STAGE/usr/share/icons/hicolor/512x512/apps/simplescanner.png"
install -m 0644 THIRD-PARTY-NOTICES.txt "$STAGE/usr/share/doc/simplescanner/"

# Invariant globalization: nothing here is locale-sensitive, and it saves depending on a
# particular ICU version.
cat > "$STAGE/usr/bin/simplescanner" << 'EOF'
#!/bin/sh
export DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1
exec /usr/lib/simplescanner/SimpleScanner "$@"
EOF
chmod 0755 "$STAGE/usr/bin/simplescanner"

cat > "$STAGE/usr/share/applications/simplescanner.desktop" << 'EOF'
[Desktop Entry]
Type=Application
Name=SimpleScanner
GenericName=Communications scanner
Comment=A communications scanner for SDRplay RSP receivers
Exec=simplescanner
Icon=simplescanner
Terminal=false
Categories=HamRadio;Network;AudioVideo;
StartupWMClass=SimpleScanner
Keywords=scanner;sdr;sdrplay;rsp;p25;dmr;radio;
EOF

cat > "$STAGE/usr/share/doc/simplescanner/copyright" << 'EOF'
Format: https://www.debian.org/doc/packaging-manuals/copyright-format/1.0/
Upstream-Name: SimpleScanner
Source: https://github.com/kingsqueak/SimpleScanner

Files: *
Copyright: 2026 KC2RGW Chris
License: GPL-3+
 On Debian systems, the full text of the GNU General Public License
 version 3 can be found in /usr/share/common-licenses/GPL-3.

Files: usr/lib/simplescanner/libss_audio.so
Copyright: 2026 KC2RGW Chris; miniaudio: David Reid
License: GPL-3+ and (public-domain or MIT-0)

Files: usr/lib/simplescanner/libss_digital.so
Copyright: 2026 KC2RGW Chris; RadioFoundry: the RadioFoundry Authors; mbelib and DSD: 2010 their authors
License: GPL-3+ and MIT and ISC

Files: usr/lib/simplescanner/libss_ambe.so
Copyright: 2026 KC2RGW Chris; mbelib: 2010 mbelib Author
License: GPL-3+ and ISC
 The AMBE and AMBE+2 decoders may be covered by patents in some countries.

The .NET runtime, Avalonia, SkiaSharp, HarfBuzzSharp, CommunityToolkit.Mvvm,
Microsoft.Data.Sqlite, SQLitePCLRaw, SQLite and the Inter typeface are under their
own licences: see /usr/share/doc/simplescanner/THIRD-PARTY-NOTICES.txt.
EOF

# Anything a bundled native library cannot find on this system.
find "$APP" -name '*.so*' -exec ldd {} \; 2> /dev/null | grep 'not found' | sort -u || true

SIZE=$(du -sk "$STAGE" | cut -f1)
cat > "$STAGE/DEBIAN/control" << EOF
Package: simplescanner
Version: ${VERSION}-1
Section: hamradio
Priority: optional
Architecture: ${ARCH}
Installed-Size: ${SIZE}
Depends: libc6, libstdc++6, libgcc-s1, zlib1g, libssl3t64 | libssl3, libasound2t64 | libasound2, libx11-6, libice6, libsm6, libfontconfig1
Recommends: pulseaudio-utils | pipewire-bin | alsa-utils
Maintainer: KC2RGW Chris <2788495+kingsqueak@users.noreply.github.com>
Homepage: https://github.com/kingsqueak/SimpleScanner
Description: Communications scanner for SDRplay RSP receivers
 Scans stored channels in groups, searches ranges, decodes P25, DMR, NXDN,
 D-STAR and System Fusion voice, and runs several RSP radios at once.
 Needs the SDRplay API (sdrplay.com/api), installed separately.
EOF

dpkg-deb --root-owner-group -Zxz --build "$STAGE" "dist/simplescanner_${VERSION}-1_${ARCH}.deb"

if command -v fpm > /dev/null; then
  rm -rf "$STAGE/DEBIAN"
  fpm_common() {
    fpm -s dir -C "$STAGE" -n simplescanner -v "$VERSION" --iteration 1 -a "$LINUX_ARCH" \
        --license GPL-3.0-or-later --url https://github.com/kingsqueak/SimpleScanner \
        --maintainer "KC2RGW Chris <2788495+kingsqueak@users.noreply.github.com>" \
        --description "Communications scanner for SDRplay RSP receivers" "$@" usr
  }
  fpm_common -t rpm -p "dist/simplescanner-${VERSION}-1.${LINUX_ARCH}.rpm" \
      -d glibc -d libstdc++ -d libgcc -d zlib -d openssl-libs -d alsa-lib -d libX11 -d libICE -d libSM -d fontconfig
  fpm_common -t pacman -p "dist/simplescanner-${VERSION}-1-${LINUX_ARCH}.pkg.tar.zst" \
      -d glibc -d gcc-libs -d zlib -d openssl -d alsa-lib -d libx11 -d libice -d libsm -d fontconfig
fi
ls -la dist/

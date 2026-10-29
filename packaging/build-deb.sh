#!/usr/bin/env bash
# Builds siren-server_<version>_<arch>.deb with dpkg-deb. Layout:
#   /usr/lib/siren/              the `dotnet publish` output (self-contained, no dotnet dependency)
#   /usr/share/siren/web/        the web client
#   /usr/bin/siren               launcher for running the server by hand
#   /lib/systemd/system/siren.service
#   /etc/default/siren           conffile; SIREN_OPTS is appended to the service's command line
#   /usr/share/doc/siren-server/ README and copyright
# Nothing here overlaps a Jellyfin package (which uses /usr/lib/jellyfin, /etc/jellyfin, the
# `jellyfin` user and jellyfin.service), so both can be installed side by side; only the default
# port, 8096, is the same.
#
# Usage: build-deb.sh VERSION ARCH SERVER_PUBLISH_DIR WEB_DIST_DIR OUT_DIR [--stage-only]
# With --stage-only the package root is left in OUT_DIR/siren-server_<version>_<arch>-root and
# dpkg-deb is not called (for checking the layout on a machine without dpkg).
set -euo pipefail

if [ "$#" -lt 5 ] || [ "$#" -gt 6 ]; then
  echo "usage: $0 VERSION ARCH SERVER_PUBLISH_DIR WEB_DIST_DIR OUT_DIR [--stage-only]" >&2
  exit 2
fi

version=$1
arch=$2
server_dir=$3
web_dir=$4
out_dir=$5
mode=${6:-}

case "$arch" in
  amd64|arm64) ;;
  *) echo "build-deb.sh: ARCH must be amd64 or arm64, got $arch" >&2; exit 2 ;;
esac
if [ -n "$mode" ] && [ "$mode" != "--stage-only" ]; then
  echo "build-deb.sh: unknown option $mode" >&2
  exit 2
fi

here=$(cd "$(dirname "$0")" && pwd)
package="siren-server_${version}_${arch}"

mkdir -p "$out_dir"
out_dir=$(cd "$out_dir" && pwd)

if [ "$mode" = "--stage-only" ]; then
  root="$out_dir/$package-root"
  rm -rf "$root"
else
  work=$(mktemp -d)
  trap 'rm -rf "$work"' EXIT
  root="$work/root"
fi

# --- file tree --------------------------------------------------------------------------------

mkdir -p "$root/usr/lib" "$root/usr/share/siren" "$root/usr/bin" "$root/lib/systemd/system" \
  "$root/etc/default" "$root/usr/share/doc/siren-server" "$root/DEBIAN"

"$here/stage.sh" "$root/usr/lib/siren" "$server_dir" "$web_dir"
mv "$root/usr/lib/siren/web" "$root/usr/share/siren/web"
mv "$root/usr/lib/siren/README-package.md" "$root/usr/share/doc/siren-server/README.md"
mv "$root/usr/lib/siren/LICENSE" "$root/usr/share/doc/siren-server/LICENSE"

cat > "$root/usr/bin/siren" <<'EOF'
#!/bin/sh
# Runs Siren in the foreground with the packaged web client and the distribution's ffmpeg.
# A --webdir or --ffmpeg given on the command line replaces the default.
webdir="--webdir /usr/share/siren/web"
ffmpeg="--ffmpeg /usr/bin/ffmpeg"
for arg in "$@"; do
  case $arg in
    -w|--webdir|--webdir=*) webdir="" ;;
    --ffmpeg|--ffmpeg=*) ffmpeg="" ;;
  esac
done
# shellcheck disable=SC2086
exec /usr/lib/siren/jellyfin $webdir $ffmpeg "$@"
EOF
chmod 0755 "$root/usr/bin/siren"

cat > "$root/lib/systemd/system/siren.service" <<'EOF'
[Unit]
Description=Siren media server
After=network-online.target
Wants=network-online.target

[Service]
Type=simple
User=siren
Group=siren
EnvironmentFile=-/etc/default/siren
ExecStart=/usr/lib/siren/jellyfin --datadir /var/lib/siren --configdir /etc/siren --cachedir /var/cache/siren --logdir /var/log/siren --webdir /usr/share/siren/web --ffmpeg /usr/bin/ffmpeg $SIREN_OPTS
Restart=on-failure
RestartSec=5
TimeoutSec=90

NoNewPrivileges=true
ProtectSystem=strict
ReadWritePaths=/var/lib/siren /etc/siren /var/cache/siren /var/log/siren
PrivateTmp=true

[Install]
WantedBy=multi-user.target
EOF

cat > "$root/etc/default/siren" <<'EOF'
# Extra command-line options for siren.service, appended to the ExecStart line.
# Example: SIREN_OPTS="--nonetchange"
SIREN_OPTS=""
EOF

cat > "$root/usr/share/doc/siren-server/copyright" <<'EOF'
Siren is a fork of Jellyfin (https://jellyfin.org), copyright the Jellyfin contributors, with
changes copyright 6a8i. Both are distributed under the GNU General Public License version 2;
the full text is in LICENSE next to this file.
EOF

# --- control files ----------------------------------------------------------------------------

installed_size=$(du -sk --exclude=DEBIAN "$root" | cut -f1)

cat > "$root/DEBIAN/control" <<EOF
Package: siren-server
Version: $version
Architecture: $arch
Section: net
Priority: optional
Maintainer: 6a8i <gbizzon@hotmail.com>
Installed-Size: $installed_size
Depends: ffmpeg, libfontconfig1, libfreetype6, ca-certificates, adduser
Recommends: libva-drm2, libva2
Homepage: https://github.com/6a8i/siren
Description: Siren media server
 Siren is a media server derived from Jellyfin: it organizes video, music,
 photos and live TV and streams them to the bundled web client and to
 Jellyfin-compatible apps. This package ships a self-contained build of the
 server (no .NET runtime needed), the web client under /usr/share/siren/web
 and a systemd unit, siren.service, that runs as the siren user. Transcoding
 uses the distribution's ffmpeg.
EOF

printf '/etc/default/siren\n' > "$root/DEBIAN/conffiles"

cat > "$root/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e

case "$1" in
  configure)
    if ! getent group siren >/dev/null; then
      addgroup --quiet --system siren
    fi
    if ! getent passwd siren >/dev/null; then
      adduser --quiet --system --ingroup siren --home /var/lib/siren --no-create-home \
        --shell /usr/sbin/nologin --gecos "Siren media server" siren
    fi
    for dir in /var/lib/siren /etc/siren /var/cache/siren /var/log/siren; do
      mkdir -p "$dir"
      chown siren:siren "$dir"
      chmod 0750 "$dir"
    done
    if [ -d /run/systemd/system ]; then
      systemctl daemon-reload
      systemctl enable siren.service
      if [ -n "$2" ]; then
        # $2 is the previously installed version: this is an upgrade.
        systemctl restart siren.service
      else
        systemctl start siren.service
      fi
    fi
    ;;
esac
EOF

cat > "$root/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e

case "$1" in
  remove|deconfigure)
    if [ -d /run/systemd/system ]; then
      systemctl stop siren.service || true
      systemctl disable siren.service || true
    fi
    ;;
esac
EOF

cat > "$root/DEBIAN/postrm" <<'EOF'
#!/bin/sh
set -e

case "$1" in
  remove)
    # Data, configuration, cache and logs stay until the package is purged.
    if [ -d /run/systemd/system ]; then
      systemctl daemon-reload || true
    fi
    ;;
  purge)
    rm -rf /etc/siren /var/cache/siren /var/log/siren /var/lib/siren
    if getent passwd siren >/dev/null; then
      deluser --quiet --system siren || true
    fi
    if getent group siren >/dev/null; then
      delgroup --quiet --system siren || true
    fi
    if [ -d /run/systemd/system ]; then
      systemctl daemon-reload || true
    fi
    ;;
esac
EOF

chmod 0755 "$root/DEBIAN/postinst" "$root/DEBIAN/prerm" "$root/DEBIAN/postrm"
chmod 0644 "$root/DEBIAN/control" "$root/DEBIAN/conffiles"
find "$root" -type d -exec chmod 0755 {} +

if [ "$mode" = "--stage-only" ]; then
  echo "staged $root (dpkg-deb skipped)"
  exit 0
fi

dpkg-deb --build --root-owner-group "$root" "$out_dir/$package.deb"
echo "built $out_dir/$package.deb"

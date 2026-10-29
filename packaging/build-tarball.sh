#!/usr/bin/env bash
# Builds siren-server_<version>_linux-<arch>.tar.gz (server + web client + `siren` launcher) and,
# with --with-web, siren-web_<version>.tar.gz (the web client alone, for people who serve it from
# elsewhere).
#
# Usage: build-tarball.sh VERSION ARCH SERVER_PUBLISH_DIR WEB_DIST_DIR OUT_DIR [--with-web]
set -euo pipefail

if [ "$#" -lt 5 ] || [ "$#" -gt 6 ]; then
  echo "usage: $0 VERSION ARCH SERVER_PUBLISH_DIR WEB_DIST_DIR OUT_DIR [--with-web]" >&2
  exit 2
fi

version=$1
arch=$2
server_dir=$3
web_dir=$4
out_dir=$5
with_web=${6:-}

if [ -n "$with_web" ] && [ "$with_web" != "--with-web" ]; then
  echo "build-tarball.sh: unknown option $with_web" >&2
  exit 2
fi

here=$(cd "$(dirname "$0")" && pwd)
name="siren-server_${version}_linux-${arch}"
work=$(mktemp -d)
trap 'rm -rf "$work"' EXIT

mkdir -p "$out_dir"
out_dir=$(cd "$out_dir" && pwd)

"$here/stage.sh" "$work/$name" "$server_dir" "$web_dir"

cat > "$work/$name/siren" <<'EOF'
#!/bin/sh
# Starts Siren with the web client that ships next to it. Any server option can follow.
DIR=$(cd "$(dirname "$0")" && pwd)
exec "$DIR/jellyfin" --webdir "$DIR/web" "$@"
EOF
chmod 0755 "$work/$name/siren"

tar -C "$work" --owner=0 --group=0 --numeric-owner -czf "$out_dir/$name.tar.gz" "$name"
echo "built $out_dir/$name.tar.gz"

if [ -n "$with_web" ]; then
  mkdir -p "$work/siren-web"
  cp -R "$web_dir/." "$work/siren-web/"
  tar -C "$work" --owner=0 --group=0 --numeric-owner -czf "$out_dir/siren-web_${version}.tar.gz" siren-web
  echo "built $out_dir/siren-web_${version}.tar.gz"
fi

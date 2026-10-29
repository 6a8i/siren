#!/usr/bin/env bash
# Assembles the tree shared by every Linux package:
#   OUT_DIR/            the `dotnet publish` output (jellyfin executable, jellyfin.dll, runtime...)
#   OUT_DIR/web/        the web client build (webpack dist)
#   OUT_DIR/LICENSE
#   OUT_DIR/README-package.md
#
# Usage: stage.sh OUT_DIR SERVER_PUBLISH_DIR WEB_DIST_DIR
set -euo pipefail

if [ "$#" -ne 3 ]; then
  echo "usage: $0 OUT_DIR SERVER_PUBLISH_DIR WEB_DIST_DIR" >&2
  exit 2
fi

out_dir=$1
server_dir=$2
web_dir=$3
repo_root=$(cd "$(dirname "$0")/.." && pwd)

for required in "$server_dir/jellyfin" "$server_dir/jellyfin.dll" "$web_dir/index.html"; do
  if [ ! -f "$required" ]; then
    echo "stage.sh: missing $required" >&2
    exit 1
  fi
done

rm -rf "$out_dir"
mkdir -p "$out_dir/web"
cp -R "$server_dir/." "$out_dir/"
cp -R "$web_dir/." "$out_dir/web/"
cp "$repo_root/LICENSE" "$out_dir/LICENSE"

# Nothing from the build should carry debug symbols or the publish manifest.
find "$out_dir" -maxdepth 1 -name '*.pdb' -delete
chmod 0755 "$out_dir/jellyfin"
[ -f "$out_dir/createdump" ] && chmod 0755 "$out_dir/createdump"

cat > "$out_dir/README-package.md" <<'EOF'
# Siren

Servidor de mídia derivado do Jellyfin. O executável continua se chamando `jellyfin`; o script
`siren` ao lado dele (ou em `/usr/bin/siren` no pacote .deb) é a forma esperada de iniciá-lo,
porque já aponta para o web client embutido em `web/`.

## Tarball

    ./siren --datadir ~/siren

Dados, configuração, cache e logs ficam dentro de `--datadir` quando os outros caminhos não são
passados. Qualquer opção aceita pelo servidor pode vir depois (`--ffmpeg`, `--configdir`,
`--logdir`, `--cachedir`, `--nonetchange`, ...). Porta padrão: 8096 (a mesma do Jellyfin; não
rode os dois na mesma máquina sem mudar uma delas).

## Pacote .deb

    sudo apt install ./siren-server_*.deb
    sudo systemctl status siren

Serviço `siren.service`, usuário `siren`, dados em `/var/lib/siren`, configuração em
`/etc/siren`, logs em `/var/log/siren`, cache em `/var/cache/siren`. Opções extras vão em
`SIREN_OPTS` dentro de `/etc/default/siren`.

O ffmpeg vem do pacote da distribuição (`/usr/bin/ffmpeg`).

Licença: GPL-2.0 (ver `LICENSE`). Documentação completa em https://github.com/6a8i/siren.
EOF

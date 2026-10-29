# Releases e pacotes Linux

Como uma versão do Siren vira pacotes instaláveis, e como instalar, atualizar e remover cada um.
Tudo é feito pelo workflow `.github/workflows/release.yml` com os scripts de `packaging/`; nada
depende de infraestrutura fora do GitHub.

## Cortar uma release

```
git tag v13.0.1
git push origin v13.0.1
```

O push da tag dispara o workflow, que:

1. Compila o web client de `6a8i/siren-web`. Se o repositório do web client tiver uma tag com o
   **mesmo nome** (`v13.0.1`), é ela que entra; se não tiver, entra `main`. Para prender o web
   client a uma versão, crie a tag lá antes de criar a do servidor:
   `git -C ../jellyfin-web tag v13.0.1 && git -C ../jellyfin-web push origin v13.0.1`.
2. Publica o servidor self-contained (`dotnet publish --self-contained`, sem ReadyToRun) para
   `linux-x64` e `linux-arm64`, em dois jobs paralelos.
3. Monta os pacotes com `packaging/build-tarball.sh` e `packaging/build-deb.sh`.
4. Cria a GitHub Release `v13.0.1`, com notas geradas a partir dos commits desde a tag anterior,
   contendo:

| Arquivo | Conteúdo |
|---|---|
| `siren-server_13.0.1_amd64.deb` | Pacote Debian/Ubuntu, x86-64 |
| `siren-server_13.0.1_arm64.deb` | Pacote Debian/Ubuntu, ARM64 (Raspberry Pi 4/5 com SO de 64 bits etc.) |
| `siren-server_13.0.1_linux-amd64.tar.gz` | Servidor + web client, sem instalador, x86-64 |
| `siren-server_13.0.1_linux-arm64.tar.gz` | O mesmo para ARM64 |
| `siren-web_13.0.1.tar.gz` | Só o web client (a pasta `dist` do webpack) |
| `SHA256SUMS` | Hashes de todos os arquivos acima; `sha256sum -c SHA256SUMS` confere o que foi baixado |

A versão dos arquivos vem da tag (sem o `v`). A versão do assembly (`SharedVersion.cs`, `13.0.0`)
não muda com a tag: é o que os clientes veem como versão do "Jellyfin Server" e fica fixa de
propósito (ver `docs/upstream.md`).

### Ensaio sem publicar

```
gh workflow run release.yml -f publish=false
```

Roda tudo a partir de `master` (ou `--ref <branch>`), com versão `0.0.0-dev.<número do run>`, e
deixa os pacotes só como artefatos do run (`siren-packages-amd64`, `siren-packages-arm64`,
`siren-web-dist`), sem criar release. `-f web_ref=<branch>` escolhe outro ramo do web client.
Com `-f publish=true` o run precisa ter sido iniciado em uma tag existente
(`--ref v13.0.1`); em qualquer outro ref o job de release falha de propósito antes de criar
qualquer coisa.

## Instalar o .deb

```
sudo apt install ./siren-server_13.0.1_amd64.deb
```

O `apt` resolve as dependências (`ffmpeg`, `libfontconfig1`, `libfreetype6`, `ca-certificates`,
`adduser`; recomendados `libva2` e `libva-drm2` para aceleração por VA-API). O pacote não precisa
de .NET instalado: o runtime vai dentro dele.

O `postinst` cria o usuário e grupo de sistema `siren` (home `/var/lib/siren`, sem shell), cria os
diretórios abaixo com dono `siren:siren` e modo `0750`, e habilita e inicia `siren.service`.

| Caminho | Uso |
|---|---|
| `/usr/lib/siren/` | Servidor (`jellyfin`, `jellyfin.dll`, runtime) |
| `/usr/share/siren/web/` | Web client |
| `/usr/bin/siren` | Lançador para rodar à mão (`siren --datadir /tmp/x`) |
| `/lib/systemd/system/siren.service` | Unidade do systemd |
| `/etc/default/siren` | `SIREN_OPTS="..."`, opções extras passadas ao serviço (conffile, preservado em atualizações) |
| `/var/lib/siren` | Dados (banco, metadados, imagens) |
| `/etc/siren` | Configuração (`system.xml`, `network.xml`, ...) |
| `/var/cache/siren` | Cache |
| `/var/log/siren` | Logs |

```
sudo systemctl status siren
sudo systemctl restart siren
sudo journalctl -u siren -f
```

Porta padrão: **8096** (HTTP), a mesma do Jellyfin. Os caminhos, o nome do serviço e o usuário
são todos distintos dos do pacote `jellyfin`, então os dois podem conviver na mesma máquina, mas
um deles precisa mudar de porta (`network.xml` ou a tela de rede do painel).

O serviço roda com `ProtectSystem=strict`: só os quatro diretórios da tabela são graváveis.
Bibliotecas de mídia em outros lugares são lidas normalmente; se o servidor precisar **escrever**
fora deles (metadados ao lado dos arquivos, por exemplo), acrescente um drop-in:

```
sudo systemctl edit siren
# [Service]
# ReadWritePaths=/srv/media
```

### ffmpeg e aceleração de hardware

O serviço usa `/usr/bin/ffmpeg` da distribuição. Aceleração por VA-API (Intel/AMD) ou NVDEC
depende dos pacotes de driver da distribuição e de o usuário `siren` poder abrir `/dev/dri/*`:

```
sudo usermod -aG render,video siren
sudo systemctl restart siren
```

Para outro ffmpeg (um build estático com mais codecs, por exemplo) troque o caminho no painel
(Reprodução > Transcodificação) ou ponha `SIREN_OPTS="--ffmpeg /opt/ffmpeg/bin/ffmpeg"` em
`/etc/default/siren`; a opção da linha de comando tem precedência sobre a do painel.

### Atualizar

```
sudo apt install ./siren-server_13.0.2_amd64.deb
```

O `postinst` detecta a atualização e reinicia o serviço. Dados, configuração e
`/etc/default/siren` ficam como estavam.

### Remover

```
sudo apt remove siren-server    # para o serviço e tira os arquivos; mantém dados, config, cache, logs e o usuário
sudo apt purge siren-server     # além disso apaga /etc/siren, /var/cache/siren, /var/log/siren, /var/lib/siren e o usuário siren
```

Faça um backup (`docs/operations.md`, seção "Backup agendado") antes de um `purge`.

## Usar o tarball

Para quem não usa Debian/Ubuntu, não quer systemd, ou quer várias versões lado a lado:

```
tar -xzf siren-server_13.0.1_linux-amd64.tar.gz
cd siren-server_13.0.1_linux-amd64
./siren --datadir ~/siren
```

`siren` é um script de três linhas que chama `./jellyfin --webdir ./web` com o que vier depois.
Sem `--configdir`, `--cachedir` e `--logdir`, tudo fica dentro de `--datadir`. O ffmpeg é
procurado no `PATH`; `--ffmpeg /caminho` força um específico. Um `README-package.md` com estas
notas vai dentro do tarball.

Atualizar é extrair a versão nova em outra pasta e apontar para o mesmo `--datadir`; remover é
apagar a pasta (e o `--datadir`, se não quiser os dados).

## Só o web client

`siren-web_13.0.1.tar.gz` contém a pasta `siren-web/` com o build do web client, para quem o serve
por um nginx/Caddy na frente do servidor ou por outro host. O servidor então roda com
`--nowebclient` (ou continua servindo o dele; os dois não conflitam). O build é o mesmo que vai
embutido nos outros pacotes, então a versão do web client de uma release é sempre uma só.

## O que não é validado antes do primeiro run

Os scripts de `packaging/` são bash e rodam em qualquer Linux; `build-deb.sh --stage-only` monta a
árvore do pacote sem chamar `dpkg-deb`, o que serve para conferir o layout em uma máquina sem
`dpkg`. A instalação de fato (postinst, unidade do systemd, dependências) só é provada instalando
o `.deb` gerado pelo workflow em uma VM Debian/Ubuntu.

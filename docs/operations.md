# Operação

O que o servidor faz por conta própria para se proteger e se manter, e onde cada comportamento é
ligado ou desligado. Os caminhos de configuração são os do Jellyfin (`config/system.xml`,
`config/network.xml`); os clientes continuam vendo um "Jellyfin Server".

## Cabeçalhos de segurança

Toda resposta do servidor (API, web client, arquivos estáticos, erros, respostas sem corpo) sai com:

| Cabeçalho | Valor | Efeito |
|---|---|---|
| `X-Content-Type-Options` | `nosniff` | O navegador não adivinha o tipo de um arquivo; usa o `Content-Type` enviado. |
| `Referrer-Policy` | `strict-origin-when-cross-origin` | Links para outros sites levam só a origem, não a URL completa (que carrega ids de itens). |
| `X-Frame-Options` | `SAMEORIGIN` | A página só pode ser embutida em `<iframe>` pela própria origem (navegadores antigos). |
| `Content-Security-Policy` | `frame-ancestors 'self'` | O mesmo, para navegadores que honram CSP. |
| `Content-Security-Policy-Report-Only` | a política completa (abaixo) | O navegador avisa o que a política bloquearia, sem bloquear nada. |
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=()` | O web client não usa nada disso; fica negado para a página e para qualquer frame dentro dela. |

**Desligar:** `EnableSecurityHeaders` em `config/system.xml` (`ServerConfiguration`), padrão
`true`. Quem embute o web client em um `<iframe>` de outra origem precisa pôr `false`, porque os
dois cabeçalhos de frame bloqueiam exatamente isso. Também pode ser alterado por
`POST /System/Configuration` como administrador; a mudança vale na requisição seguinte, sem
reiniciar. Com `false` nenhum cabeçalho desta seção sai, nem a CSP. Implementação:
`Jellyfin.Api/Middleware/SecurityHeadersMiddleware.cs`, registrado em `Jellyfin.Server/Startup.cs`
antes dos arquivos estáticos e do roteamento.

### Content-Security-Policy completa

`ContentSecurityPolicyMode` em `config/system.xml` (`ServerConfiguration`) escolhe como a política
completa é enviada; vale na requisição seguinte, sem reiniciar:

| Valor | Cabeçalho | Efeito |
|---|---|---|
| `Off` | só `Content-Security-Policy: frame-ancestors 'self'` | O que havia antes da política existir. |
| `ReportOnly` (padrão) | `Content-Security-Policy-Report-Only` com a política completa, mais o `frame-ancestors` de sempre no cabeçalho que bloqueia | O navegador manda um relatório para cada recurso que a política bloquearia e carrega tudo normalmente. |
| `Enforce` | `Content-Security-Policy` com a política completa | O navegador bloqueia o que a política não permite. |

A política é uma só, montada uma vez por processo (o `report-uri` recebe o `BaseUrl` na frente):

```
default-src 'self'; script-src 'self' 'unsafe-inline' 'unsafe-eval' blob:; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob: https:; media-src 'self' blob: data:; font-src 'self' data:; connect-src 'self' ws: wss: https:; worker-src 'self' blob:; child-src 'self' blob:; frame-src 'self' https:; frame-ancestors 'self'; base-uri 'self'; form-action 'self'; report-uri /System/CspReport
```

Cada permissão existe por algo que o web client ou seus plugins fazem hoje (o comentário em
`SecurityHeadersMiddleware.cs` lista uma a uma): scripts inline no `index.html` e nas páginas de
configuração de plugins, `eval` em bibliotecas empacotadas, workers em `blob:` (libass, leitor de
PDF), estilos inline do layout e dos temas, imagens de provedores de metadados servidas direto do
provedor (`https:`), reprodução por `blob:` (HLS, MediaSource), fontes de ícones inline, websocket
da sessão (`ws:`/`wss:`) e frames `https:` de plugins e trailers. O que a política de fato proíbe:
scripts e estilos de outras origens, imagens em `http:` de outros hosts, `<base>` e envio de
formulário para fora, e embutir a página em outra origem.

**Relatórios.** O navegador envia cada violação em `POST /System/CspReport` (anônimo, sem contar
no limite de autenticação; aceita `application/csp-report` e `application/reports+json`, responde
204). O servidor grava uma linha de aviso por violação distinta:

```
CSP violation (report) on https://media.exemplo/web/: img-src blocked http://cdn.exemplo/poster.jpg, source https://media.exemplo/web/main.js:12, client 203.0.113.7
```

- `(report)` veio do cabeçalho report-only, `(enforce)` do cabeçalho que bloqueia.
- `img-src blocked ...` é a diretiva violada e o recurso; `eval` ou `inline` aparecem no lugar
  da URL quando o bloqueado é código, não um download.
- `source ...:linha` é o script que pediu o recurso, quando o navegador sabe; `-` quando não.
- Os campos são cortados em 512 caracteres e perdem caracteres de controle, então um relatório
  forjado não consegue inserir linhas no log.

Para o log não virar lixo com uma aba aberta em uma página quebrada, só as **20 primeiras
violações distintas por minuto** (documento, diretiva, recurso e origem) são gravadas; repetições
e o excedente são contados. Ao atingir o limite sai uma linha `CSP report limit reached ...` e, no
primeiro relatório do minuto seguinte, uma linha `N further CSP violation reports in the previous
window ... were not logged`. Corpos acima de 64 KiB recebem 413.

**Passar para `Enforce`:**

1. Deixe `ReportOnly` por alguns dias de uso normal, com todos os clientes e plugins que importam
   (dashboard, reprodução com legendas, páginas de configuração de plugins, Live TV).
2. Procure `CSP violation` no log. Cada linha é algo que `Enforce` quebraria: um plugin que carrega
   script de CDN, um tema com imagens em `http:`, um canal que toca mídia de outro host
   (`media-src`). Ajuste o plugin, proxie o recurso pelo servidor ou aceite perder aquilo.
3. Com o log limpo, `ContentSecurityPolicyMode=Enforce` (XML ou `POST /System/Configuration`).
   Se algo quebrar, volte para `ReportOnly`: a mudança vale na requisição seguinte.

A política em si não tem chave de configuração: mudar uma diretiva exige recompilar, e é melhor
assim, porque cada permissão a mais precisa de uma justificativa no comentário do middleware.

## Limite de tentativas de autenticação

Os endpoints que recebem senha, PIN ou segredo do Quick Connect têm a política de limitação
`authentication`:

- `POST /Users/AuthenticateByName`
- `POST /Users/{userId}/Authenticate` (obsoleto, mas ainda servido)
- `POST /Users/AuthenticateWithQuickConnect`
- `POST /Users/ForgotPassword` e `POST /Users/ForgotPassword/Pin`
- `POST /Families/{familyId}/Members/{userId}/Switch` (troca de perfil com PIN)

A regra é **N tentativas por janela fixa de 60 segundos, por endereço IP do cliente**, sem fila,
com N em `AuthenticationRateLimitPerMinute` (`config/system.xml`, `ServerConfiguration`), padrão
**10**; `0` desliga o limite. A tentativa seguinte à N-ésima na mesma janela recebe
`429 Too Many Requests` com `Retry-After` em segundos e gera uma linha de aviso no log:

```
Rate limit reached for /Users/AuthenticateByName by 203.0.113.7, rejecting with 429 for 42s
```

O contador vive em memória e zera ao reiniciar; o limite conta tentativas, não falhas, então dez
logins certos seguidos também disparam (nenhum cliente faz isso). A janela de 60 s é fixa
(`Jellyfin.Api/Constants/AuthenticationRateLimit.cs`); só a quantidade é configurável.

A mudança vale sem reiniciar, mas por endereço: o valor é lido quando um IP aparece pela
primeira vez ou volta depois de uma janela inteira sem tentativas (o limitador dele é descartado
ao ficar ocioso). Um IP com tentativas na janela corrente mantém o limite com que começou até a
janela zerar, no máximo 60 s. O mesmo vale para desligar com `0`.

Pontos de atenção:

- O endereço é `RemoteIpAddress` depois do middleware de `X-Forwarded-For`. Atrás de um proxy
  reverso, cadastre o proxy em `KnownProxies` (`config/network.xml`); sem isso todos os clientes
  chegam com o IP do proxy e dividem as N tentativas por minuto.
- IPv6 conta por endereço, não por prefixo `/64`.
- Quando a conexão não tem endereço (o `TestServer` dos testes de integração) nada é limitado.
- `POST /System/CspReport` não entra na política: é anônimo e um navegador o chama sozinho.

## Backup agendado

A tarefa **Fazer backup do sistema** (chave `SystemBackup`, categoria Manutenção) roda por padrão
aos **domingos às 03:00** e pode ser reagendada ou disparada no painel como qualquer tarefa.

- **Conteúdo:** banco de dados e configuração. É o que o serviço de backup sempre inclui
  (`system.xml` e demais XML/JSON de `config/`, `config/users`, as tarefas agendadas, a pasta
  `root`, coleções e playlists) mais o dump do banco. Metadados, trickplay e legendas ficam de
  fora: são regeneráveis e transformariam alguns MB em muitos GB por semana. Um backup completo
  continua disponível a mão por `POST /Backup/Create` com as opções desejadas.
- **Arquivos:** `<dados>/backups/jellyfin-backup-scheduled-<AAAAMMDDhhmmss>.zip`. O serviço gera o
  arquivo como `jellyfin-backup-<carimbo>.zip` e a tarefa o renomeia com o prefixo `scheduled`;
  é só pelo nome que a retenção reconhece os seus.
- **Retenção:** ao fim de cada execução ficam os **5 arquivos agendados mais recentes** (pela data
  do manifesto); os demais são apagados. Backups feitos pela API ou pela interface
  (`jellyfin-backup-<carimbo>.zip`) nunca são tocados, nem arquivos com o prefixo que não sejam
  backups válidos.
- **Quando não roda:** se há um scan de biblioteca em andamento (o serviço se recusa a ler o
  banco nesse estado) ou se uma restauração está agendada para o próximo boot; nos dois casos a
  tarefa registra o motivo e espera o próximo gatilho. Como o serviço, exige 5 GiB livres na
  pasta de backups.
- **Restaurar:** `POST /Backup/Restore` com `ArchiveFileName` igual ao nome do arquivo (o prefixo
  não muda nada) ou `--restore-archive <caminho>` na linha de comando. O servidor reinicia e
  aplica o arquivo antes de subir.

## Carga de plugins

Na inicialização o servidor lê os metadados de cada `.dll` da pasta de cada plugin ativo (ou só
das listadas em `assemblies` no `meta.json`, quando há lista) sem carregá-la, e separa:

- **Assembly do plugin:** referencia `MediaBrowser.*`, `Jellyfin.*` ou `Emby.*`. É carregada na
  hora, tem todos os tipos percorridos uma vez (é essa passagem que detecta um plugin compilado
  contra uma versão incompatível e o marca como `NotSupported`) e é onde o servidor procura as
  partes do plugin. Log: `Loaded assembly <nome> from <caminho>`.
- **Biblioteca do plugin:** assembly .NET sem referência ao servidor (Newtonsoft, HtmlAgilityPack,
  cópias de bibliotecas do framework). Fica registrada por nome e é carregada na primeira
  referência, dentro do contexto do plugin; a cópia do plugin vence a do servidor, como sempre foi.
- **Outros arquivos:** bibliotecas nativas e arquivos que não são assembly são ignorados (linha
  em nível Debug), sem desativar o plugin.

Um plugin cujas DLLs não referenciam o servidor carrega todas elas, como antes, com o aviso
`No assembly of plugin <nome> references the server; loading all N of them`. Um plugin sem nenhuma
DLL .NET é marcado `Malfunctioned` (`None of the files of plugin <nome> is a .NET assembly`).

## Métricas

`EnableMetrics` em `config/system.xml` (padrão `false`) expõe `/metrics` (abaixo do `BaseUrl`) no
formato Prometheus, via prometheus-net: contadores e histogramas por rota HTTP
(`UseHttpMetrics`) e estatísticas do runtime .NET (GC, thread pool, JIT, contenção), coletadas
desde o boot. A mudança só vale depois de reiniciar: o pipeline HTTP é montado na inicialização.

O endpoint **não exige autenticação**. Só a validação de acesso por IP (`config/network.xml`)
se aplica, então quem liga as métricas em um servidor exposto deve restringir `/metrics` no
proxy ou no firewall.

## Política de VACUUM do SQLite

Implementada em `src/Jellyfin.Database/Jellyfin.Database.Providers.Sqlite/SqliteDatabaseProvider.cs`.
O `VACUUM` reescreve o arquivo inteiro sob lock exclusivo, então ele só acontece quando compensa:

- **Otimização agendada** (`RunScheduledOptimisation`): executada pela tarefa **Otimizar base de
  dados** a cada 6 horas e antes de cada backup. Faz `PRAGMA wal_checkpoint(TRUNCATE)`, mede
  `freelist_count` e `page_count` e só roda `VACUUM` quando a lista livre tem **ao menos 2560
  páginas (cerca de 10 MiB no tamanho de página padrão) e ao menos 10% das páginas**. A decisão
  fica no log:

  ```
  jellyfin.db has 3120 free pages of 24800 (12%), vacuuming
  jellyfin.db has 120 free pages of 24800 (0%), skipping vacuum
  ```

  Em seguida roda `ANALYZE` com `analysis_limit=1000` (estatísticas aproximadas, as que o SQLite
  recomenda para rotina), mas só se a biblioteca já tem itens: estatísticas colhidas com o banco
  vazio fazem o planejador tratar toda tabela como uma linha. Termina com outro checkpoint.
- **Depois de migrações** (`RunPostMigrationOptimisation`): nunca faz `VACUUM`, para não segurar o
  boot; roda `ANALYZE` completo (`analysis_limit=0`), porque o esquema mudou.
- **No desligamento** (`RunShutdownTask`): só o checkpoint, esvaziando o pool de conexões antes
  para que a truncagem do WAL consiga acontecer. O que não couber fica para o próximo boot.
- A tarefa de otimização é pulada enquanto um scan está rodando, para não disputar o banco.
- O `VACUUM` usa `temp_store=1` (arquivo): o espaço de rascunho vai para `SQLITE_TMPDIR` (ou o
  diretório temporário padrão do SQLite) e precisa de espaço livre da ordem do tamanho do banco.

Para forçar uma otimização, dispare a tarefa no painel; ela aplica as mesmas regras, ou seja, um
banco pouco fragmentado continua sem `VACUUM`.

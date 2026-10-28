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
| `Permissions-Policy` | `camera=(), microphone=(), geolocation=()` | O web client não usa nada disso; fica negado para a página e para qualquer frame dentro dela. |

Não há uma CSP completa (`script-src`, `connect-src`...): ela exigiria acompanhar cada
dependência do web client e de plugins e quebraria sem aviso.

**Desligar:** `EnableSecurityHeaders` em `config/system.xml` (`ServerConfiguration`), padrão
`true`. Quem embute o web client em um `<iframe>` de outra origem precisa pôr `false`, porque os
dois cabeçalhos de frame bloqueiam exatamente isso. Também pode ser alterado por
`POST /System/Configuration` como administrador; a mudança vale na requisição seguinte, sem
reiniciar. Implementação: `Jellyfin.Api/Middleware/SecurityHeadersMiddleware.cs`, registrado em
`Jellyfin.Server/Startup.cs` antes dos arquivos estáticos e do roteamento.

## Limite de tentativas de autenticação

Os endpoints que recebem senha, PIN ou segredo do Quick Connect têm a política de limitação
`authentication`:

- `POST /Users/AuthenticateByName`
- `POST /Users/{userId}/Authenticate` (obsoleto, mas ainda servido)
- `POST /Users/AuthenticateWithQuickConnect`
- `POST /Users/ForgotPassword` e `POST /Users/ForgotPassword/Pin`
- `POST /Families/{familyId}/Members/{userId}/Switch` (troca de perfil com PIN)

A regra é **10 tentativas por janela fixa de 60 segundos, por endereço IP do cliente**, sem fila.
A décima primeira na mesma janela recebe `429 Too Many Requests` com `Retry-After` em segundos e
gera uma linha de aviso no log:

```
Rate limit reached for /Users/AuthenticateByName by 203.0.113.7, rejecting with 429 for 42s
```

O contador vive em memória e zera ao reiniciar; o limite conta tentativas, não falhas, então dez
logins certos seguidos também disparam (nenhum cliente faz isso). Os números estão em
`Jellyfin.Api/Constants/AuthenticationRateLimit.cs`, de propósito sem chave de configuração: mudar
exige recompilar.

Pontos de atenção:

- O endereço é `RemoteIpAddress` depois do middleware de `X-Forwarded-For`. Atrás de um proxy
  reverso, cadastre o proxy em `KnownProxies` (`config/network.xml`); sem isso todos os clientes
  chegam com o IP do proxy e dividem as 10 tentativas por minuto.
- IPv6 conta por endereço, não por prefixo `/64`.
- Quando a conexão não tem endereço (o `TestServer` dos testes de integração) nada é limitado.

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

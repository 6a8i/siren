# Siren: roteiro de performance

Levantamento feito em 2026-10-27 por leitura de código, cobrindo inicialização, banco de dados,
API/HTTP, scan de biblioteca, imagens e transcodificação. Nada foi medido em execução: a máquina
de análise não tem ffmpeg e o servidor aborta na validação do encoder. As estimativas de impacto
são por análise estática e devem ser confirmadas com medição antes e depois de cada mudança.

Baseline conhecida: saída de build Release com 716 MB e 102 DLLs no diretório de saída.

Objetivo: servidor leve e rápido para uso pessoal. Prioridade para mudanças de baixo risco e
alto retorno, mantendo compatibilidade com os clientes Jellyfin existentes.

---

## Onda 1: ganhos rápidos, baixo risco

Cada item é uma mudança pequena e isolada. Juntos, reduzem latência por requisição, pausas
e trabalho desnecessário em background.

### Banco de dados (SQLite + EF Core)

| # | Problema | Onde | Mudança |
|---|---|---|---|
| 1 | `DISTINCT` sobre a linha inteira de BaseItems (inclui o JSON `Data`) em GetItems/GetItemList. Impede `ORDER BY ... LIMIT` de usar índice. | `Jellyfin.Server.Implementations/Item/BaseItemRepository.QueryBuilding.cs:119` | Remover o `Distinct()`; os filtros só usam `Where`/`Any`, não geram duplicatas. |
| 2 | Consultas de item usam `AsSingleQuery` com Include de Provider, LockedFields, UserData, Images, LinkedChildren, TrailerTypes: explosão cartesiana de linhas, cada uma repetindo o `Data`. | `BaseItemRepository.QueryBuilding.cs:30`, `:250-303`; `Querying.cs:568-574` | Trocar para `AsSplitQuery()` e adicionar `Id` como último critério de ordenação. Reativar o aviso `MultipleCollectionIncludeWarning` em `SqliteDatabaseProvider.cs:86`. |
| 3 | UserData de todos os usuários é carregado por item e filtrado em memória. | `QueryBuilding.cs:267-270`; `UserDataManager.cs:211,372` | Include filtrado por `filter.User.Id`. |
| 4 | `cache_size` padrão do SQLite (2 MB) e sem `mmap_size`. | `SqliteDatabaseProvider.cs:90`; `PragmaConnectionInterceptor.cs:85-109` | Padrão `cache_size=-65536` e `mmap_size=268435456`, ainda sobrescrevíveis por `#PRAGMA:`. |
| 5 | Lote de PRAGMAs executado em toda abertura lógica de conexão, ou seja, a cada consulta. | `PragmaConnectionInterceptor.cs:51-77` | Executar uma vez por conexão física (marcar o `SqliteConnection.Handle`). |
| 6 | Deleção de item faz anti-join sobre toda a tabela ItemValues. | `ItemPersistenceService.cs:149` | Limitar a varredura aos ItemValueIds dos itens deletados, como já é feito para Peoples em `:154`. |
| 7 | `TypeRequiresDeserialization` chama `GetCustomAttribute` por item, sem cache. | `BaseItemMapper.cs:266` | Cache por `Type` em `ConcurrentDictionary`. |

### Inicialização e tarefas em background

| # | Problema | Onde | Mudança |
|---|---|---|---|
| 8 | `GC.Collect()` forçado ao fim de toda tarefa agendada, inclusive as que não fizeram nada. Pausa gen2 bloqueante que trava threads de streaming. | `Emby.Server.Implementations/ScheduledTasks/ScheduledTaskWorker.cs:297` | Remover. Se necessário, `GC.Collect(2, Optimized, blocking: false)` só após o scan de biblioteca. |
| 9 | `VACUUM` completo e `ANALYZE` sem limite a cada 6 horas. Reescreve o arquivo inteiro com lock exclusivo. | `SqliteDatabaseProvider.cs:179-240`; `OptimizeDatabaseTask.cs:69` | A cada 6 h apenas `PRAGMA optimize`. VACUUM semanal ou quando `freelist_count/page_count > 10%`. |
| 10 | VACUUM também roda no boot após qualquer migração, inclusive as que só editam XML. | `Jellyfin.Server/Program.cs:219,225,330,348-371` | Só marcar a flag quando uma migração de esquema EF rodou, e mesmo assim deixar o VACUUM para a tarefa agendada. |
| 11 | Fila de log de startup e sink extra do Serilog vivem pelo processo inteiro; a fila cresce sem limite. | `StartupHelpers.cs:261-268`; `SetupServer.cs:74,316-342` | Anular `LogQueue` após `configurationCompleted` ou usar um ring buffer de ~500 entradas. |
| 12 | Console sink síncrono e enrichers `FromLogContext`/`WithThreadId` configurados duas vezes. | `Resources/Configuration/logging.json:14-18,39`; `StartupHelpers.cs:266-267` | Envolver o Console em `Async` e remover os enrichers duplicados do código. Precisa de migração para instalações existentes. |
| 13 | Duas tarefas disparam 3 s após todo boot: atualização de plugins (HTTP para todos os repositórios) e limpeza do diretório de transcodificação (enumeração recursiva). | `StartupTrigger.cs:13,35`; `PluginUpdateTask.cs:64`; `DeleteTranscodeFileTask.cs:70` | Atrasar o gatilho para 2 a 5 min; pular a atualização de plugins se o último sucesso foi há menos de 24 h. |
| 14 | Em instalação nova todas as tarefas de intervalo disparam juntas em T+1h, sem limite global de concorrência. | `IntervalTrigger.cs:47`; `TaskManager.cs:157-173` | Jitter determinístico por tarefa e um `SemaphoreSlim` para as tarefas pesadas de ffmpeg. |

### API e HTTP

| # | Problema | Onde | Mudança |
|---|---|---|---|
| 15 | Compressão de resposta desligada para HTTPS (`EnableForHttps` padrão false). Listas de itens com centenas de KB vão sem compressão. | `Jellyfin.Server/Startup.cs:66` | `EnableForHttps = true`, Brotli/Gzip em `CompressionLevel.Fastest`. |
| 16 | Arquivos estáticos do web client sem `Cache-Control` (só `index.html` recebe `no-cache`) e recomprimidos a cada download. | `Startup.cs:214-226,191` | `public, max-age=31536000, immutable` para bundles com hash no nome; `no-cache` para `index.html`, `config.json`, service worker e manifest. Servir `.br`/`.gz` pré-comprimidos. |
| 17 | Endpoint de imagem processa a imagem antes de checar `If-None-Match`; um cache hit do cliente ainda custa stats e hash, um miss custa o encode inteiro para devolver 304. | `Jellyfin.Api/Controllers/ImageController.cs:2006,2042-2066` | Comparar o ETag antes de `ProcessImage`. Adicionar `max-age` quando não houver tag. |
| 18 | Validação de IP roda depois da autenticação; IP bloqueado ainda paga lookup de token e carga de usuário. | `Startup.cs:232-238` | Mover para logo após `UseForwardedHeaders`. |
| 19 | Tick automático de progresso de playback a cada 1 s por sessão faz carga de usuário no banco e publica evento, sem salvar nada. | `MediaBrowser.Controller/Session/SessionInfo.cs:397`; `SessionManager.cs:591,903` | Nos ticks automáticos usar os IDs já na sessão e pular `PublishAsync` sem consumidores. Avaliar intervalo de 5 a 10 s. |
| 20 | Toda busca de sessão por ID copia e ordena todas as sessões. | `SessionManager.cs:158,1226-1241` | Índice `ConcurrentDictionary<string, SessionInfo>` ao lado de `_activeConnections`. |
| 21 | `GetSessionInfo` aloca uma `SessionInfo` nova (MD5, DTO de device, cache tag) mesmo quando a sessão já existe. | `SessionManager.cs:507-508` | `TryGetValue` antes, ou `GetOrAdd` com factory. |
| 22 | Websocket: broadcasts serializam a mesma mensagem uma vez por conexão, e `SendAsync` não tem lock de envio (envios concorrentes quebram o socket). | `WebSocketConnection.cs:104,111`; `SessionManager.cs:1336-1351` | Serializar uma vez por broadcast e adicionar `SemaphoreSlim(1)` por conexão. Inclui uma correção de robustez. |
| 23 | Conversores JSON de Guid e DateTime alocam string por valor. | `Jellyfin.Extensions/Json/Converters/JsonGuidConverter.cs:28`; `JsonDateTimeConverter.cs:26` | `TryFormat` em `stackalloc` + `WriteStringValue(span)`. |

### Scan e mídia

| # | Problema | Onde | Mudança |
|---|---|---|---|
| 24 | Relatório de progresso do scan recalcula a média de todos os itens a cada atualização: custo quadrático no tamanho da pasta. | `MediaBrowser.Controller/LibraryTaskScheduler/LimitedConcurrencyLibraryScheduler.cs:295-297` | Soma corrente com `Interlocked` e throttle de ~1% ou 250 ms. |
| 25 | `LinkTarget` (readlink) consultado para todo arquivo enumerado. | `Emby.Server.Implementations/IO/ManagedFileSystem.cs:260,644-647` | Só ler quando `Attributes` tiver `ReparsePoint`. |
| 26 | Decode do Skia copia o arquivo inteiro para temp em caminhos não-ASCII e nunca apaga a cópia. | `src/Jellyfin.Drawing.Skia/SkiaEncoder.cs:324-337,368,401` | Decodificar via `FileStream`/`SKManagedStream`, ou ao menos apagar em `finally`. |
| 27 | Requisições concorrentes para a mesma imagem não cacheada encodam todas, e o arquivo é escrito no destino final (leitores veem arquivo parcial). | `src/Jellyfin.Drawing/ImageProcessor.cs:197-205`; `SkiaEncoder.cs:718` | `AsyncKeyedLocker<string>` por caminho de cache, rechecar existência dentro do lock, escrever em temp e `File.Move`. |
| 28 | Cada DTO de Audio dispara uma consulta de media streams só para `HasLyrics`. 500 faixas = 500 consultas. | `Emby.Server.Implementations/Dto/DtoService.cs:461` | Consulta em lote para todos os IDs do page, ou flag persistida no item. |
| 29 | Chapter images, segmentos de mídia e normalização de áudio carregam a biblioteca inteira em memória em toda execução. | `ChapterImagesTask.cs:85,133,137,152`; `MediaSegmentExtractionTask.cs:68,82-93`; `AudioNormalizationTask.cs:89,169,230` | Filtrar em SQL (só o que falta processar), paginar por keyset, `HashSet` para falhas, sair cedo quando não há providers. |
| 30 | ffprobe sem semáforo nem timeout; um NAS travado bloqueia um worker do scan indefinidamente. | `MediaBrowser.MediaEncoding/Encoder/MediaEncoder.cs:503-593` | Semáforo configurável e timeout de 60 a 120 s com kill do processo. |
| 31 | `GetCleanValue` usa `Regex.Replace` estático em todo save de item. | `src/Jellyfin.Extensions/StringExtensions.cs:170,173` | `[GeneratedRegex]`. |

---

## Onda 2: ganhos estruturais, risco médio

Mudanças maiores que exigem testes mais cuidadosos. Cada uma vale um PR próprio.

### Caches de identidade e autenticação

| # | Problema | Onde | Mudança |
|---|---|---|---|
| 32 | Usuário carregado do banco 3 ou mais vezes por requisição autenticada (autenticação, handlers de policy, controller, streaming), com join que multiplica linhas por permissões × preferências. Chamado de ~134 lugares. | `Jellyfin.Server.Implementations/Users/UserManager.cs:123-145`; `AuthorizationContext.cs:186`; `StreamingHelpers.cs:87` | Cache em memória `ConcurrentDictionary<Guid, User>` no UserManager, invalidado em todo caminho de escrita. No mínimo, `AsSplitQuery` e reuso do usuário em `HttpContext.Items`. |
| 33 | Toda checagem de token copia e ordena todos os devices; API keys vão ao banco a cada requisição sem cache; tokens inválidos custam consulta e exceção. | `AuthorizationContext.cs:129-195`; `DeviceManager.cs:146-167` | Índice secundário por access token no DeviceManager; cache de API keys com negative caching curto; só criar DbContext no ramo de API key. |

### Persistência de itens

| # | Problema | Onde | Mudança |
|---|---|---|---|
| 34 | Salvar item existente marca a entidade inteira como Modified: UPDATE de ~90 colunas e reescrita de ~27 índices; providers, imagens e locked fields são deletados e reinseridos sempre. | `ItemPersistenceService.cs:350-360,480-486` | Comparar com o registro armazenado e marcar só as propriedades alteradas; diff de providers e imagens em vez de delete+insert. |
| 35 | Cerca de 27 índices secundários em BaseItems, vários sobrepostos (ex.: `(TopParentId, Id)` vs `(Type, TopParentId, Id)`). Cada escrita paga todos. | `ModelConfiguration/BaseItemConfiguration.cs:36-85` | Medir uso real com `EXPLAIN QUERY PLAN` em banco de produção e remover os não usados. |
| 36 | Escrita de progresso de playback: `Any()` por chave, entidade inteira Modified, `ExecuteUpdate` extra, releitura de todos os usuários; 7 índices em UserData, 4 deles com prefixo igual à PK. | `UserDataManager.cs:58-99`; `UserDataConfiguration.cs:15-22` | Uma consulta por `(ItemId, UserId)` em HashSet; rehidratar só o usuário atual; revisar os índices `(ItemId, UserId, X)`. |
| 37 | Filtros que fazem `LIKE '%..%'` dentro do JSON `Data` (SeriesStatus, VideoType, Is3D, IsPlaceHolder, playlist owner, AiredDuringSeason). Nunca usam índice. | `BaseItemRepository.TranslateQuery.cs:1164-1203,1285-1289` | Promover a colunas indexadas com migração e backfill, ou colunas geradas via `json_extract`. |
| 38 | Busca e filtros de nome não usam índice: `Contains`, e `lower(SortName)` nos filtros A–Z. | `TranslateQuery.cs:264-274,486-497`; `QueryBuilding.cs:228-244`; `PeopleRepository.cs:476-493` | FTS5 para busca; SortName já normalizado ou índice em `lower(SortName)`. |
| 39 | Cada linha é desserializada do JSON `Data` por reflexão e depois sobrescrita coluna a coluna. | `BaseItemMapper.cs:485-528` | Não serializar propriedades com coluna própria no `Data`; `JsonSerializerContext` gerado. |
| 40 | Helpers de descendentes executam consultas ocultas enquanto a query é montada, várias vezes por requisição. | `DescendantQueryHelper.cs:323-428`; `TranslateQuery.cs:833-944,1103` | Calcular uma vez por requisição; CTE recursiva via `FromSql`; cache do conjunto de link-parents (BoxSet/Playlist). |
| 41 | Versões alternativas buscadas uma a uma; `Video.HasLocalAlternateVersions` é uma propriedade que consulta o banco. | `LibraryManager.cs:2311-2322`; `Video.cs:165,255-279,845-870` | Lote de cache misses em um `GetItemList`; cachear `GetLinkedChildrenIds` por requisição. |
| 42 | Caches de itens e user data dimensionados por CPU (`ProcessorCount * 100`): 400 entradas em um NAS de 4 núcleos. | `ServerConfiguration.cs:183`; `LibraryManager.cs:181`; `UserDataManager.cs:43` | Dimensionar separadamente e em valor absoluto (10 a 20 mil), ou por memória disponível. |

### DTOs e endpoints

| # | Problema | Onde | Mudança |
|---|---|---|---|
| 43 | Artists, Genres, Studios, Persons e Years montam DTOs um a um sem lote: contagens, resume e artistas vão ao banco por item. | `RequestHelpers.cs:164`; `PersonsController.cs:124`; `YearsController.cs:153`; `DtoService.cs:511-523` | Variante em lote `GetItemByNameDtos` sobre `GetBaseItemDtos`. |
| 44 | Páginas de detalhe montam DTOs completos (todos os campos) item a item para intros, trailers, temas, ancestrais e media folders; inclui trickplay sync-over-async por item. | `UserLibraryController.cs:105-492`; `LibraryController.cs:197-272,505-566`; `DtoService.cs:1451` | `GetBaseItemDtos` com conjunto mínimo de campos por endpoint; lote para trickplay. |
| 45 | Pessoas resolvidas por nome uma a uma com MD5 recomputado; mesmo padrão para estúdios e gêneros. | `DtoService.cs:939-954,1003-1035`; `LibraryManager.cs:797-819` | Memoizar `(Type, nome) -> Guid`; carregar entidades Person em lote. |
| 46 | Cache tags de imagem recomputadas com MD5 (string concat + byte[] + string) várias vezes por item. | `src/Jellyfin.Drawing/ImageProcessor.cs:435-440` | Cachear no `ItemImageInfo` ou LRU; `MD5.HashData` em span. |
| 47 | `DtoOptions.ContainsField` é varredura linear em ~70 enums, chamada 50 a 60 vezes por item. | `MediaBrowser.Controller/Dto/DtoOptions.cs` | Bitmask `ulong` precomputada. |
| 48 | Fila de refresh de metadados é serial e sem deduplicação; `LibraryMetadataRefreshConcurrency` existe na config mas nada lê. | `ProviderManager.cs:1136-1231`; `ServerConfiguration.cs:251` | Deduplicar por ItemId e rodar N consumidores conforme a config. |

### Transcodificação e HLS

| # | Problema | Onde | Mudança |
|---|---|---|---|
| 49 | Cada requisição de segmento HLS lista o diretório de transcodificação inteiro (de todas as sessões) duas vezes, segurando o lock da playlist, e faz polling de `File.Exists` a cada 100 ms dentro do lock. | `DynamicHlsController.cs:1462-1468,1925-1990,2042-2058`; `TranscodingSegmentCleaner.cs:143`; `TranscodeManager.cs:298,511-525` | Rastrear o último índice escrito no `TranscodingJob` (progress do ffmpeg ou watcher por job); liberar o lock antes de esperar; subdiretório por job. |
| 50 | Padrões de transcodificação: throttling e deleção de segmentos desligados, aceleração de hardware desligada. ffmpeg transcodifica o arquivo inteiro em velocidade máxima e mantém todos os segmentos em disco. | `MediaBrowser.Model/Configuration/EncodingOptions.cs:24-28`; `TrickplayOptions.cs:14-19,70` | Ligar throttling e segment deletion por padrão; detectar HW accel no primeiro boot e expor no wizard. |
| 51 | Extração de thumbnails: semáforo compartilhado com o resize do Skia, cada ffmpeg com `-threads 0` (N processos × N threads), `thumbnail=n=24` decodifica 24 frames, segundo ffmpeg no fallback; chapter images um ffmpeg por capítulo com `File.Copy` + delete. | `MediaEncoder.cs:133-139,618-649,722-752`; `ChapterManager.cs:146-205` | Limite próprio (`ProcessorCount/2`), `-threads 1` ou 2, `File.Move`, capítulos em uma única execução. |
| 52 | Trickplay encoda cada frame em JPEG e depois decodifica e re-encoda no Skia, com lock global de 1. | `MediaEncoder.cs:1024-1040`; `SkiaEncoder.cs:801-846`; `TrickplayManager.cs:45` | ffmpeg gerar os tiles direto com `fps,scale,tile=10x10`. |
| 53 | ~20 a 27 processos ffmpeg sequenciais no boot para validar encoder (versão, decoders, encoders, filtros, 9× `-h filter`, 5× bsf, hwaccels). 1 a 4 s antes de "Startup complete". | `ApplicationHost.cs:429`; `MediaEncoder.cs:226-250`; `EncoderValidator.cs:300-304,635-673` | Rodar as sondagens independentes em paralelo (limite 4) e cachear o resultado por caminho+tamanho+mtime do ffmpeg. |
| 54 | Imagens decodificadas em resolução total antes de qualquer resize (foto 6000×4000 = ~96 MB) com cópias extras; `SKPixmap` criado e não usado. | `SkiaEncoder.cs:384-401,557-591,709-720` | Decode escalado via `SKCodec.GetScaledDimensions`; encodar o `SKImage` direto. |

### Scan de biblioteca

| # | Problema | Onde | Mudança |
|---|---|---|---|
| 55 | Todo item inalterado rechecagem suas imagens a cada scan com stat direto no disco; imagens com BlurHash vazio (SVG, formatos não suportados, erro) são re-decodificadas e regravadas em todo scan, para sempre. | `Folder.cs:507`; `LibraryManager.cs:2532-2553,2630-2639` | Marcador "não aplicável/falhou" com o mtime; ler mtime da listagem do DirectoryService. |
| 56 | Itens que falham no probe ou na extração de thumbnail refazem tudo (inclusive lookups remotos) em todo scan, porque `DateLastRefreshed` só avança sem falhas. | `MetadataService.cs:216-231,729-746`; `ProbeProvider.cs:184-202` | Registrar "última tentativa" e contador de falhas com backoff, separado de `DateLastRefreshed`. |
| 57 | Por item: consulta `ItemExistsAsync` no caso comum (sem mudança) e stat extra que ignora o DirectoryService. | `MetadataService.cs:349,377` | Usar o DirectoryService e pular a checagem de existência para itens vindos da enumeração. |
| 58 | Monitor de biblioteca processa eventos de forma síncrona na thread do watcher, assina Attributes/CreationTime/Size (muitos eventos por cópia) e, em overflow de buffer, descarta o watcher sem recriar. | `LibraryMonitor.cs:255-271,340-354,374-460` | `Channel` limitado com consumidor em background que coalesce por diretório; filtros mínimos; recriar o watcher em overflow. |
| 59 | Fan-out do scan: `ProcessorCount - 3` sem teto (29 a 61 workers em máquinas grandes, 1 em 4 núcleos). | `LimitedConcurrencyLibraryScheduler.cs:163-176` | `Math.Clamp(ProcessorCount - 3, 4, 8)`; a config explícita continua sobrescrevendo. |
| 60 | Cache do `.ignore` com ProcessorCount × 100 entradas e walk de pais em miss; limpo no início e fim de cada scan. | `DotIgnoreIgnoreRule.cs:28-36,151-215`; `LibraryManager.cs:1419,1429` | Mapa top-down de `.ignore` construído uma vez por scan. |
| 61 | Plugins: todo DLL de toda pasta de plugin tem todos os tipos carregados e refletidos duas vezes. | `PluginManager.cs:136-189`; `ApplicationHost.cs:761-791` | Só tratar como assembly de plugin o que referencia MediaBrowser.*; carregar dependências sob demanda. |

---

## Onda 3: runtime e build

| # | Problema | Onde | Mudança |
|---|---|---|---|
| 62 | Nenhuma otimização de JIT ou modelo EF: sem `PublishReadyToRun`, sem `TieredPGO` explícito, sem modelo EF compilado. | `Jellyfin.Server/Jellyfin.Server.csproj`; `ServiceCollectionExtensions.cs:139` | `PublishReadyToRun=true` no publish; `dotnet ef dbcontext optimize` para o JellyfinDbContext; avaliar Server GC com DATAS (padrão .NET 9+) em vez de `ServerGarbageCollection=false`, mantendo workstation para hosts com pouca RAM via `DOTNET_gcServer`. |
| 63 | Host observa o diretório de trabalho recursivamente por mudanças de config (`reloadConfigOnChange` padrão). | `Program.cs:178,402-403` | `hostBuilder:reloadConfigOnChange=false`, como o SetupServer já faz. |
| 64 | Swagger carrega todos os `*.xml` do diretório base em memória para sempre após a primeira requisição. | `ApiServiceCollectionExtensions.cs:222-230` | Só os XML de API/Model, ou swagger opt-in. |
| 65 | JSON sem source generation (`DefaultJsonTypeInfoResolver` por reflexão). | `JsonDefaults.cs` | `JsonSerializerContext` para BaseItemDto, QueryResult, SessionInfoDto e mensagens de websocket. Ganho moderado por causa dos conversores customizados. |

---

## Correções de robustez encontradas no caminho

Não são de performance, mas apareceram na leitura e valem um PR curto.

- `MediaSourceManager.cs:804`: `AsyncFile.OpenRead(cacheFilePath)` fica fora do `try`, então o método lança em cache miss e nunca preenche o cache.
- `WebSocketConnection.SendAsync` sem lock: envios concorrentes em um `WebSocket` lançam `InvalidOperationException` e derrubam a conexão.
- `LibraryMonitor.OnWatcherError` descarta o watcher em `InternalBufferOverflowException` e nunca recria; o monitoramento daquela biblioteca para até o próximo scan ou restart.
- `ImageProcessor` escreve direto no arquivo de cache final; um `File.Exists` concorrente pode servir um arquivo parcial.

---

## O que já está bem

- Pool de DbContext via `AddPooledDbContextFactory`; WAL ligado por migração; `synchronous=1` e `temp_store=2` aplicados.
- Modo de lock padrão NoLock (sem lock global).
- Caminhos de leitura no repositório de itens usam `AsNoTracking`.
- `GetBaseItemDtos` já faz lote de user data, contagens, artistas, pessoas e versões alternativas.
- `JsonSerializerOptions` é singleton estático; XmlSerializer, TypeMapper, BaseItemMapper e OpenAPI têm cache.
- ProbeProvider só re-sonda quando o mtime do arquivo ou arquivos externos mudam.
- Timers de sessão e keep-alive de websocket são baratos.

---

## Como medir antes de mudar

1. Instalar ffmpeg e rodar o servidor com `--nowebclient --datadir <temp>`; medir tempo até "Startup complete", working set e threads.
2. Criar uma biblioteca sintética (alguns milhares de arquivos vazios com nomes válidos) e cronometrar o scan completo e o rescan sem mudanças.
3. Com `dotnet-counters` e `dotnet-trace`, registrar GC gen2, alocações por segundo e latência das rotas `/Items`, `/Users/{id}/Items/{id}` e segmentos HLS sob carga (`bombardier` ou `k6`).
4. Ligar o log de consultas do EF (`Microsoft.EntityFrameworkCore.Database.Command` em Debug) e contar consultas por requisição nas rotas acima.
5. Repetir após cada onda e registrar os números aqui.

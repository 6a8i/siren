# Roadmap do Siren

Lista de melhorias levantada em 2026-10-28, depois da entrega das famílias, com a
ordem de execução e o estado de cada item. O roadmap de performance continua em
`performance-roadmap.md`; este arquivo cobre o restante do servidor e o cliente web.

Legenda: **feito**, **planejado** (fase futura), **descartado** (com o motivo).

## Decisões que valem para tudo

- `ProductName` em `/System/Info` e `/System/Info/Public` continua `Jellyfin Server`.
  Os aplicativos oficiais (Android, Android TV, Swiftfin, etc.) usam esse valor para
  reconhecer um servidor compatível; mudar quebraria o login neles. O rebranding do
  servidor fica em logs, console, página de inicialização, ajuda da linha de comando e
  usuário padrão.
- O fork diverge do upstream de propósito (ondas de performance, rebranding, famílias).
  A estratégia para trazer correções de segurança do Jellyfin está em `upstream.md`.
- Nada é publicado automaticamente: cada push ao GitHub é aprovado pelo dono do projeto.

## Fase 1 (concluída em 2026-10-28)

### Servidor

| # | Item | Estado | Como ficou |
|---|------|--------|------------|
| S1 | Acesso a bibliotecas no nível da família | feito | `Families.EnableAllFolders` + `EnabledFolders`, migração `AddFamilyLibraryAccess`; perfis são limitados ao alcance da família na criação, no vínculo, na edição pela API de famílias e na página genérica de usuário (`CapToFamily`) |
| S2 | Bloqueio por tentativas de PIN | feito | 5 tentativas; `IsLockedOut` no `FamilyMemberDto`; desbloqueio ao editar o membro com `IsDisabled = false`; rotina `SetFamilyProfileLockout` aplica o limite aos perfis antigos |
| S3 | Log de atividades de família | feito | eventos + consumidores (`FamilyCreated`, `FamilyRenamed`, `FamilyDeleted`, `FamilyProfileCreated/Linked/Unlinked/Deleted`, `FamilyProfileSwitch`), strings em en-US e pt-BR |
| S4 | `HasPin`, `IsLockedOut`, `HasAllFamilyFolders` no DTO de membro | feito | `FamilyMemberDto : UserDto`; `HasPassword` obsoleto ainda preenchido por uma versão |
| S5 | Teste de integração do `FamilyController` | feito | `FamilyControllerTests` (6 cenários, inclusive capping e bloqueio) |
| S6 | Sessões por família | feito | administrador da família (ou membro com controle remoto) vê e controla só sessões da família; `ISessionManager.CanControlSession`; todos os comandos de sessão respondem 403 fora da família; web socket de sessões segue a mesma regra |
| S7 | Auditoria de vazamentos entre famílias | feito | tabela em `families.md`; correções em playlists compartilhadas e no web socket de tarefas; `FamilyIsolationTests` e testes do SyncPlay |
| S8 | Build Debug e da solução inteira | feito | pacotes `Microsoft.CodeAnalysis.*` fixados em 5.6.0 (Roslyn do SDK 10.0.301); erros do analisador corrigidos no código do fork |
| S9 | Testes de localização | feito | o teste dependia da cultura do sistema; agora fixa en-US |
| S10 | CI no GitHub Actions | feito | `.github/workflows/ci.yml`: restore, build Release da solução, todos os testes, `dotnet format --verify-no-changes` |
| S11 | Cabeçalhos de segurança | feito | `SecurityHeadersMiddleware`, `ServerConfiguration.EnableSecurityHeaders` (padrão ligado) |
| S12 | Limite de tentativas de autenticação por IP | feito | 10 por minuto por IP em login, recuperação de senha e troca de perfil; 429 com `Retry-After` |
| S13 | Tarefa agendada de backup | feito | tarefa `SystemBackup`, domingo 03:00, banco + configuração, mantém os 5 últimos arquivos agendados |
| S14 | Instrumentação de boot e scan | feito | linhas `Startup phases` e `Library {Name} scanned in ...`; como ler em `performance-roadmap.md` |
| S15 | Identidade do produto no servidor | feito | logs, ajuda da linha de comando, página de inicialização e usuário padrão `MySirenUser`; `ProductName` mantido |
| S16 | Estratégia de sincronização com o upstream | feito | `upstream.md`; remote `upstream` adicionado ao repositório do servidor |
| — | VACUUM por fragmentação | feito antes | já implementado na onda de performance (`SqliteDatabaseProvider`); descrito em `operations.md` |
| — | Endpoint de métricas Prometheus | feito antes | já existia: `EnableMetrics` expõe `/metrics` |

### Cliente web

| # | Item | Estado | Como ficou |
|---|------|--------|------------|
| W1 | `HasPin`, `IsLockedOut`, `HasAllFamilyFolders`; perfil bloqueado com desbloqueio | feito | chip "Bloqueado" no grid e no painel; ação "Desbloquear"; o prompt de PIN mostra o estado bloqueado; 403 na troca tratado como perfil recusado |
| W2 | Hooks de família devolvem `undefined` no 401 | feito | `nullToUndefined` como `select`; o comportamento global do cache permanece |
| W3 | Bibliotecas da família no painel; diálogo de membro limitado ao alcance | feito | `FamilyLibrariesCard`; o diálogo lista só o que a família (e quem edita) alcança |
| W4 | Editor de membro no painel | feito | mesmo diálogo da tela da família; link secundário para a página completa do usuário |
| W5 | Preset de perfil infantil | feito | pré-seleciona a maior classificação até 10 anos |
| W6 | Seletor de perfis ao abrir o app; lembrar o último perfil | feito | redirecionamento em `ConnectionRequired`; `sessionStorage siren.profileChosen`, `localStorage siren.lastProfile.<serverId>`; chip "Continuar como X" |
| W7 | Celular e TV nas telas de família | feito | diálogos em tela cheia abaixo de `sm`; verificado a 375 px; tiles são botões focáveis |
| W8 | Alvo de compilação ES2017 e `browserslist`; build de produção | feito | `browser-support.md` no web; bundle inicial 4,6% menor; `dist` passa a ser o build de produção |
| W9 | Testes unitários de família | feito | `familySwitch`, `apiError`, `useMyFamily`, `profileSelection`, `accent` (216 testes no total) |
| W10 | Cor de destaque configurável | feito | 8 presets com contraste AA nos dois temas; por usuário, em `userSettings.accentColor` |

### Verificação da fase 1

- Servidor: build Release e Debug da solução inteira, 18 projetos de teste (4.227 testes aprovados, 21 ignorados) e
  `dotnet format --verify-no-changes`, o mesmo que o CI executa.
- Instância descartável (`siren-test`): cenário de API com 42 verificações (DTO de membro,
  alcance de bibliotecas, 403 para administrador de família, log de atividades, sessões,
  bloqueio e desbloqueio, cabeçalhos, 429 na 11ª tentativa de login), mais a conferência no
  navegador do painel de famílias, do seletor de perfis, do bloqueio e da cor de destaque.

## Fase 2a (concluída em 2026-10-28)

### Servidor

| # | Item | Estado | Como ficou |
|---|------|--------|------------|
| S17 | Bloqueio de PIN temporário em vez de desativar o perfil | feito | 15 minutos (`Users.LockedOutUntil`, migração `AddLockedOutUntil`); `POST .../Members/{userId}/Unlock` encerra antes; perfis sem família mantêm o comportamento do upstream |
| S18 | Bibliotecas que a família alcança, por nome | feito | `GET /Families/{id}/Libraries` e `GET /Families/Mine/Libraries` (`FamilyLibraryDto`) |
| S19 | Imagem de perfil definida pelo administrador da família | feito | `POST|DELETE /Families/{id}/Members/{userId}/Image`; `ImageUploadHelper` compartilhado com `/UserImage` |
| S20 | CSP completa em modo relatório | feito | `ContentSecurityPolicyMode` (`Off`, `ReportOnly`, `Enforce`; padrão `ReportOnly`), `POST /System/CspReport` com limite de 20 relatos por minuto no log; a primeira rodada no navegador só acusou o SDK do Chromecast, já liberado |
| S21 | Rate limit de autenticação configurável | feito | `AuthenticationRateLimitPerMinute` (padrão 10; 0 desliga) |
| S22 | Performance 45: ids por nome memoizados e pessoas carregadas em lote | feito | `ItemByNameIdCache` (LRU de 8192), `GetPersonsByName` |
| S23 | Performance 61: carga de plugins | feito | só DLLs que referenciam o servidor são carregadas; dependências sob demanda; uma passagem de reflexão |
| — | "Quem está assistindo agora" para o administrador da família | feito | não precisou de servidor: usa as sessões já filtradas por família (S6) |

### Cliente web

| # | Item | Estado | Como ficou |
|---|------|--------|------------|
| W11 | Contagem regressiva do bloqueio e desbloqueio pelo endpoint novo | feito | chip "Bloqueado por N min", prompt com a hora do desbloqueio, destrava sozinho quando o tempo passa |
| W12 | Bibliotecas da família por nome nos diálogos | feito | `useFamilyLibraries`; o administrador da família vê até as que ele próprio não acessa |
| W13 | Seção "Assistindo agora" no modo gerenciar | feito | sessões da família a cada 10 s e pelo web socket; dispositivo, item em reprodução e progresso |
| W14 | Avatares com presets | feito | 8 cores × 12 ícones renderizados em PNG 256 px e enviados pela API de famílias |
| W15 | Suíte Playwright | feito | 7 testes contra um servidor descartável na porta 8097 (`npm run test:e2e`, `docs/e2e.md`), workflow `e2e.yml` ainda não executado no GitHub |
| W16 | Polyfills ES5 | feito | removidos regenerator, whatwg-fetch, proxy-polyfill e element-closest; os demais ficam documentados em `browser-support.md` |
| W17 | Configurações de segurança no painel | feito | seção "Segurança" na página de rede: cabeçalhos, modo da CSP e limite de logins |

### Verificação da fase 2a

- Servidor: build Release e Debug da solução inteira, 4.303 testes aprovados, `dotnet format`
  com analisadores.
- Instância descartável: cenário de API com 38 verificações (CSP e relato de violação,
  bibliotecas por nome, imagem de membro, bloqueio temporário com `Unlock`, 429 na 11ª
  tentativa), suíte Playwright (7 testes) e conferência no navegador do painel de rede, dos
  avatares, da seção "Assistindo agora" e da contagem regressiva do bloqueio.

## Fase 2b (planejado)

### Servidor

- Cauda do roadmap de performance que exige medição com dados reais: índices e colunas
  promovidas em `BaseItems` com FTS5 (itens 35, 37, 38), serialização do campo `Data` (39),
  campos mínimos por endpoint (44), tiles de trickplay no ffmpeg (52), backoff de refresh com
  falha (56) e source generation do JSON (65). O banco atual tem 1 MB, então esses itens só
  valem com uma biblioteca grande para comparar.
- Escopos para chaves de API (somente leitura, por biblioteca); exige migração própria.
- Segundo fator para o administrador do servidor, só no cliente web (os apps oficiais não
  sabem pedir o segundo fator).
- Passar a CSP para `Enforce` depois de alguns dias sem violações no log, com teste de
  reprodução em todos os players.
- Perfil infantil com interface simplificada (além do preset de classificação).

### Cliente web

- Executar o workflow `e2e.yml` no GitHub e ampliar a suíte (criação de família pelo painel,
  reprodução).
- Migração das views legadas (login, wizard, cadastro e edição de usuários) para
  React e MUI.
- Upload de uma foto própria como avatar (hoje só presets).
- Remover as dependências de polyfill que sobraram no `package.json` e trocar o
  `classList.replace` do slideshow para dispensar o `classlist.js`.
- Tradução das strings novas para os demais idiomas do cliente (hoje só en-US e
  pt-BR; os outros caem no inglês).
- Seções da tela inicial por perfil já existem nas preferências de exibição do upstream;
  nada a fazer.

## Fora do roadmap

- Trocar `ProductName`: ver decisões.
- Migrar para outro banco: o SQLite com o modelo compilado atende o uso doméstico.

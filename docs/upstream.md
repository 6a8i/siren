# Sincronização com o upstream (Jellyfin)

O Siren é um fork que diverge do Jellyfin de propósito: ondas de performance,
rebranding, famílias, hardening. Este documento define como trazer correções do
upstream sem desfazer esse trabalho.

## Ponto de partida

| Repositório | Base do fork (upstream) | Data | Commits próprios em 2026-10-28 |
|-------------|-------------------------|------|--------------------------------|
| servidor (`6a8i/siren`) | `02c2951eb7` (master do Jellyfin, versão de assembly 13.0.0) | 2026-10-26 | 13 |
| web (`6a8i/siren-web`) | `0a580ce8df` ("Bump version to 12.4") | 2026-10-26 | 8 |

Remotes: o web já tem `upstream` apontando para `jellyfin/jellyfin-web`. No servidor:

```bash
git remote add upstream https://github.com/jellyfin/jellyfin.git
```

## Regra geral: cherry-pick, não merge

Um `git merge upstream/master` reintroduziria as superfícies Jellyfin que o rebranding
trocou, conflitaria com as ondas de performance (repositórios, scan, transcodificação,
host) e traria mudanças de esquema que precisam passar pelo modelo compilado. Por isso:

1. **Correções de segurança e bugs**: cherry-pick por commit, na ordem do upstream, em
   uma branch `upstream-sync`. Fontes: avisos de segurança do Jellyfin (GHSA), notas das
   versões 10.11.x/12.x e o changelog do `jellyfin-web`.
2. **Funcionalidades grandes do upstream**: decidir caso a caso. Se vale a pena, portar à
   mão em cima do código do Siren em vez de cherry-pick, porque as áreas abaixo já não
   batem com o upstream.
3. **Traduções do Weblate**: ignorar no servidor (strings do Siren ficam em en-US e pt-BR);
   no web, trazer só os arquivos de idioma que não tocam as chaves do Siren.

## Áreas que conflitam com frequência

Servidor:

- `Jellyfin.Server.Implementations/Users/UserManager.cs` (famílias, usuário padrão)
- `Jellyfin.Server/Startup.cs`, `Jellyfin.Server/Program.cs`, `Emby.Server.Implementations/ApplicationHost.cs` (hardening, instrumentação, rebranding)
- `Jellyfin.Server.Implementations/Item/BaseItemRepository.cs` e `src/Jellyfin.Database/*` (ondas de performance, entidades de família, modelo compilado)
- `Emby.Server.Implementations/Library/LibraryManager.cs` e o monitor de biblioteca
- `Jellyfin.Api/Controllers/UserController.cs`, `SessionController.cs`, `Emby.Server.Implementations/Session/SessionManager.cs` (escopo por família)

Web:

- temas e tokens do design system, `ServerDrawerSection`, menu do usuário, páginas de usuários do painel
- `src/utils/query/queryClient.ts` e os hooks sob `src/hooks/api`

## Procedimento

```bash
git fetch upstream
git checkout -b upstream-sync master
git cherry-pick <commit>...
```

Depois de cada lote:

1. Servidor: `dotnet build Jellyfin.sln -c Release`, os três projetos de teste, `dotnet format Jellyfin.sln --verify-no-changes`.
   Se o lote tocar entidades ou migrações: regenerar o modelo compilado
   (`dotnet ef dbcontext optimize ...`, ver `families.md`) e conferir que as migrações do
   Siren (`AddFamilies`, `AddFamilyLibraryAccess`) continuam depois das do upstream no snapshot.
2. Web: `npx tsc --noEmit`, `npx eslint`, `npm test`, `npm run build:production`, `npm run escheck`.
   Se o lote subir a versão do `@jellyfin/sdk`, confirmar que o servidor expõe a API esperada.
3. Verificar na instância descartável (`siren-test`) o que o lote toca.
4. Registrar o lote na tabela abaixo e integrar em `master` com um commit por lote
   (`git merge --squash upstream-sync`), com a mensagem citando os commits de origem.

## Compatibilidade que não se mexe

- `ProductName` continua `Jellyfin Server` (apps oficiais dependem disso).
- Rotas, nomes de DTO e o formato do `/System/Info` ficam compatíveis com o SDK do upstream.
- Caminhos de configuração e de dados continuam os do Jellyfin, para que um backup do
  Jellyfin seja restaurável no Siren.

## Lotes aplicados

| Data | Repositório | Commits do upstream | Motivo |
|------|-------------|---------------------|--------|
| — | — | — | nenhum até 2026-10-28 |

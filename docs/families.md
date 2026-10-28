# Famílias

Uma **família** é um grupo de até **6 perfis** que só enxergam uns aos outros. O servidor continua
tendo o **administrador do servidor** (permissão `IsAdministrator`), que vê e gerencia todas as contas
e famílias pelo painel e define quais bibliotecas cada família alcança. Dentro de cada família, um ou
mais perfis com a permissão `IsFamilyAdministrator` gerenciam os perfis daquela família (criar,
renomear, PIN, controle parental, acesso às bibliotecas dentro do alcance da família, desativar,
desbloquear, remover) sem nenhum acesso ao painel do servidor.

## Modelo

| Onde | O quê |
|---|---|
| `Families` (tabela) | `Id`, `Name`, `DateCreated`, `EnableAllFolders` (padrão `true`), `EnabledFolders` (ids das bibliotecas separados por vírgula, `NULL` para nenhuma; `Family.GetEnabledFolderIds`/`SetEnabledFolderIds`), `RowVersion`. Migração `AddFamilyLibraryAccess`. |
| `Users.FamilyId` | Família do usuário, ou `NULL` para contas fora de qualquer família (admins do servidor e contas antigas). FK com `ON DELETE SET NULL`. |
| `PermissionKind.IsFamilyAdministrator` (24) | Perfil que administra a própria família. |
| `UserDto.FamilyId`, `UserPolicy.IsFamilyAdministrator` | Expostos na API para o cliente. |
| `FamilyDto` | `Id`, `Name`, `DateCreated`, `MemberCount`, `MaxMembers`, `EnableAllFolders`, `EnabledFolders`. |
| `FamilyMemberDto` | `UserDto` mais `HasPin` (o perfil tem senha), `IsLockedOut` (desativado por PINs errados) e `HasAllFamilyFolders` (alcança tudo o que a família alcança). Devolvido por todos os endpoints de membros. |
| `Family.MaxMembers` | 6, aplicado em `FamilyManager.AssignUserAsync` e em `POST /Families/{id}/Members`. |
| `Family.LoginAttemptsBeforeLockout` | 5, `LoginAttemptsBeforeLockout` dos perfis criados pela API de famílias. |
| `FamilyEntityExtensions` (`Jellyfin.Data`) | `GetLibraryReach`, `CapToFamily(user, family)` e `HasAllFamilyFolders`: a regra de alcance em um só lugar. |

Regras aplicadas no servidor:

- Um administrador do servidor nunca pertence a uma família, e um membro de família nunca vira
  administrador do servidor (`UserManager.UpdatePolicyAsync` e `POST /Users/{id}/Policy` recusam).
- Quem sai de uma família (ou tem a família excluída) perde `IsFamilyAdministrator`.
- **Alcance de bibliotecas.** O administrador do servidor define as bibliotecas que a família alcança
  (`EnableAllFolders`/`EnabledFolders` da família); os perfis herdam esse alcance e nunca o excedem.
  O limite é aplicado ao mudar o alcance da família (`FamilyManager.UpdateLibraryAccessAsync` limita
  todos os membros), ao entrar numa família (`AssignUserAsync`), ao criar ou editar um perfil pela API
  de famílias e em `UserManager.UpdatePolicyAsync` quando o usuário tem `FamilyId`, para que a página
  genérica de usuário do painel também não ultrapasse a família. Um usuário que alcançava tudo passa a
  alcançar exatamente as bibliotecas da família; um usuário com lista fica só com as que a família
  também alcança. Se a família alcança tudo, nada muda.
- Pela API de famílias, o que pode ser concedido a um perfil é o alcance da família ∩ o alcance de
  quem edita (administrador do servidor e chave de API = alcance da família). Nos membros,
  `EnableAllFolders = true` (ou ausente na criação) significa "todas as bibliotecas da família": se a
  família alcança tudo, o perfil fica com `EnableAllFolders = true`; senão, com `EnableAllFolders =
  false` e `EnabledFolders` = lista da família (∩ alcance de quem edita).
- **Bloqueio por PIN.** Perfis criados pela API de famílias (inclusive o primeiro administrador de uma
  família nova) nascem com `LoginAttemptsBeforeLockout = 5`; contas vinculadas mantêm o valor que já
  tinham. Depois de 5 PINs errados o perfil é desativado (`IsLockedOut = true`). Editar o membro com
  `IsDisabled = false` reativa e zera `InvalidLoginAttemptCount` (é o desbloqueio); um administrador de
  família não desativa a si mesmo, mas desbloqueia qualquer perfil da família.
- A família precisa manter ao menos um administrador de família quando quem edita é um
  administrador de família; o administrador do servidor pode deixar a família sem nenhum.

## Isolamento

- `GET /Users`: quem não é administrador do servidor recebe só a própria família (ou só a si mesmo,
  fora de uma família). Vale para o compartilhamento de playlists e qualquer outra lista de usuários.
- `GET /Users/{id}`: 403 para contas de outra família.
- `GET /Users/Public` (tela de login): perfis de família só aparecem em um dispositivo onde algum perfil
  daquela família já está conectado (há um registro em `Devices` para o `DeviceId` da requisição).
  Em um dispositivo novo o perfil entra pelo login manual (nome + PIN) e, a partir daí, a família
  aparece na tela de login daquele aparelho.
- SyncPlay: um grupo guarda a família de quem o criou (`Group.FamilyId`) e só é listado, consultado
  ou aceito (`JoinGroup`) por membros da mesma família; grupos de contas sem família ficam entre
  contas sem família.
- Sessões, log de atividade, dispositivos e todo o painel continuam restritos ao administrador do
  servidor, como antes.

## API (`Jellyfin.Api/Controllers/FamilyController.cs`)

| Método | Rota | Quem | Corpo |
|---|---|---|---|
| GET | `/Families` | admin do servidor | |
| POST | `/Families` | admin do servidor | `{ Name }` |
| GET | `/Families/Mine` | qualquer usuário | 404 sem família |
| GET | `/Families/{id}` | admin ou membro | |
| POST | `/Families/{id}` | admin ou admin da família | `{ Name?, EnableAllFolders?, EnabledFolders? }` → 204. Campo ausente mantém o valor; `EnabledFolders` sem `EnableAllFolders` vale como `false`. Os campos de biblioteca só pelo admin do servidor (403 para admin da família, que ainda renomeia); mudá-los limita todos os perfis |
| DELETE | `/Families/{id}?deleteMembers=` | admin do servidor | `false` mantém os perfis como contas soltas |
| GET | `/Families/{id}/Members` | admin ou membro | `FamilyMemberDto[]`; `HasPin` indica se o perfil pede PIN |
| GET | `/Families/{id}/Members/{userId}` | admin ou membro | `FamilyMemberDto` |
| POST | `/Families/{id}/Members` | admin ou admin da família | `{ Name, Password?, IsFamilyAdministrator?, MaxParentalRating?, EnableAllFolders?, EnabledFolders? }` → `FamilyMemberDto` |
| PUT | `/Families/{id}/Members/{userId}` | admin do servidor | move uma conta existente para a família, limitada ao alcance dela |
| POST | `/Families/{id}/Members/{userId}` | admin ou admin da família | `{ Name, IsFamilyAdministrator, IsDisabled, MaxParentalRating, EnableAllFolders, EnabledFolders }` → 200 `FamilyMemberDto`; `IsDisabled = false` também desbloqueia |
| POST | `/Families/{id}/Members/{userId}/Password` | admin ou admin da família | `{ NewPassword?, ResetPassword? }` |
| DELETE | `/Families/{id}/Members/{userId}?deleteUser=` | admin ou admin da família | admin da família só com `true` e nunca a si mesmo |
| POST | `/Families/{id}/Members/{userId}/Switch` | membro da mesma família | `{ Pw? }` → `AuthenticationResult` com o novo token; 403 se o perfil está desativado ou bloqueado |

A troca de perfil (`Switch`) passa pelo mesmo caminho do login (`AuthenticateNewSession`): PIN errado
conta como tentativa inválida (401) e respeita o bloqueio por tentativas.

Log de atividade (eventos em `Jellyfin.Data/Events/Families`, consumidores em
`Jellyfin.Server.Implementations/Events/Consumers/Families`, textos em `Localization/Core/*.json` com
fallback para `en-US`): `FamilyCreated`, `FamilyRenamed` e `FamilyDeleted` são publicados pelo
`FamilyManager`; `FamilyProfileCreated`, `FamilyProfileLinked` (conta existente vinculada),
`FamilyProfileUnlinked` (removido mantendo a conta), `FamilyProfileDeleted` e `FamilyProfileSwitch`
("X trocou para o perfil Y", `UserId` = perfil de destino, publicado pelo `Switch` após a autenticação)
pelo `FamilyController`. As entradas genéricas de usuário (`UserCreated`, `UserDeleted`,
`UserLockedOut`, `AuthenticationFailed`) continuam sendo geradas como antes.

## Cliente web (siren-web)

- Painel → **Famílias**: lista, criação (nome + primeiro perfil administrador), detalhe com perfis,
  PIN, vínculo de contas existentes e exclusão.
- Menu do usuário → **Trocar de perfil** (`/family`): grade de perfis da família, com PIN quando o perfil
  tem senha. Administradores da família têm **Gerenciar família** na mesma tela.
- Um perfil de família não vê o painel nem o editor de metadados (eles continuam atrás de
  `IsAdministrator`).

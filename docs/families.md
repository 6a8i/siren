# Famílias

Uma **família** é um grupo de até **6 perfis** que só enxergam uns aos outros. O servidor continua
tendo o **administrador do servidor** (permissão `IsAdministrator`), que vê e gerencia todas as contas
e famílias pelo painel. Dentro de cada família, um ou mais perfis com a permissão
`IsFamilyAdministrator` gerenciam os perfis daquela família (criar, renomear, PIN, controle parental,
acesso às bibliotecas, desativar, remover) sem nenhum acesso ao painel do servidor.

## Modelo

| Onde | O quê |
|---|---|
| `Families` (tabela) | `Id`, `Name`, `DateCreated`, `RowVersion`. |
| `Users.FamilyId` | Família do usuário, ou `NULL` para contas fora de qualquer família (admins do servidor e contas antigas). FK com `ON DELETE SET NULL`. |
| `PermissionKind.IsFamilyAdministrator` (24) | Perfil que administra a própria família. |
| `UserDto.FamilyId`, `UserPolicy.IsFamilyAdministrator` | Expostos na API para o cliente. |
| `Family.MaxMembers` | 6, aplicado em `FamilyManager.AssignUserAsync` e em `POST /Families/{id}/Members`. |

Regras aplicadas no servidor:

- Um administrador do servidor nunca pertence a uma família, e um membro de família nunca vira
  administrador do servidor (`UserManager.UpdatePolicyAsync` e `POST /Users/{id}/Policy` recusam).
- Quem sai de uma família (ou tem a família excluída) perde `IsFamilyAdministrator`.
- Um administrador de família só concede às outras contas as bibliotecas que ele mesmo acessa.
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
| POST | `/Families/{id}` | admin ou admin da família | `{ Name }` |
| DELETE | `/Families/{id}?deleteMembers=` | admin do servidor | `false` mantém os perfis como contas soltas |
| GET | `/Families/{id}/Members` | admin ou membro | `HasPassword` indica se o perfil pede PIN |
| POST | `/Families/{id}/Members` | admin ou admin da família | `{ Name, Password?, IsFamilyAdministrator?, MaxParentalRating?, EnableAllFolders?, EnabledFolders? }` |
| PUT | `/Families/{id}/Members/{userId}` | admin do servidor | move uma conta existente para a família |
| POST | `/Families/{id}/Members/{userId}` | admin ou admin da família | `{ Name, IsFamilyAdministrator, IsDisabled, MaxParentalRating, EnableAllFolders, EnabledFolders }` |
| POST | `/Families/{id}/Members/{userId}/Password` | admin ou admin da família | `{ NewPassword?, ResetPassword? }` |
| DELETE | `/Families/{id}/Members/{userId}?deleteUser=` | admin ou admin da família | admin da família só com `true` e nunca a si mesmo |
| POST | `/Families/{id}/Members/{userId}/Switch` | membro da mesma família | `{ Pw? }` → `AuthenticationResult` com o novo token |

A troca de perfil (`Switch`) passa pelo mesmo caminho do login (`AuthenticateNewSession`): PIN errado
conta como tentativa inválida e respeita o bloqueio por tentativas.

## Cliente web (siren-web)

- Painel → **Famílias**: lista, criação (nome + primeiro perfil administrador), detalhe com perfis,
  PIN, vínculo de contas existentes e exclusão.
- Menu do usuário → **Trocar de perfil** (`/family`): grade de perfis da família, com PIN quando o perfil
  tem senha. Administradores da família têm **Gerenciar família** na mesma tela.
- Um perfil de família não vê o painel nem o editor de metadados (eles continuam atrás de
  `IsAdministrator`).

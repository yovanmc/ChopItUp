# Milestone 51: classes, model and effort in the Roles dialog

Outcome: each participant card in the Roles dialog shows what the roster says about the row, read-only:
the model name its host CLI is launched with, its class set, and the effort its classes earn inside a run.
Editing a persona, a global role or a room override, suppressing and clearing, and dispatch by class are
unchanged. No new configuration mechanism: classes still change with `--set-classes` while the hub is
stopped, and the dialog says so.

## Acceptance
1. `GET /api/rooms/{id}/roles` (and every write's answer) carries `model`, `classes` (normalised, empty
   when none) and `effort` (`high` for a judge-class row, `null` for "no flag, the CLI default") per row,
   plus `conductorEffort` at the top level. Values come from the live roster and `EffortPolicy`, the same
   rule the launch site applies; nothing is a guessed runtime value.
2. The dialog renders the three fields per card. A row with no classes says `none`. A row earning no
   flag says the CLI default applies and names the conductor exception. Both effort values are the
   server's, never a literal in the client.
3. Existing Roles tests, the run-effort tests (`Run11_AC7*`) and the spawn command-line tests pass unchanged.
4. Release gates: Debug build with warnings as errors, full .NET suite, client suite, the Row 14 Roles
   synthetic check, and a scratch UIA open, capture and close of the dialog showing the new line.

## Seams
- `src/ChopItUp.Hub/Spawning/EffortPolicy.cs` (new): `ForClasses` and `AtLaunch`; `SpawnerService.Launch` calls the latter.
- `src/ChopItUp.Hub/Web/RolesApi.cs` `BuildRoomRoles`.
- `src/ChopItUp.Hub/client/src/RolesDialog.tsx` `describeClasses`, `describeEffort`, the `roles-meta` line; `types.ts`; `styles.css`.
- Tests: `RolesApiTests` (3 new), `EffortPolicyTests` (new), `RolesDialog.test.tsx` (7 new).

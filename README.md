# PBM Editor

> **Steam:** An unofficial alternative to the official free DataEditor. Built and tested with the Steam version of the game. The Epic Games version is not verified; in "New mod..." the game folder can be selected manually with "Browse...".

> **Compatibility:** This tool only works with **Pro Basketball Manager 2025**. Pro Basketball Manager 2026 has not been tested. If it turns out to be incompatible, a separate version for PBM 2026 will follow soon.

A desktop editor for **Pro Basketball Manager 2025** mods and save games. It edits the game's SQLite database (`.pbmsave`) through forms instead of raw tables: teams, players, leagues, cups, finances, contracts, logos, jerseys and photos.

Unofficial fan tool, not affiliated with Umix Studios. Always work on a mod or a copy of a save; the editor also makes a backup before the first write.

## Features

- **Mods and saves**: pick a local mod, a Steam workshop mod or a save game from a list, or open any `.pbmsave` file.
- **New mod**: creates a new mod from the game's default data (same result as "duplicate default mod" in the game) and opens it.
- **Teams**: name, founding year, city, budget and finances, fans and prestige, colours, rivals, logo, jerseys, arena (name, capacity, images), staff.
- **Players**: name, birth date and age, height and weight, in-game look (skin, hair, beard), 26 attributes, contract, jersey number, photo.
  A live preview shows the game's 2D avatar for the current appearance values and team colours (built from the game's own layer images, so it needs the game installed). Hairstyles are offered per gender, and a note explains when the game itself reuses a picture or model.
  Move a player to another team or release them as a free agent.
- **New player**: create a player as free agent or directly for a team (button "New player..." in the team players tab and the free agents tab): name, gender, nationality, birth date, body, main position, overall rating (the 26 attributes are generated from it and the position), potential, look, jersey number and contract. Fine-tune everything in the editor afterwards.
- **Staff**: coaches, scouts, physios and others per team: name, birth date, nationality, look with live avatar preview (jacket and tie), skills, contract (role, salary, term), photo; move to another team or release.
- **Free agents**: search, edit and sign for a team.
- **Leagues and cups**: names and settings, rules, team assignment, phases, forced and qualified teams, brackets, match days, prize money, quotas, promotion and relegation, objectives, divisions.
- **Jerseys**: preview of the game's default kit (front and back) from the team colours, one-click generation, combine a front and a back photo into one image, or remove the file to fall back to the default.
- **Languages**: English (default), German and French, switchable at runtime.

## Safety

- Changes are collected in one database transaction: **Save** commits, **Discard** rolls back, **Show changes** lists the pending statements.
- A backup is written to `Documents\PbmEditor\Backups` before the first change.
- An integrity check runs after every save.
- Long operations (open, save, discard, new mod, language switch) show a busy overlay with the current step, so you can see when they are done.
- The editor warns if the game is running. Close the game before editing.
- Mod changes only apply to **new** careers; a save game keeps its own copy of the data.
- Serialized blobs and very long values are shown read-only and never written back.

## Requirements

- Windows 10/11
- [.NET 8 SDK](https://dotnet.microsoft.com/download) to build and run
- Pro Basketball Manager 2025 (Steam), for the default data used by "New mod"

## Build and run

```
dotnet run --project src/PbmEditor.App
```

Run the tests:

```
dotnet test tests/PbmEditor.Tests
```

Optional: set `PBM_TEST_DB` to a copy of a real database to run the smoke tests against it. They never write permanently.

## Where the data lives

| What | Location |
|---|---|
| Local mods | `%USERPROFILE%\AppData\LocalLow\Umix Studios\Pro Basketball Manager 2025\Mods\<name>\` |
| Save games | `...\Pro Basketball Manager 2025\Saves\` |
| Default game data | `<Steam>\steamapps\common\Pro Basketball Manager 2025\PBM2025_Data\Datas\` |
| Images in a mod | `team_logos`, `player_faces`, `coach_faces`, `league_logos`, `flags`, `jerseys`, `arenas` (files named by ID) |

Jerseys: the game loads `jerseys/<teamId>_HOME.png` or `_AWAY.png` (one picture per kit). Without the file it draws a default kit (front and back) from the team colours.

## Project layout

- `src/PbmEditor.Core`: UI-independent library. Database access with lazy transaction, schema-driven row and table editors, game logic (moving players, contracts, finance), mod creation, name codec, translations.
- `src/PbmEditor.App`: WPF application (MVVM-style view models).
- `tests/PbmEditor.Tests`: xUnit tests.

The editor builds its forms from the live schema (`PRAGMA table_info`). Internal columns without a field definition are hidden, and missing columns or tables are skipped, so it should keep working with newer game versions as long as the database stays SQLite.

## Translations

The English text is the key. Translations are in `src/PbmEditor.Core/translations.tsv` (English, German, French per line; placeholders such as `{0}` must match, which a test checks). In XAML use `{loc:T 'English text'}`, in code `Loc.T(...)` or `Loc.F(...)`. A missing entry falls back to English. Team, player and league names come from the game data and are not translated.

## Known limitations

- Creating a whole new competition from scratch is not supported yet. Edit existing ones, or add rows in the phase tables.
- How the game renders appearance values (skin, hair, beard) and custom jersey images is only partly verified.
- The season cost estimate "13 x monthly payroll" in the finance line is an unverified assumption.
- New players: attributes, market value and salary are estimates fitted on the default database. A new player has no statistics history, and how the game reacts to players added this way is not verified in the game yet.
- Windows only (WPF).

## License

[MIT](LICENSE). The game, its data and its images belong to Umix Studios and are not part of this repository.

The only third-party dependency is [Microsoft.Data.Sqlite](https://www.nuget.org/packages/Microsoft.Data.Sqlite) (MIT).

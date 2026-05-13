# Win.Codex.ProfileSwitch

Win.Codex.ProfileSwitch is a small Windows tray utility for switching Codex Desktop profiles while keeping one shared `%USERPROFILE%\.codex` session pool.

It switches only the active `auth.json` and `config.toml` files. It does not move, copy, split, or manage Codex sessions.

## Features

- Runs as a Windows tray app
- Creates a profile from the current `%USERPROFILE%\.codex\auth.json` and `config.toml`
- Imports paired `auth*.json` and `config*.toml` files from `%USERPROFILE%\.codex`
- Switches profiles from the tray menu or management window
- Renames profiles
- Opens the profiles directory and selected profile files
- Backs up replaced `auth.json` and `config.toml` files before switching
- Attempts to restart the Codex Windows client after a profile switch

## Technology Stack

- C#
- .NET 10
- Windows Forms
- Windows tray icon via `NotifyIcon`
- File-based profile switching with `auth.json` and `config.toml`

## Usage

1. Make sure Codex Desktop has already created `%USERPROFILE%\.codex\auth.json` and `%USERPROFILE%\.codex\config.toml`.
2. Run Win.Codex.ProfileSwitch.
3. Double-click the tray icon to open the management window, or right-click the tray icon to use the quick menu.
4. Click `Create Profile From Current Config` to save the current Codex files as a reusable profile.
5. Add more profiles by placing folders under `%USERPROFILE%\.codex\profiles\`, each with its own `auth.json` and `config.toml`.
6. Select a complete profile and switch to it from the tray menu or management window.
7. Start a new Codex session, or restart the Codex client from the tray menu, so the newly written files are used.

You can also import existing paired files from `%USERPROFILE%\.codex`. For example:

```text
auth.json + config.toml -> default
auth-work.json + config-work.toml -> work
auth-lab.json + config-lab.toml -> lab
```

## Data Locations

Profiles are stored under:

```text
%USERPROFILE%\.codex\profiles\
```

Each profile contains:

```text
auth.json
config.toml
```

The active Codex files are:

```text
%USERPROFILE%\.codex\auth.json
%USERPROFILE%\.codex\config.toml
```

Backups are written to:

```text
%USERPROFILE%\.codex\win-codex-profile-switch\backups\
```

Codex session history remains in the original shared locations:

```text
%USERPROFILE%\.codex\sessions
%USERPROFILE%\.codex\archived_sessions
```

## Build

Requirements:

- Windows
- .NET SDK with Windows Forms support

Build:

```powershell
dotnet build
```

Run:

```powershell
dotnet run
```

Publish a single-file Windows executable:

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

## Scope

This repository does not include personal Codex configuration, API keys, OAuth tokens, local session history, build output, or machine-specific project settings.

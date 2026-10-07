# Rubbish

Unity project for Rubbish. Open this repository root in Unity Hub with **Unity 6000.3.25f1** (see `ProjectSettings/ProjectVersion.txt`).

## First checkout

1. Install Git LFS and run `git lfs install` once on your machine.
2. Clone the repository, then run `git lfs pull` in the clone if Git did not fetch the art automatically.
3. Open the repository root in Unity Hub. Unity restores packages from `Packages/manifest.json` and regenerates `Library/` locally.
4. Open `Assets/Scenes/Bootstrap.unity` to run the game. For Windows playtests, use **Rubbish → Build → Windows Playtest (zip for friends)**; see [Docs/WindowsBuild.md](Docs/WindowsBuild.md).

## Repository layout

| Path | Purpose |
| --- | --- |
| `Assets/` | Game scenes, code, prefabs, art, tests and each asset's matching `.meta` file. |
| `Packages/` | Package manifest and lockfile for reproducible dependencies. |
| `ProjectSettings/` | Shared Unity project and build settings. |
| `Docs/` | Design and technical documentation. |
| `Tools/` | Development, validation and playtest scripts. |

Commit `.meta` files alongside their assets. Unity serializes scenes and prefabs as text so changes can be reviewed and merged. `Library/`, `Temp/`, `Logs/`, `UserSettings/` and `Builds/` are generated or personal and stay out of Git. The existing Plastic workspace is left on disk but is not part of the Git repository.

Binary source art uses Git LFS; collaborators need Git LFS installed before working with those files. If you add a new binary asset type, update `.gitattributes` before committing it. Do not commit local save data, credentials or exported playtest builds.

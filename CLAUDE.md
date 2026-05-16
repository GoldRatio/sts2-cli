# CLAUDE.md

## Testing Requirements

Any code change MUST pass a full regression test before claiming completion:

```bash
# Run 5 games per character, ALL must complete (0 crashes/stuck)
for char in Ironclad Silent Defect Regent Necrobinder; do
    STS2_GAME_DIR="$HOME/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64" python3 python/play_full_run.py 5 "$char" 2>&1 | grep -E "Wins|Completed"
do
```

Expected: `Completed: 5/5` for every character.

## Localization

- Source strings from `localization_eng/` (game's official English translations)
- Template variables like `{Damage}`, `{Block}`, `{MaxHp}` must be resolved to actual values before display

## Build & Setup

```bash
# Full environment setup (cross-platform)
python3 python/setup.py

# Rebuild only the headless project
dotnet build src/Sts2Headless/Sts2Headless.csproj
```

## Key Architecture

- `src/Sts2Headless/RunSimulator.cs` — game lifecycle, decision point detection, state serialization
- `src/Sts2Headless/Program.cs` — JSON command router
- `src/GodotStubs/` — replacement GodotSharp.dll (no-op Godot types)
- `python/play.py` — interactive terminal player
- `python/play_full_run.py` — batch testing tool
- `lib/` — game DLLs (not in repo, copied by setup.sh)
- `localization_eng/` — official English loc data

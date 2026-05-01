# sts2-cli

<details open>
<summary><b>English</b></summary>

Forked from [wuhao21/sts2-cli](https://github.com/wuhao21/sts2-cli). This fork keeps the original headless CLI core but aims to add RL agent capability and fixing some bugs that exist within the original repository for that purpose.

## Original Readme Start

A CLI for Slay the Spire 2.

Runs the real game engine headless in your terminal — all damage, card effects, enemy AI, relics, and RNG are identical to the actual game. Everything is unlocked from the start: all characters, cards, relics, potions, and ascension levels — no timeline progression required.

![demo](docs/demo_en.gif)

## Setup

Requirements:
- [Slay the Spire 2](https://store.steampowered.com/app/2868840/Slay_the_Spire_2/) on Steam
- [.NET 9+ SDK](https://dotnet.microsoft.com/download)
- Python 3.9+

```bash
git clone https://github.com/wuhao21/sts2-cli.git
cd sts2-cli
./setup.sh      # copies DLLs from Steam → IL patches → builds
```

Or just run `python3 python/play.py` — it auto-detects and sets up on first run.

## Play

```bash
python3 python/play.py                        # interactive (Chinese)
python3 python/play.py --lang en              # interactive (English)
python3 python/play.py --ascension 10         # Ascension 10
python3 python/play.py --character Silent      # play as Silent
```

Type `help` in-game:

```
  help     — show help
  map      — show map
  deck     — show deck
  potions  — show potions
  relics   — show relics
  quit     — quit

  Map:     enter path number (0, 1, 2)
  Combat:  card index / e (end turn) / p0 (use potion)
  Reward:  card index / s (skip)
  Rest:    option index
  Event:   option index / leave
  Shop:    c0 (card) / r0 (relic) / p0 (potion) / rm (remove) / leave
```

## JSON Protocol

For programmatic control (AI agents, RL, etc.), communicate via stdin/stdout JSON:

```bash
dotnet run --project src/Sts2Headless/Sts2Headless.csproj
```

```json
{"cmd": "start_run", "character": "Ironclad", "seed": "test", "ascension": 0}
{"cmd": "action", "action": "play_card", "args": {"card_index": 0, "target_index": 0}}
{"cmd": "action", "action": "end_turn"}
{"cmd": "action", "action": "select_map_node", "args": {"col": 3, "row": 1}}
{"cmd": "action", "action": "skip_card_reward"}
{"cmd": "quit"}
```

Each command returns a JSON decision point (`map_select` / `combat_play` / `card_reward` / `rest_site` / `event_choice` / `shop` / `game_over`). All names are in English.

## Game Logs

Every run is automatically logged to `logs/` as a JSONL file (one JSON per line), recording each game state and action with timestamps. Logs older than 7 days are cleaned up automatically.

```bash
python3 python/play.py --no-log    # disable logging
```

**When filing a bug report, please attach the relevant log file from `logs/`** — it contains the full step-by-step game state needed to reproduce the issue.

## Supported Characters

| Character | Status |
|---|---|
| Ironclad | Fully playable |
| Silent | Fully playable |
| Defect | Fully playable |
| Necrobinder | Fully playable |
| Regent | Fully playable |

## Architecture

```
Your code (Python / JS / LLM)
    │  JSON stdin/stdout
    ▼
src/Sts2Headless (C#)
    │  RunSimulator.cs
    ▼
sts2.dll (game engine, IL patched)
  + src/GodotStubs (replaces GodotSharp.dll)
  + Harmony patches (localization)
```

</details>

## Known Issues
- Fights such as **Bygone Effigy** will freeze the game. This is likely due to some unhandled behaviour for the speech bubble system in the original game.
- Multi-step events where don't continue after first step. ie. **Tablet Of Truth** only allows for the first choice.
- **Bygone Effigy** doesn't stack slow after each card played. It may do this internally but doesn't display, need to check numbers explicitly
- When selecting things like upgrade in **Rest Sites**, it allows you to multi select cards. Similar to the bug in actual gameplay funnily enough. 

## Planned Changes
- Add support for console commands to allow for easier debugging.
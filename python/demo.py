#!/usr/bin/env python3
"""Demo script — auto-plays a few turns with delays for recording."""
import json
import subprocess
import sys
import os
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(ROOT, "python"))

# Set language
os.environ["STS2_GAME_DIR"] = os.path.expanduser(
    "~/Library/Application Support/Steam/steamapps/common/"
    "Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
)

import play as P
P.LANG = sys.argv[1] if len(sys.argv) > 1 else "zh"

DOTNET = P.DOTNET or os.path.expanduser("~/.dotnet-arm64/dotnet")
PROJECT = P.PROJECT

DELAY = 1.5  # seconds between actions for visual effect

proc = subprocess.Popen(
    [DOTNET, "run", "--no-build", "--project", PROJECT],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
    text=True, bufsize=1)

def read():
    while True:
        l = proc.stdout.readline().strip()
        if not l: return None
        if l.startswith("{"): return json.loads(l)

def send(cmd):
    proc.stdin.write(json.dumps(cmd) + "\n")
    proc.stdin.flush()
    return read()

def pause(s=DELAY):
    time.sleep(s)

try:
    print(P.c("═" * 60, "bold"))
    print(P.c(f"  {P.t('Slay the Spire 2 — Headless CLI Demo', '杀戮尖塔 2 — 终端版 Demo')}", "bold"))
    print(P.c("═" * 60, "bold"))
    pause(1.0)

    ready = read()
    state = send({"cmd": "start_run", "character": "Ironclad", "seed": "demo_2024"})

    step = 0
    max_steps = 35  # Show ~35 actions

    while step < max_steps and state:
        step += 1
        dec = state.get("decision", "")

        if dec == "game_over":
            victory = state.get("victory", False)
            p = state.get("player", {})
            print(f"\n{'═' * 60}")
            if victory:
                print(f"  {P.c(P.t('VICTORY!','胜利!'), 'green')}")
            else:
                print(f"  {P.c(P.t('DEFEAT','战败'), 'red')} Act {state.get('act')}, Floor {state.get('floor')}")
            P.show_player(p)
            print(f"{'═' * 60}")
            break

        elif dec == "event_choice":
            P.show_event(state)
            pause()
            opts = [o for o in state.get("options", []) if not o.get("is_locked")]
            if opts:
                pick = opts[0]
                print(f"\n{P.c('>', 'green')} {P.t('Choose','选择')} [{pick['index']}]: {P.c(str(pick['index']), 'yellow')}")
                pause()
                state = send({"cmd": "action", "action": "choose_option",
                             "args": {"option_index": pick["index"]}})
            else:
                state = send({"cmd": "action", "action": "leave_room"})

        elif dec == "map_select":
            # Show map
            map_data = send({"cmd": "get_map"})
            choices = state.get("choices", [])
            choice_set = {(ch["col"], ch["row"]) for ch in choices}
            P._render_map(map_data, choice_set)

            type_icons = {"Monster": "⚔", "Elite": "💀", "Boss": "👹",
                          "RestSite": "🏕", "Shop": "🏪", "Treasure": "💎",
                          "Event": "❓", "Unknown": "❓", "Ancient": "🏛"}
            print(f"  {P.c(P.t('Available paths:','可选路径:'), 'bold')}")
            for i, ch in enumerate(choices):
                icon = type_icons.get(ch["type"], "?")
                ntype = P.t(ch["type"], P.NODE_TYPE_ZH.get(ch["type"], ch["type"]))
                print(f"    [{i}] {P.c(icon, 'yellow')} {ntype}")

            pause()
            pick = choices[0]
            print(f"\n{P.c('>', 'green')} {P.t('Choose path:','选择路径:')} {P.c('0', 'yellow')}")
            pause()
            state = send({"cmd": "action", "action": "select_map_node",
                         "args": {"col": pick["col"], "row": pick["row"]}})

        elif dec == "combat_play":
            P.show_combat(state)
            pause(1.0)

            hand = state.get("hand", [])
            energy = state.get("energy", 0)
            playable = [c for c in hand if c.get("can_play") and c.get("cost", 99) <= energy]

            if playable:
                card = playable[0]
                args = {"card_index": card["index"]}
                if card.get("target_type") == "AnyEnemy":
                    enemies = state.get("enemies", [])
                    if enemies:
                        args["target_index"] = 0
                action_label = f"{card['index']}"
                if card.get("target_type") == "AnyEnemy":
                    action_label += f" → 0"
                print(f"\n{P.c('>', 'green')} {P.t('Play:','出牌:')} {P.c(action_label, 'yellow')}")
                pause(0.5)
                state = send({"cmd": "action", "action": "play_card", "args": args})
            else:
                print(f"\n{P.c('>', 'green')} {P.c('e', 'yellow')} ({P.t('end turn','结束回合')})")
                pause(0.5)
                state = send({"cmd": "action", "action": "end_turn"})

        elif dec == "card_reward":
            P.show_card_reward(state)
            pause()
            cards = state.get("cards", [])
            if cards:
                print(f"\n{P.c('>', 'green')} {P.t('Pick card:','选择卡牌:')} {P.c('0', 'yellow')}")
                pause()
                state = send({"cmd": "action", "action": "select_card_reward",
                             "args": {"card_index": 0}})
            else:
                state = send({"cmd": "action", "action": "skip_card_reward"})

        elif dec == "rest_site":
            P.show_rest_site(state)
            pause()
            opts = [o for o in state.get("options", []) if o.get("is_enabled")]
            if opts:
                print(f"\n{P.c('>', 'green')} 选择: {P.c(str(opts[0]['index']), 'yellow')}")
                pause()
                state = send({"cmd": "action", "action": "choose_option",
                             "args": {"option_index": opts[0]["index"]}})
                if state and state.get("type") == "error":
                    state = send({"cmd": "action", "action": "leave_room"})
            else:
                state = send({"cmd": "action", "action": "leave_room"})

        elif dec == "card_select":
            cards = state.get("cards", [])
            if cards:
                state = send({"cmd": "action", "action": "select_cards", "args": {"indices": "0"}})
            else:
                state = send({"cmd": "action", "action": "skip_select"})

        elif dec == "bundle_select":
            state = send({"cmd": "action", "action": "select_bundle", "args": {"bundle_index": 0}})

        elif dec == "shop":
            P.show_shop(state)
            pause()
            print(f"\n{P.c('>', 'green')} leave")
            pause()
            state = send({"cmd": "action", "action": "leave_room"})

        else:
            state = send({"cmd": "action", "action": "proceed"})

    print(f"\n{P.c('— Demo End —', 'dim')}")

finally:
    try:
        proc.stdin.write(json.dumps({"cmd": "quit"}) + "\n")
        proc.stdin.flush()
    except: pass
    try:
        proc.terminate()
        proc.wait(timeout=5)
    except:
        proc.kill()

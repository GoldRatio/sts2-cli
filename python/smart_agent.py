#!/usr/bin/env python3
"""
Smart agent for sts2-cli — goal: beat Ascension 0.

Strategy:
- Prioritize strength scaling (Inflame, Demon Form, Feed)
- Use Bash for Vulnerable, then hit hard
- Defend when enemy is attacking and HP is low
- Pick Powers > Rare > AOE > damage cards
- Heal at rest when HP < 70%, otherwise upgrade
- Buy strong relics from shop
- Skip bad cards (keep deck small)
"""

import json
import subprocess
import sys
import os
import random

DOTNET = os.path.expanduser("~/.dotnet-arm64/dotnet")
PROJECT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                       "Sts2Headless", "Sts2Headless.csproj")

# Card tier list for pick decisions
ALWAYS_PICK = {
    "Demon Form", "Barricade", "Impervious", "Feed", "Offering",
    "Reaper Form", "Corruption", "Limit Break",
}
GOOD_PICKS = {
    "Inflame", "Feel No Pain", "Battle Trance", "Whirlwind",
    "Hemokinesis", "Shrug It Off", "Burning Pact", "Pommel Strike",
    "Thunderclap", "Flame Barrier", "Armaments", "Bludgeon",
    "Iron Wave", "Uppercut", "Fiend Fire", "Sword Boomerang",
    "Dark Embrace", "Brutality", "Rampage", "Body Slam",
    "Second Wind", "True Grit", "Metallicize", "Evolve",
}
SKIP = {
    "Anger", "Clash", "Havoc", "Flex", "Warcry", "Cleave",
}


def play_game(seed, verbose=True):
    proc = subprocess.Popen(
        [DOTNET, "run", "--no-build", "--project", PROJECT],
        stdin=subprocess.PIPE, stdout=subprocess.PIPE,
        stderr=subprocess.PIPE, text=True, bufsize=1)

    def read():
        while True:
            l = proc.stdout.readline().strip()
            if not l: return None
            if l.startswith("{"): return json.loads(l)

    def send(cmd):
        proc.stdin.write(json.dumps(cmd) + "\n")
        proc.stdin.flush()
        return read()

    def nm(o):
        if isinstance(o, dict) and "en" in o:
            return o["en"]
        return str(o)

    try:
        ready = read()
        state = send({"cmd": "start_run", "character": "Ironclad", "seed": seed})

        step = 0
        prev_key = None
        stuck = 0

        while step < 500:
            step += 1
            if not state:
                break

            if state.get("type") == "error":
                state = send({"cmd": "action", "action": "proceed"})
                continue

            dec = state.get("decision", "")
            p = state.get("player", {})
            hp = p.get("hp", 0)
            mhp = p.get("max_hp", 1)
            gold = p.get("gold", 0)
            deck_size = p.get("deck_size", 10)

            # Stuck detection
            key = f"{dec}:{state.get('round')}:{hp}:{len(state.get('hand',[]))}:{state.get('energy',0)}"
            if key == prev_key:
                stuck += 1
                if stuck > 20:
                    if verbose: print(f"  STUCK at step {step}")
                    break
            else:
                stuck = 0
                prev_key = key

            if dec == "game_over":
                victory = state.get("victory", False)
                act = state.get("act", "?")
                floor = state.get("floor", "?")
                relics = [nm(r) for r in p.get("relics", [])]
                if verbose:
                    status = "🏆 VICTORY!" if victory else "💀 DEFEAT"
                    print(f"\n{status} Act {act} Floor {floor} | HP {hp}/{mhp} | Gold {gold} | Deck {deck_size}")
                    print(f"  Relics: {', '.join(relics)}")
                return {"victory": victory, "act": act, "floor": floor, "hp": hp,
                        "gold": gold, "deck": deck_size, "seed": seed, "steps": step}

            elif dec == "map_select":
                choices = state.get("choices", [])
                hp_ratio = hp / max(mhp, 1)
                floor = state.get("floor", 0)

                # PATHING STRATEGY:
                # - Low HP (<40%): rest > treasure > shop > unknown > monster > elite
                # - Med HP (40-75%): monster > unknown > shop > rest > treasure > elite
                # - High HP (>75%): elite > monster > unknown > shop > treasure > rest
                # - Boss always priority 0
                if hp_ratio < 0.4:
                    prio = {"RestSite": 0, "Treasure": 1, "Shop": 2, "Unknown": 3, "Monster": 4, "Elite": 5, "Boss": 0, "Ancient": 3}
                elif hp_ratio < 0.75:
                    prio = {"Monster": 1, "Unknown": 2, "Shop": 3, "RestSite": 4, "Treasure": 5, "Elite": 6, "Boss": 0, "Ancient": 3}
                else:
                    prio = {"Elite": 0, "Monster": 1, "Unknown": 2, "Shop": 3, "Treasure": 4, "RestSite": 5, "Boss": 0, "Ancient": 3}

                choices.sort(key=lambda c: prio.get(c.get("type", ""), 99))
                ch = choices[0]
                if verbose:
                    act_n = nm(state.get("act_name", "?"))
                    types = [c["type"] for c in choices]
                    print(f"\n{act_n} F{floor} HP={hp}/{mhp} G={gold} D={deck_size} [{','.join(types)}] → {ch['type']}")

                state = send({"cmd": "action", "action": "select_map_node",
                             "args": {"col": ch["col"], "row": ch["row"]}})

            elif dec == "combat_play":
                hand = state.get("hand", [])
                enemies = state.get("enemies", [])
                energy = state.get("energy", 0)
                rnd = state.get("round", 0)
                block = p.get("block", 0)

                if rnd == 1 and energy == state.get("max_energy", 3):
                    en_str = "+".join(f"{nm(e['name'])}{e['hp']}hp" for e in enemies)
                    if verbose: print(f"  ⚔ vs {en_str}")

                playable = [c for c in hand if c.get("can_play") and c.get("cost", 99) <= energy]

                if playable:
                    attacks = [c for c in playable if c.get("type") == "Attack"]
                    defends = [c for c in playable if c.get("type") == "Skill"]
                    powers = [c for c in playable if c.get("type") == "Power"]

                    total_enemy_hp = sum(e.get("hp", 0) for e in enemies)
                    any_attacking = any(e.get("intends_attack") for e in enemies)
                    hp_ratio = hp / max(mhp, 1)

                    card = None

                    # 1. Always play Powers first (they persist)
                    if powers:
                        card = powers[0]
                    # 2. Use Bash early for Vulnerable
                    elif rnd <= 2 and not card:
                        bash = next((c for c in attacks if "vulnerablepower" in (c.get("stats") or {})), None)
                        if bash:
                            card = bash
                    # 3. If enemy attacking and we're low, defend
                    if not card and any_attacking and hp_ratio < 0.4 and defends:
                        card = defends[0]
                    # 4. Play highest damage attack
                    if not card and attacks:
                        card = max(attacks, key=lambda c: (c.get("stats") or {}).get("damage", 0))
                    # 5. Play remaining skills
                    if not card and defends:
                        card = defends[0]
                    # 6. Any playable card
                    if not card:
                        card = playable[0]

                    args = {"card_index": card["index"]}
                    if card.get("target_type") == "AnyEnemy" and enemies:
                        # Target lowest HP enemy to kill faster (reduce incoming damage)
                        args["target_index"] = min(enemies, key=lambda e: e.get("hp", 999))["index"]

                    state = send({"cmd": "action", "action": "play_card", "args": args})
                else:
                    if verbose and rnd >= 1:
                        print(f"  R{rnd} HP={hp} blk={block} → end")
                    state = send({"cmd": "action", "action": "end_turn"})

            elif dec == "card_reward":
                cards = state.get("cards", [])
                if not cards:
                    state = send({"cmd": "action", "action": "skip_card_reward"})
                    continue

                # CARD PICK STRATEGY:
                # Always pick S-tier, usually pick A-tier, skip bloat
                best = None
                best_score = -1

                for c in cards:
                    name = nm(c.get("name", {}))
                    rarity = c.get("rarity", "Common")
                    ctype = c.get("type", "")
                    score = 0

                    if name in ALWAYS_PICK:
                        score = 100
                    elif name in GOOD_PICKS:
                        score = 50
                    elif name in SKIP:
                        score = -10
                    elif ctype == "Power":
                        score = 60
                    elif rarity == "Rare":
                        score = 40
                    elif rarity == "Uncommon":
                        score = 20
                    elif ctype == "Attack":
                        dmg = (c.get("stats") or {}).get("damage", 0)
                        score = 10 + dmg
                    else:
                        score = 5

                    if score > best_score:
                        best_score = score
                        best = c

                # Skip if deck is getting bloated (>18 cards) and card isn't great
                if deck_size > 18 and best_score < 40:
                    if verbose:
                        names = [nm(c["name"]) for c in cards]
                        print(f"  🃏 Skip (deck={deck_size}): [{', '.join(names)}]")
                    state = send({"cmd": "action", "action": "skip_card_reward"})
                else:
                    if verbose:
                        names = [nm(c["name"]) for c in cards]
                        print(f"  🃏 [{', '.join(names)}] → {nm(best['name'])}")
                    state = send({"cmd": "action", "action": "select_card_reward",
                                 "args": {"card_index": best["index"]}})

            elif dec == "rest_site":
                opts = state.get("options", [])
                enabled = [o for o in opts if o.get("is_enabled")]
                heal = next((o for o in enabled if o.get("option_id") == "HEAL"), None)
                smith = next((o for o in enabled if o.get("option_id") == "SMITH"), None)

                hp_ratio = hp / max(mhp, 1)
                # Heal if HP < 70%, otherwise upgrade
                if hp_ratio < 0.70 and heal:
                    if verbose: print(f"  🏕 Heal (HP {hp}/{mhp})")
                    choice = heal
                elif smith:
                    if verbose: print(f"  🏕 Smith")
                    choice = smith
                elif heal:
                    if verbose: print(f"  🏕 Heal")
                    choice = heal
                else:
                    choice = enabled[0] if enabled else None

                if choice:
                    state = send({"cmd": "action", "action": "choose_option",
                                 "args": {"option_index": choice["index"]}})
                    if state and state.get("type") == "error":
                        state = send({"cmd": "action", "action": "leave_room"})
                else:
                    state = send({"cmd": "action", "action": "leave_room"})

            elif dec == "event_choice":
                opts = [o for o in state.get("options", []) if not o.get("is_locked")]
                ename = state.get("event_name", "?")
                if verbose: print(f"  📜 {ename}")
                if opts:
                    state = send({"cmd": "action", "action": "choose_option",
                                 "args": {"option_index": opts[0]["index"]}})
                    if state and state.get("type") == "error":
                        state = send({"cmd": "action", "action": "leave_room"})
                else:
                    state = send({"cmd": "action", "action": "leave_room"})

            elif dec == "shop":
                # BUY STRATEGY: buy one relic or potion, then leave
                relics_list = state.get("relics", [])
                potions_list = state.get("potions", [])

                bought = False
                # Buy cheapest relic if affordable and < 150g (one only)
                stocked_relics = [r for r in relics_list if r.get("is_stocked") and r.get("cost", 999) <= gold and r.get("cost", 999) <= 150]
                if stocked_relics and not bought:
                    cheapest = min(stocked_relics, key=lambda r: r.get("cost", 999))
                    if verbose: print(f"  🏪 Buy relic {nm(cheapest['name'])} ({cheapest['cost']}g)")
                    state = send({"cmd": "action", "action": "buy_relic",
                                 "args": {"relic_index": cheapest["index"]}})
                    bought = True
                    # After buying, just leave (don't loop)

                if not bought:
                    # Buy one cheap potion (max 1)
                    stocked_potions = [p for p in potions_list if p.get("is_stocked") and p.get("cost", 999) <= gold and p.get("cost", 999) <= 75]
                    if stocked_potions:
                        cheapest = min(stocked_potions, key=lambda p: p.get("cost", 999))
                        if verbose: print(f"  🏪 Buy potion {nm(cheapest['name'])} ({cheapest['cost']}g)")
                        state = send({"cmd": "action", "action": "buy_potion",
                                     "args": {"potion_index": cheapest["index"]}})
                        bought = True

                # Always leave after one purchase (or none)
                if bought and state and state.get("decision") == "shop":
                    state = send({"cmd": "action", "action": "leave_room"})
                elif not bought:
                    if verbose: print(f"  🏪 Leave (G={gold})")
                    state = send({"cmd": "action", "action": "leave_room"})

            else:
                state = send({"cmd": "action", "action": "proceed"})

        return {"victory": False, "seed": seed, "steps": step, "stuck": True}

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


def main():
    num = int(sys.argv[1]) if len(sys.argv) > 1 else 100
    verbose = "--verbose" in sys.argv or "-v" in sys.argv or num <= 5

    wins = 0
    total = 0
    floors = []

    for i in range(num):
        seed = f"smart_{i+1}"
        if verbose or (i+1) % 20 == 0:
            print(f"\n--- Game {i+1}/{num} (seed: {seed}) ---")

        result = play_game(seed, verbose=verbose)
        total += 1

        if result.get("victory"):
            wins += 1
            print(f"  🏆 WIN! Act {result['act']} Floor {result['floor']}")

        if result.get("floor"):
            floors.append(result["floor"])

    print(f"\n{'='*50}")
    print(f"Results: {wins}/{total} wins ({wins*100//max(total,1)}%)")
    if floors:
        print(f"Avg floor: {sum(floors)/len(floors):.1f}, Max: {max(floors)}")


if __name__ == "__main__":
    main()

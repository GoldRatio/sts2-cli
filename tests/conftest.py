"""Pytest fixtures: Game process wrapper for unit tests."""

import json
import os
import subprocess
import pytest

DOTNET = os.path.expanduser("~/.dotnet-arm64/dotnet")
PROJECT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))),
                       "src", "Sts2Headless", "Sts2Headless.csproj")


class Game:
    """Wraps the headless C# process for testing."""

    def __init__(self):
        env = os.environ.copy()
        env.setdefault("STS2_GAME_DIR",
                       os.path.expanduser("~/Library/Application Support/Steam/steamapps/common/"
                                          "Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/"
                                          "data_sts2_macos_arm64"))
        self.proc = subprocess.Popen(
            [DOTNET, "run", "--no-build", "--project", PROJECT],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
            text=True, bufsize=1, env=env,
        )
        ready = self._read()
        assert ready.get("type") == "ready", f"Expected ready, got: {ready}"

    def _read(self):
        while True:
            line = self.proc.stdout.readline().strip()
            if not line:
                raise RuntimeError("EOF from game process")
            if line.startswith("{"):
                return json.loads(line)

    def send(self, cmd):
        self.proc.stdin.write(json.dumps(cmd) + "\n")
        self.proc.stdin.flush()
        return self._read()

    def start(self, character="Ironclad", seed="test", ascension=0, lang="en"):
        return self.send({"cmd": "start_run", "character": character,
                          "seed": seed, "ascension": ascension, "lang": lang})

    def act(self, action, **args):
        cmd = {"cmd": "action", "action": action}
        if args:
            cmd["args"] = args
        return self.send(cmd)

    def get_map(self):
        return self.send({"cmd": "get_map"})

    def close(self):
        try:
            self.proc.stdin.write('{"cmd":"quit"}\n')
            self.proc.stdin.flush()
        except Exception:
            pass
        try:
            self.proc.terminate()
            self.proc.wait(timeout=5)
        except Exception:
            self.proc.kill()

    # --- Auto-play helpers ---

    def auto_combat(self, state):
        """Play one card or end turn. Returns next state."""
        hand = state.get("hand", [])
        energy = state.get("energy", 0)
        playable = [c for c in hand if c.get("can_play") and c.get("cost", 99) <= energy]
        if playable:
            card = playable[0]
            args = {"card_index": card["index"]}
            if card.get("target_type") == "AnyEnemy":
                enemies = state.get("enemies", [])
                if enemies:
                    args["target_index"] = enemies[0]["index"]
            return self.act("play_card", **args)
        return self.act("end_turn")

    def play_to_decision(self, state, target, max_steps=500):
        """Auto-play until reaching a specific decision type.

        Returns the state at the target decision.
        Raises RuntimeError if game_over or max_steps exceeded.
        """
        for _ in range(max_steps):
            dec = state.get("decision", "")
            if dec == target:
                return state
            if dec == "game_over":
                raise RuntimeError(f"Game ended before reaching '{target}'")
            if state.get("type") == "error":
                state = self.act("proceed")
                continue

            if dec == "event_choice":
                opts = [o for o in state.get("options", []) if not o.get("is_locked")]
                state = self.act("choose_option", option_index=opts[0]["index"]) if opts else self.act("leave_room")
            elif dec == "map_select":
                ch = state["choices"][0]
                state = self.act("select_map_node", col=ch["col"], row=ch["row"])
            elif dec == "combat_play":
                state = self.auto_combat(state)
            elif dec == "card_reward":
                state = self.act("skip_card_reward")
            elif dec == "bundle_select":
                state = self.act("select_bundle", bundle_index=0)
            elif dec == "card_select":
                if state.get("min_select", 0) == 0:
                    state = self.act("skip_select")
                else:
                    state = self.act("select_cards", indices="0")
            elif dec == "rest_site":
                opts = [o for o in state.get("options", []) if o.get("is_enabled")]
                state = self.act("choose_option", option_index=opts[0]["index"])
            elif dec == "shop":
                state = self.act("leave_room")
            else:
                state = self.act("proceed")
        raise RuntimeError(f"Did not reach '{target}' in {max_steps} steps")

    def play_to_decision_via(self, state, target, prefer_node=None):
        """Like play_to_decision but prefers a specific map node type."""
        for _ in range(500):
            dec = state.get("decision", "")
            if dec == target:
                return state
            if dec == "game_over":
                raise RuntimeError(f"Game ended before reaching '{target}'")
            if state.get("type") == "error":
                state = self.act("proceed")
                continue

            if dec == "map_select" and prefer_node:
                choices = state["choices"]
                pick = next((c for c in choices if c["type"] == prefer_node), choices[0])
                state = self.act("select_map_node", col=pick["col"], row=pick["row"])
            elif dec == "map_select":
                ch = state["choices"][0]
                state = self.act("select_map_node", col=ch["col"], row=ch["row"])
            elif dec == "combat_play":
                state = self.auto_combat(state)
            elif dec == "event_choice":
                opts = [o for o in state.get("options", []) if not o.get("is_locked")]
                state = self.act("choose_option", option_index=opts[0]["index"]) if opts else self.act("leave_room")
            elif dec == "card_reward":
                state = self.act("skip_card_reward")
            elif dec == "bundle_select":
                state = self.act("select_bundle", bundle_index=0)
            elif dec == "card_select":
                if state.get("min_select", 0) == 0:
                    state = self.act("skip_select")
                else:
                    state = self.act("select_cards", indices="0")
            elif dec == "rest_site":
                opts = [o for o in state.get("options", []) if o.get("is_enabled")]
                state = self.act("choose_option", option_index=opts[0]["index"])
            elif dec == "shop":
                if target != "shop":
                    state = self.act("leave_room")
                else:
                    return state
            else:
                state = self.act("proceed")
        raise RuntimeError(f"Did not reach '{target}' in 500 steps")


@pytest.fixture
def game():
    """Each test gets an independent game process."""
    g = Game()
    yield g
    g.close()

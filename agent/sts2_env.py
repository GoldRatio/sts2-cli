import os
import json
import subprocess
import threading
import gymnasium as gym
import numpy as np
from gymnasium import spaces
import uuid

class Sts2Env(gym.Env):
    """
    Gymnasium Environment for Slay the Spire 2 Headless.
    Supports a generalized Discrete action space with Action Masking.
    Provides dense reward signals in the `info` dict for hierarchical RL.
    """
    metadata = {"render_modes": ["human"]}

    MAX_HAND = 10
    MAX_ENEMIES = 5
    MAX_CHOICES = 20

    ACTION_END_TURN = 0
    ACTION_PLAY_CARD_START = 1
    ACTION_PLAY_CARD_END = 100
    ACTION_USE_POTION_START = 101
    ACTION_USE_POTION_END = 150
    ACTION_CHOICE_START = 151
    ACTION_CHOICE_END = 170
    ACTION_SHOP_BUY_START = 171
    ACTION_SHOP_BUY_END = 190
    ACTION_SHOP_REMOVE = 191
    ACTION_SKIP_REWARD = 192
    
    TOTAL_ACTIONS = 193

    def __init__(self, character="Necrobinder"):
        super().__init__()
        self.character = character
        self.proc = None
        self.lock = threading.Lock()
        
        # Define Action Space
        self.action_space = spaces.Discrete(self.TOTAL_ACTIONS)
        
        # Define Observation Space
        self.observation_space = spaces.Dict({
            "global": spaces.Box(low=-1000, high=10000, shape=(7,), dtype=np.float32), # hp, max_hp, energy, block, gold, floor, act
            "hand": spaces.Box(low=-100, high=1000, shape=(self.MAX_HAND, 4), dtype=np.float32), # cost, damage, block, is_playable
            "enemies": spaces.Box(low=-100, high=10000, shape=(self.MAX_ENEMIES, 4), dtype=np.float32), # hp, max_hp, block, intent_damage
            "phase": spaces.Box(low=0, high=1, shape=(8,), dtype=np.float32), # one-hot phase
            "action_mask": spaces.Box(low=0, high=1, shape=(self.TOTAL_ACTIONS,), dtype=np.int8)
        })

        self.last_state = None
        self.last_hp = 0
        self.last_enemy_hp = 0
        self.current_floor = 0
        
        # Start game engine
        self._start_engine()

    def _start_engine(self):
        if self.proc:
            self.proc.terminate()
            self.proc.wait()

        game_dir = os.path.expanduser(
            "~/Library/Application Support/Steam/steamapps/common/Slay the Spire 2/SlayTheSpire2.app/Contents/Resources/data_sts2_macos_arm64"
        )
        if not os.path.exists(game_dir):
            game_dir = "" # Fallback to setup.sh copy

        env = os.environ.copy()
        env["STS2_GAME_DIR"] = game_dir

        base_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
        
        self.proc = subprocess.Popen(
            ["dotnet", "run", "--no-build", "--project", "src/Sts2Headless/Sts2Headless.csproj"],
            stdin=subprocess.PIPE,
            stdout=subprocess.PIPE,
            stderr=subprocess.PIPE,
            text=True,
            bufsize=1,
            cwd=base_dir,
            env=env
        )
        
        def _forward_stderr():
            for line in self.proc.stderr:
                import sys
                print(f"[GAME] {line.rstrip()}", file=sys.stderr)
        threading.Thread(target=_forward_stderr, daemon=True).start()
        
        # Wait for ready signal
        self._read_json()

    def _read_json(self):
        while True:
            line = self.proc.stdout.readline().strip()
            if not line:
                return {"decision": "error", "message": "EOF"}
            if line.startswith("{"):
                return json.loads(line)

    def _send_json(self, cmd):
        with self.lock:
            self.proc.stdin.write(json.dumps(cmd) + "\n")
            self.proc.stdin.flush()
            return self._read_json()

    def reset(self, seed=None, options=None):
        super().reset(seed=seed)
        seed_str = uuid.uuid4().hex[:12] if seed is None else str(seed)
        
        cmd = {"cmd": "start_run", "character": self.character, "seed": seed_str}
        state = self._send_json(cmd)
        
        self.last_state = state
        self.last_hp = state.get("player", {}).get("hp", 0)
        self.last_enemy_hp = self._get_total_enemy_hp(state)
        self.current_floor = state.get("context", {}).get("floor", 0)
        
        print("DEBUG reset state:", state)
        obs, info = self._build_obs(state)
        return obs, info

    def _get_total_enemy_hp(self, state):
        if "enemies" not in state:
            return 0
        return sum(e.get("hp", 0) for e in state["enemies"] if e.get("alive", True))

    def step(self, action):
        if isinstance(action, np.integer):
            action = int(action)
        cmd = self._action_to_cmd(action, self.last_state)
        
        # If invalid command generated (e.g., fallback), we just proceed
        if not cmd:
            cmd = {"cmd": "action", "action": "proceed"}

        state = self._send_json(cmd)
        
        # Handle errors
        if state.get("type") == "error" or state.get("decision") == "error":
            # Just send proceed to unstuck
            state = self._send_json({"cmd": "action", "action": "proceed"})
            
        self.last_state = state
        
        # Calculate Rewards
        current_hp = state.get("player", {}).get("hp", 0) if "player" in state else self.last_hp
        current_enemy_hp = self._get_total_enemy_hp(state)
        current_floor = state.get("context", {}).get("floor", 0) if "context" in state else self.current_floor
        
        damage_taken = max(0, self.last_hp - current_hp)
        damage_dealt = max(0, self.last_enemy_hp - current_enemy_hp)
        floor_advanced = current_floor > self.current_floor
        
        # Update trackers
        self.last_hp = current_hp
        self.last_enemy_hp = current_enemy_hp
        self.current_floor = current_floor

        # Sparse Base Reward
        reward = 0.0
        terminated = False
        
        if state.get("decision") == "game_over":
            terminated = True
            if state.get("victory", False):
                reward = 100.0
            else:
                reward = -1.0
                
        # Dense signals for subagents
        info = {
            "damage_taken": damage_taken,
            "damage_dealt": damage_dealt,
            "floor_advanced": floor_advanced,
            "combat_won": False
        }
        
        if state.get("decision") == "card_reward":
            info["combat_won"] = True

        obs, mask_info = self._build_obs(state)
        info["action_mask"] = mask_info["action_mask"]
        
        return obs, reward, terminated, False, info

    def _action_to_cmd(self, action, state):
        dec = state.get("decision", "")
        
        if action == self.ACTION_END_TURN:
            if dec == "combat_play": return {"cmd": "action", "action": "end_turn"}
            elif dec == "shop" or dec == "event_choice": return {"cmd": "action", "action": "leave_room"}
            else: return {"cmd": "action", "action": "proceed"}
            
        elif self.ACTION_PLAY_CARD_START <= action <= self.ACTION_PLAY_CARD_END:
            idx = action - self.ACTION_PLAY_CARD_START
            c_idx = idx // 10
            t_idx = idx % 10
            return {"cmd": "action", "action": "play_card", "args": {"card_index": c_idx, "target_index": t_idx}}
            
        elif self.ACTION_USE_POTION_START <= action <= self.ACTION_USE_POTION_END:
            idx = action - self.ACTION_USE_POTION_START
            p_idx = idx // 10
            t_idx = idx % 10
            return {"cmd": "action", "action": "use_potion", "args": {"potion_index": p_idx, "target_index": t_idx}}
            
        elif self.ACTION_CHOICE_START <= action <= self.ACTION_CHOICE_END:
            idx = action - self.ACTION_CHOICE_START
            if dec == "map_select":
                choices = state.get("choices", [])
                if idx < len(choices):
                    return {"cmd": "action", "action": "select_map_node", "args": {"col": choices[idx]["col"], "row": choices[idx]["row"]}}
            elif dec == "card_reward":
                return {"cmd": "action", "action": "select_card_reward", "args": {"card_index": idx}}
            elif dec in ["rest_site", "event_choice"]:
                return {"cmd": "action", "action": "choose_option", "args": {"option_index": idx}}
            elif dec == "card_select":
                return {"cmd": "action", "action": "select_cards", "args": {"indices": str(idx)}}
                
        elif self.ACTION_SHOP_BUY_START <= action <= self.ACTION_SHOP_BUY_END:
            idx = action - self.ACTION_SHOP_BUY_START
            if dec == "shop":
                cards = state.get("cards", [])
                relics = state.get("relics", [])
                potions = state.get("potions", [])
                total = len(cards) + len(relics) + len(potions)
                if idx < len(cards):
                    return {"cmd": "action", "action": "buy_card", "args": {"card_index": cards[idx]["index"]}}
                # (Simplicity: omitting relics and potions buy logic for now, easily expandable)
                
        elif action == self.ACTION_SHOP_REMOVE:
            if dec == "shop":
                return {"cmd": "action", "action": "remove_card"}
                
        elif action == self.ACTION_SKIP_REWARD:
            if dec == "card_reward":
                return {"cmd": "action", "action": "skip_card_reward"}
                
        return None

    def _build_obs(self, state):
        obs = {
            "global": np.zeros(7, dtype=np.float32),
            "hand": np.zeros((self.MAX_HAND, 4), dtype=np.float32),
            "enemies": np.zeros((self.MAX_ENEMIES, 4), dtype=np.float32),
            "phase": np.zeros(8, dtype=np.float32),
            "action_mask": np.zeros(self.TOTAL_ACTIONS, dtype=np.int8)
        }
        
        player = state.get("player", {})
        context = state.get("context", {})
        obs["global"] = np.array([
            player.get("hp", 0),
            player.get("max_hp", 0),
            state.get("energy", 0),
            player.get("block", 0),
            player.get("gold", 0),
            context.get("floor", 0),
            context.get("act", 0)
        ], dtype=np.float32)

        # Phase one-hot
        dec = state.get("decision", "")
        phases = ["combat_play", "map_select", "card_reward", "rest_site", "event_choice", "shop", "game_over"]
        if dec in phases:
            obs["phase"][phases.index(dec)] = 1.0
        else:
            obs["phase"][7] = 1.0

        mask = obs["action_mask"]

        if dec == "combat_play":
            mask[self.ACTION_END_TURN] = 1
            
            # Enemies
            enemies = state.get("enemies", [])
            for i, en in enumerate(enemies[:self.MAX_ENEMIES]):
                if not en.get("alive", False): continue
                intent_dmg = 0
                for intent in (en.get("intents") or []):
                    if intent.get("type") == "Attack":
                        intent_dmg += intent.get("damage", 0)
                obs["enemies"][i] = [en.get("hp", 0), en.get("max_hp", 0), en.get("block", 0), intent_dmg]
            
            # Hand
            hand = state.get("hand", [])
            for i, card in enumerate(hand[:self.MAX_HAND]):
                can_play = card.get("can_play", False)
                stats = card.get("stats", {})
                obs["hand"][i] = [card.get("cost", 0), stats.get("damage", 0), stats.get("block", 0), 1.0 if can_play else 0.0]
                
                # Action masking for cards
                if can_play:
                    target_type = card.get("target_type", "")
                    if target_type == "AnyEnemy":
                        for t_idx in range(len(enemies)):
                            if enemies[t_idx].get("alive", False):
                                mask[self.ACTION_PLAY_CARD_START + i * 10 + t_idx] = 1
                    else:
                        mask[self.ACTION_PLAY_CARD_START + i * 10] = 1 # target_index = 0

        elif dec == "map_select":
            choices = state.get("choices", [])
            for i in range(min(len(choices), self.MAX_CHOICES)):
                mask[self.ACTION_CHOICE_START + i] = 1
                
        elif dec == "card_reward":
            mask[self.ACTION_SKIP_REWARD] = 1
            cards = state.get("cards", [])
            for i in range(min(len(cards), self.MAX_CHOICES)):
                mask[self.ACTION_CHOICE_START + i] = 1
                
        elif dec == "rest_site" or dec == "event_choice":
            options = state.get("options", [])
            for i, opt in enumerate(options[:self.MAX_CHOICES]):
                if not opt.get("is_locked", False):
                    mask[self.ACTION_CHOICE_START + i] = 1
            if dec == "event_choice":
                mask[self.ACTION_END_TURN] = 1 # leave
                
        elif dec == "shop":
            mask[self.ACTION_END_TURN] = 1 # leave
            if player.get("gold", 0) >= state.get("card_removal_cost", 999):
                mask[self.ACTION_SHOP_REMOVE] = 1
            cards = state.get("cards", [])
            for i, card in enumerate(cards[:self.MAX_CHOICES]):
                if player.get("gold", 0) >= card.get("cost", 9999):
                    mask[self.ACTION_SHOP_BUY_START + i] = 1
                    
        elif dec == "card_select":
            cards = state.get("cards", [])
            for i in range(min(len(cards), self.MAX_CHOICES)):
                mask[self.ACTION_CHOICE_START + i] = 1
                
        elif dec in ("bundle_select", "proceed"):
            mask[self.ACTION_END_TURN] = 1

        info = {"action_mask": mask}
        return obs, info

    def close(self):
        if self.proc:
            self.proc.terminate()
            self.proc.wait()

# Example test runner
if __name__ == "__main__":
    env = Sts2Env()
    obs, info = env.reset()
    print("Reset complete, Floor:", obs["global"][5])
    
    # Take 5 random valid steps
    for step in range(5):
        mask = info["action_mask"]
        valid_actions = np.where(mask == 1)[0]
        if len(valid_actions) == 0:
            print("No valid actions, ending")
            break
            
        action = np.random.choice(valid_actions)
        print(f"Step {step}: taking action {action}")
        obs, reward, done, trunc, info = env.step(action)
        print(f"  Rewards: sparse={reward}, taken={info['damage_taken']}, dealt={info['damage_dealt']}")
        if done:
            print("Game over.")
            break
            
    env.close()

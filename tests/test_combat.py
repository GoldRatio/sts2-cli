"""Tests for combat scenarios."""
import pytest


class TestCombatStructure:
    def test_combat_play_fields(self, game):
        state = game.start(seed="combat_s1")
        state = game.play_to_decision(state, "combat_play")
        for key in ("decision", "round", "energy", "max_energy", "hand",
                    "enemies", "player", "draw_pile_count", "discard_pile_count"):
            assert key in state, f"Missing field: {key}"
        assert state["round"] >= 1
        assert state["energy"] > 0

    def test_card_fields(self, game):
        state = game.start(seed="combat_s2")
        state = game.play_to_decision(state, "combat_play")
        for card in state["hand"]:
            assert isinstance(card["name"], str)
            assert "cost" in card
            assert "can_play" in card
            assert "type" in card
            assert card["type"] in ("Attack", "Skill", "Power", "Status", "Curse")

    def test_enemy_fields(self, game):
        state = game.start(seed="combat_s3")
        state = game.play_to_decision(state, "combat_play")
        for e in state["enemies"]:
            assert isinstance(e["name"], str)
            assert e["hp"] > 0
            assert e["max_hp"] > 0
            assert "block" in e
            assert "intents" in e


class TestPlayCards:
    def test_play_card_costs_energy(self, game):
        state = game.start(seed="combat_play1")
        state = game.play_to_decision(state, "combat_play")
        energy_before = state["energy"]
        playable = [c for c in state["hand"] if c.get("can_play") and c["cost"] <= energy_before]
        assert playable
        card = playable[0]
        args = {"card_index": card["index"]}
        if card.get("target_type") == "AnyEnemy":
            args["target_index"] = state["enemies"][0]["index"]
        state = game.act("play_card", **args)
        if state["decision"] == "combat_play":
            assert state["energy"] == energy_before - card["cost"]

    def test_play_attack_reduces_enemy_hp(self, game):
        state = game.start(seed="combat_play2")
        state = game.play_to_decision(state, "combat_play")
        enemies_before = {e["index"]: e["hp"] for e in state["enemies"]}
        attacks = [c for c in state["hand"] if c.get("can_play") and c["type"] == "Attack"
                   and c["cost"] <= state["energy"]]
        if not attacks:
            pytest.skip("No attack cards in hand")
        card = attacks[0]
        target = state["enemies"][0]
        args = {"card_index": card["index"]}
        if card.get("target_type") == "AnyEnemy":
            args["target_index"] = target["index"]
        state = game.act("play_card", **args)
        if state["decision"] == "combat_play":
            new_target = next((e for e in state["enemies"] if e["index"] == target["index"]), None)
            if new_target and target.get("block", 0) == 0:
                assert new_target["hp"] < enemies_before[target["index"]]

    def test_play_defend_adds_block(self, game):
        state = game.start(seed="combat_play3")
        state = game.play_to_decision(state, "combat_play")
        block_before = state["player"].get("block", 0)
        defends = [c for c in state["hand"] if c.get("can_play") and c["type"] == "Skill"
                   and c["cost"] <= state["energy"]]
        if not defends:
            pytest.skip("No skill cards in hand")
        card = defends[0]
        state = game.act("play_card", card_index=card["index"])
        if state["decision"] == "combat_play":
            assert state["player"].get("block", 0) >= block_before


class TestTurnFlow:
    def test_end_turn_advances_round(self, game):
        state = game.start(seed="combat_turn1")
        state = game.play_to_decision(state, "combat_play")
        rnd = state["round"]
        state = game.act("end_turn")
        if state["decision"] == "combat_play":
            assert state["round"] == rnd + 1

    def test_end_turn_resets_energy(self, game):
        state = game.start(seed="combat_turn2")
        state = game.play_to_decision(state, "combat_play")
        max_e = state["max_energy"]
        # Spend some energy
        playable = [c for c in state["hand"] if c.get("can_play") and c["cost"] <= state["energy"]]
        if playable:
            card = playable[0]
            args = {"card_index": card["index"]}
            if card.get("target_type") == "AnyEnemy":
                args["target_index"] = state["enemies"][0]["index"]
            state = game.act("play_card", **args)
        state = game.act("end_turn")
        if state["decision"] == "combat_play":
            assert state["energy"] == max_e

    def test_end_turn_draws_new_hand(self, game):
        state = game.start(seed="combat_turn3")
        state = game.play_to_decision(state, "combat_play")
        state = game.act("end_turn")
        if state["decision"] == "combat_play":
            assert len(state["hand"]) > 0


class TestCombatEnd:
    def test_win_combat_leads_to_reward(self, game):
        state = game.start(seed="combat_win1")
        state = game.play_to_decision(state, "combat_play")
        # Auto-play until combat ends
        for _ in range(200):
            if state.get("decision") != "combat_play":
                break
            state = game.auto_combat(state)
        assert state["decision"] in ("card_reward", "map_select", "card_select", "bundle_select")

    def test_enemy_powers_have_description(self, game):
        state = game.start(seed="combat_epow1")
        state = game.play_to_decision(state, "combat_play")
        # Play a few rounds to find enemies with powers
        for _ in range(100):
            if state.get("decision") != "combat_play":
                break
            for e in state.get("enemies", []):
                for pw in (e.get("powers") or []):
                    assert "name" in pw
                    assert "amount" in pw
                    assert "description" in pw
                    return  # found one, test passed
            state = game.auto_combat(state)
        pytest.skip("No enemy powers encountered")


class TestCombatEdgeCases:
    def test_exhaust_all_and_end_turn(self, game):
        """Empty hand → end_turn should work."""
        state = game.start(seed="combat_edge1")
        state = game.play_to_decision(state, "combat_play")
        # Play all playable cards
        for _ in range(20):
            if state.get("decision") != "combat_play":
                break
            playable = [c for c in state["hand"] if c.get("can_play") and c["cost"] <= state["energy"]]
            if not playable:
                break
            card = playable[0]
            args = {"card_index": card["index"]}
            if card.get("target_type") == "AnyEnemy" and state["enemies"]:
                args["target_index"] = state["enemies"][0]["index"]
            state = game.act("play_card", **args)
        if state.get("decision") == "combat_play":
            state = game.act("end_turn")
            assert state.get("type") != "error"

    def test_zero_hp_game_over(self, game):
        """If player HP reaches 0, should get game_over."""
        state = game.start(seed="combat_edge2")
        state = game.play_to_decision(state, "combat_play")
        # Just end turn repeatedly until we die or win
        for _ in range(200):
            dec = state.get("decision")
            if dec == "game_over":
                assert state.get("victory") is not None
                return
            if dec != "combat_play":
                break
            state = game.act("end_turn")
        # If we didn't die, that's fine too

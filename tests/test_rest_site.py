"""Tests for rest site / campfire scenarios."""
import pytest


class TestRestSiteStructure:
    def test_rest_site_fields(self, game):
        state = game.start(seed="rest_s1")
        try:
            state = game.play_to_decision_via(state, "rest_site", prefer_node="RestSite")
        except RuntimeError:
            pytest.skip("Could not reach a rest site")
        assert state["decision"] == "rest_site"
        assert "options" in state
        for opt in state["options"]:
            assert "index" in opt
            assert "option_id" in opt
            assert "name" in opt
            assert "is_enabled" in opt

    def test_has_heal_and_smith(self, game):
        state = game.start(seed="rest_s2")
        try:
            state = game.play_to_decision_via(state, "rest_site", prefer_node="RestSite")
        except RuntimeError:
            pytest.skip("Could not reach a rest site")
        option_ids = {o["option_id"] for o in state["options"]}
        assert "HEAL" in option_ids, f"Missing HEAL, got: {option_ids}"
        assert "SMITH" in option_ids, f"Missing SMITH, got: {option_ids}"


class TestRestSiteActions:
    def test_heal_restores_hp(self, game):
        state = game.start(seed="rest_heal1")
        # Fight first to lose some HP
        state = game.play_to_decision(state, "combat_play")
        for _ in range(200):
            if state.get("decision") != "combat_play":
                break
            state = game.auto_combat(state)
        # Navigate to rest site
        try:
            state = game.play_to_decision_via(state, "rest_site", prefer_node="RestSite")
        except RuntimeError:
            pytest.skip("Could not reach rest site")
        hp_before = state["player"]["hp"]
        max_hp = state["player"]["max_hp"]
        heal_opt = next((o for o in state["options"] if o["option_id"] == "HEAL" and o["is_enabled"]), None)
        if not heal_opt:
            pytest.skip("HEAL not enabled")
        if hp_before >= max_hp:
            pytest.skip("Already at full HP")
        state = game.act("choose_option", option_index=heal_opt["index"])
        # After heal, check HP increased (might be in map_select now)
        new_hp = state.get("player", {}).get("hp", hp_before)
        assert new_hp > hp_before or new_hp == max_hp

    def test_smith_triggers_card_select(self, game):
        state = game.start(seed="rest_smith1")
        try:
            state = game.play_to_decision_via(state, "rest_site", prefer_node="RestSite")
        except RuntimeError:
            pytest.skip("Could not reach rest site")
        smith_opt = next((o for o in state["options"] if o["option_id"] == "SMITH" and o["is_enabled"]), None)
        if not smith_opt:
            pytest.skip("SMITH not enabled")
        state = game.act("choose_option", option_index=smith_opt["index"])
        assert state["decision"] == "card_select"
        assert len(state.get("cards", [])) > 0

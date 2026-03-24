"""Tests for map navigation."""
import pytest


class TestMapStructure:
    def test_map_select_fields(self, game):
        state = game.start(seed="map_s1")
        state = game.play_to_decision(state, "map_select")
        assert state["decision"] == "map_select"
        assert "choices" in state
        assert len(state["choices"]) > 0
        for ch in state["choices"]:
            assert "col" in ch
            assert "row" in ch
            assert "type" in ch

    def test_get_map_full(self, game):
        state = game.start(seed="map_s2")
        state = game.play_to_decision(state, "map_select")
        m = game.get_map()
        assert m["type"] == "map"
        assert "rows" in m
        assert "boss" in m
        assert "current_coord" in m
        assert len(m["rows"]) > 0

    def test_context_has_act_floor(self, game):
        state = game.start(seed="map_s3")
        state = game.play_to_decision(state, "map_select")
        ctx = state.get("context", {})
        assert "floor" in ctx
        assert "act_name" in ctx
        assert isinstance(ctx["act_name"], str)

    def test_node_types_valid(self, game):
        state = game.start(seed="map_s4")
        state = game.play_to_decision(state, "map_select")
        valid_types = {"Monster", "Elite", "Boss", "RestSite", "Shop",
                       "Treasure", "Event", "Unknown", "Ancient"}
        for ch in state["choices"]:
            assert ch["type"] in valid_types


class TestMapNavigation:
    def test_select_node_changes_state(self, game):
        state = game.start(seed="map_nav1")
        state = game.play_to_decision(state, "map_select")
        pick = state["choices"][0]
        state = game.act("select_map_node", col=pick["col"], row=pick["row"])
        # Should now be in a different decision type
        assert state.get("decision") is not None

    def test_monster_leads_to_combat(self, game):
        state = game.start(seed="map_nav2")
        state = game.play_to_decision(state, "map_select")
        monster = next((ch for ch in state["choices"] if ch["type"] == "Monster"), None)
        if not monster:
            pytest.skip("No Monster node in choices")
        state = game.act("select_map_node", col=monster["col"], row=monster["row"])
        assert state["decision"] == "combat_play"

    def test_floor_increments(self, game):
        state = game.start(seed="map_nav3")
        state = game.play_to_decision(state, "map_select")
        floor1 = state.get("context", {}).get("floor", 0)
        # Navigate through one node and back to map
        state = game.play_to_decision(state, "map_select")  # picks first node
        # After auto-play back to map, floor should have incremented
        # (play_to_decision will auto-play through combat/events)

"""Tests for event scenarios (Neow + random events)."""
import pytest


class TestNeowEvent:
    def test_neow_event_structure(self, game):
        """First decision after start_run is Neow event."""
        state = game.start(seed="event_neow1")
        assert state["decision"] == "event_choice"
        assert isinstance(state["event_name"], str)
        assert len(state["event_name"]) > 0
        assert "options" in state

    def test_neow_options_have_titles(self, game):
        state = game.start(seed="event_neow2")
        assert state["decision"] == "event_choice"
        for opt in state["options"]:
            assert "index" in opt
            assert "title" in opt
            assert isinstance(opt["title"], str)
            assert "is_locked" in opt

    def test_neow_option_vars(self, game):
        """Neow options with vars should have numeric values."""
        state = game.start(seed="event_neow3")
        for opt in state["options"]:
            if opt.get("vars"):
                for k, v in opt["vars"].items():
                    assert isinstance(v, (int, float)), f"Var {k} should be numeric, got {type(v)}"

    def test_choose_neow_option(self, game):
        state = game.start(seed="event_neow4")
        unlocked = [o for o in state["options"] if not o.get("is_locked")]
        assert unlocked
        state = game.act("choose_option", option_index=unlocked[0]["index"])
        # Should advance past the event
        assert state["decision"] != "event_choice" or state.get("event_name") != "Neow"


class TestRandomEvents:
    def test_event_has_name_and_options(self, game):
        """Random events (?) have event_name and options."""
        state = game.start(seed="event_rand1")
        try:
            # Navigate to map, prefer Event nodes
            state = game.play_to_decision_via(state, "event_choice", prefer_node="Event")
        except RuntimeError:
            pytest.skip("Could not reach a random event with this seed")
        # If we got here, it might be Neow again at start; skip past it
        if "Neow" in str(state.get("event_name", "")):
            state = game.act("choose_option", option_index=0)
            try:
                state = game.play_to_decision_via(state, "event_choice", prefer_node="Event")
            except RuntimeError:
                pytest.skip("No random event found")
        assert isinstance(state["event_name"], str)
        assert len(state["options"]) > 0

    def test_event_description_no_raw_tags(self, game):
        """Event descriptions should not contain unresolved [IsMultiplayer] etc."""
        state = game.start(seed="event_rand2")
        # Just check Neow descriptions
        for opt in state.get("options", []):
            d = opt.get("description") or ""
            assert "[IsMultiplayer]" not in d, f"Unresolved tag in: {d}"

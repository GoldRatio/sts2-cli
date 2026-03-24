"""Tests for card rewards and card selection."""
import pytest


class TestCardReward:
    def test_card_reward_structure(self, game):
        state = game.start(seed="reward_s1")
        state = game.play_to_decision(state, "card_reward")
        assert state["decision"] == "card_reward"
        assert "cards" in state
        assert len(state["cards"]) > 0
        for card in state["cards"]:
            assert isinstance(card["name"], str)
            assert "cost" in card
            assert "type" in card
            assert "rarity" in card

    def test_select_card_adds_to_deck(self, game):
        state = game.start(seed="reward_pick1")
        state = game.play_to_decision(state, "card_reward")
        deck_before = state["player"]["deck_size"]
        card = state["cards"][0]
        state = game.act("select_card_reward", card_index=card["index"])
        deck_after = state.get("player", {}).get("deck_size", deck_before)
        assert deck_after == deck_before + 1

    def test_skip_card_no_change(self, game):
        state = game.start(seed="reward_skip1")
        state = game.play_to_decision(state, "card_reward")
        deck_before = state["player"]["deck_size"]
        state = game.act("skip_card_reward")
        deck_after = state.get("player", {}).get("deck_size", deck_before)
        assert deck_after == deck_before

    def test_skip_leads_to_map(self, game):
        state = game.start(seed="reward_skip2")
        state = game.play_to_decision(state, "card_reward")
        state = game.act("skip_card_reward")
        # Might go to another card_reward (gold/potion) or map
        for _ in range(5):
            if state.get("decision") == "map_select":
                return
            if state.get("decision") == "card_reward":
                state = game.act("skip_card_reward")
            else:
                break
        assert state["decision"] == "map_select"


class TestCardSelect:
    def test_card_select_structure(self, game):
        """card_select has cards, min_select, max_select."""
        state = game.start(seed="cardsel_s1")
        try:
            state = game.play_to_decision(state, "card_select")
        except RuntimeError:
            pytest.skip("Could not reach card_select")
        assert "cards" in state
        assert "min_select" in state
        assert "max_select" in state
        assert len(state["cards"]) > 0

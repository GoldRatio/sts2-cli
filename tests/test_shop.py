"""Tests for shop scenarios."""
import pytest


class TestShopStructure:
    def test_shop_fields(self, game):
        state = game.start(seed="shop_s1")
        try:
            state = game.play_to_decision_via(state, "shop", prefer_node="Shop")
        except RuntimeError:
            pytest.skip("Could not reach a shop")
        assert state["decision"] == "shop"
        assert "cards" in state
        assert "relics" in state
        assert "potions" in state
        assert "card_removal_cost" in state
        assert "player" in state

    def test_shop_cards_have_description(self, game):
        state = game.start(seed="shop_s2")
        try:
            state = game.play_to_decision_via(state, "shop", prefer_node="Shop")
        except RuntimeError:
            pytest.skip("Could not reach a shop")
        for card in state["cards"]:
            assert isinstance(card["name"], str)
            assert "description" in card
            assert "cost" in card
            assert "type" in card
            assert "card_cost" in card
            assert "is_stocked" in card

    def test_shop_cards_have_upgrade_preview(self, game):
        state = game.start(seed="shop_s3")
        try:
            state = game.play_to_decision_via(state, "shop", prefer_node="Shop")
        except RuntimeError:
            pytest.skip("Could not reach a shop")
        has_upgrade = any(c.get("after_upgrade") for c in state["cards"])
        assert has_upgrade, "At least one shop card should have upgrade preview"

    def test_shop_relics_have_description(self, game):
        state = game.start(seed="shop_s4")
        try:
            state = game.play_to_decision_via(state, "shop", prefer_node="Shop")
        except RuntimeError:
            pytest.skip("Could not reach a shop")
        for r in state["relics"]:
            assert isinstance(r["name"], str)
            assert "description" in r
            assert "cost" in r

    def test_shop_potions_have_description(self, game):
        state = game.start(seed="shop_s5")
        try:
            state = game.play_to_decision_via(state, "shop", prefer_node="Shop")
        except RuntimeError:
            pytest.skip("Could not reach a shop")
        for p in state["potions"]:
            assert isinstance(p["name"], str)
            assert "description" in p
            assert "cost" in p


class TestShopBuy:
    def test_buy_card_reduces_gold(self, game):
        state = game.start(seed="shop_buy1")
        try:
            state = game.play_to_decision_via(state, "shop", prefer_node="Shop")
        except RuntimeError:
            pytest.skip("Could not reach a shop")
        gold_before = state["player"]["gold"]
        deck_before = state["player"]["deck_size"]
        stocked = [c for c in state["cards"] if c.get("is_stocked") and c["cost"] <= gold_before]
        if not stocked:
            pytest.skip("No affordable cards")
        card = stocked[0]
        state = game.act("buy_card", card_index=card["index"])
        if state.get("decision") == "shop":
            assert state["player"]["gold"] < gold_before
            assert state["player"]["deck_size"] == deck_before + 1

    def test_leave_shop(self, game):
        state = game.start(seed="shop_leave1")
        try:
            state = game.play_to_decision_via(state, "shop", prefer_node="Shop")
        except RuntimeError:
            pytest.skip("Could not reach a shop")
        state = game.act("leave_room")
        assert state["decision"] == "map_select"

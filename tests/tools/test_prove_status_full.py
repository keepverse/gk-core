"""Contract tests for `gk-core/scripts/prove_status_full.py`.

The proofable substance of this script is the ASSERTION LOGIC — the pure functions that decide whether
a scenario's events constitute a pass. Reaching the rest of the flow needs a running game with the
injector connected, which no slot on this machine has. So the assertions are decided by planted event
lists here, and the refusal path is decided against a REAL server, which is what every one of these
live scripts actually does until a game is running.

THE SCENARIO MATRIX IS A CLOSED VOCABULARY, and the reason is a URL. The PS1 interpolated scenario
names into `/scenario/$id` with no validation. The Python port preserves the matrix verbatim but the
test pins that the matrix is exactly the 27 L2 + 5 unity scenarios the PS1 defined.

THE PURE FUNCTIONS ARE THE CONTRACT. Each assertion function takes a list of event dicts and returns
`{"pass": bool, "note": str}`. The tests plant events that exercise every branch of every assertion,
so a careless rewrite that drops a check or inverts a condition is caught by a red case.
"""
from __future__ import annotations

import ast
import http.server
import importlib.util
import io
import json
import os
import socket
import subprocess
import sys
import threading
import time
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("PROVE_STATUS_FULL_SCRIPT",
                            REPO / "scripts" / "prove_status_full.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_prove_status_full.py"
RUN_TIMEOUT = 300


def _load():
    spec = importlib.util.spec_from_file_location("prove_status_full", SCRIPT)
    module = importlib.util.module_from_spec(spec)
    sys.modules["prove_status_full"] = module
    spec.loader.exec_module(module)
    return module


p = _load()


# ---------------------------------------------------------------------------
# Planted event fixtures
# ---------------------------------------------------------------------------

def _event(id: int, kind: str, payload=None) -> dict:
    """A debug event with an id, kind, and optional payload."""
    ev = {"id": id, "kind": kind}
    if payload is not None:
        ev["payload"] = payload
    return ev


def _board(plants=None, zombies=None) -> dict:
    """A board-stats payload."""
    return {"plants": plants or [], "zombies": zombies or []}


def _plant(ptr: str, col: int = 2, row: int = 2) -> dict:
    return {"ptr": ptr, "col": col, "row": row}


def _zombie(ptr: str, row: int = 2) -> dict:
    return {"ptr": ptr, "row": row}


def _status(instances=None, resisted=None, count=0, resisted_count=0) -> dict:
    """A status snapshot payload."""
    out = {"instances": instances or [], "count": count, "resistedCount": resisted_count}
    if resisted is not None:
        out["resisted"] = resisted
    return out


def _synthetic(actor_ptr: str, target_ptr: str, actions: int = 1) -> dict:
    return {"actorPtr": actor_ptr, "targetPtr": target_ptr, "actions": actions}


def _instance(status_id: str, attacker_ptr: str, host_ptr: str) -> dict:
    return {"statusId": status_id, "attackerPtr": attacker_ptr, "hostPtr": host_ptr}


# A complete passing apply scenario
PASSING_APPLY_EVENTS = [
    _event(1, "debug.board-stats", _board(
        plants=[_plant("0xAAA")],
        zombies=[_zombie("0xBBB"), _zombie("0xCCC")],
    )),
    _event(2, "debug.effect.synthetic", _synthetic("0xAAA", "0xBBB", 1)),
    _event(3, "debug.status", _status(
        instances=[_instance("wither", "0xAAA", "0xBBB")],
        count=1,
    )),
    _event(4, "debug.run-steps.done"),
]


class TestEqPtr(unittest.TestCase):
    """The pointer comparison is the foundation of every assertion."""

    def test_EQUAL_ptrs_match(self) -> None:
        self.assertIs(p.eq_ptr("0xABC", "0xABC"), True)

    def test_CASE_INSENSITIVE(self) -> None:
        self.assertIs(p.eq_ptr("0xabc", "0xABC"), True)

    def test_0x_prefix_irrelevant(self) -> None:
        self.assertIs(p.eq_ptr("0xABC", "ABC"), True)

    def test_EMPTY_ptrs_do_not_match(self) -> None:
        self.assertIs(p.eq_ptr("", "0xABC"), False)
        self.assertIs(p.eq_ptr("0xABC", ""), False)
        self.assertIs(p.eq_ptr("   ", "0xABC"), False)

    def test_NONE_does_not_match(self) -> None:
        self.assertIs(p.eq_ptr(None, "0xABC"), False)
        self.assertIs(p.eq_ptr("0xABC", None), False)

    def test_DIFFERENT_ptrs_do_not_match(self) -> None:
        self.assertIs(p.eq_ptr("0xABC", "0xABD"), False)

    def test_0x_only_does_not_match(self) -> None:
        """'0x' with nothing after it is not a pointer."""
        self.assertIs(p.eq_ptr("0x", "0x"), False)


class TestGetPlantFromBoard(unittest.TestCase):
    def test_RETURNS_plant_at_col2_row2(self) -> None:
        board = _board(plants=[_plant("0xOTHER", col=0, row=0), _plant("0xTARGET", col=2, row=2)])
        self.assertEqual(p.get_plant_from_board(board)["ptr"], "0xTARGET")

    def test_RETURNS_first_plant_when_no_col2_row2(self) -> None:
        board = _board(plants=[_plant("0xFIRST", col=0, row=0), _plant("0xSECOND", col=1, row=1)])
        self.assertEqual(p.get_plant_from_board(board)["ptr"], "0xFIRST")

    def test_NONE_when_no_plants(self) -> None:
        self.assertIsNone(p.get_plant_from_board(_board(plants=[])))

    def test_NONE_when_board_is_None(self) -> None:
        self.assertIsNone(p.get_plant_from_board(None))

    def test_NONE_when_board_has_no_plants_key(self) -> None:
        self.assertIsNone(p.get_plant_from_board({"zombies": []}))


class TestGetZombiePtrs(unittest.TestCase):
    def test_RETURNS_ptrs(self) -> None:
        board = _board(zombies=[_zombie("0xZ1"), _zombie("0xZ2")])
        self.assertEqual(p.get_zombie_ptrs(board), ["0xZ1", "0xZ2"])

    def test_EMPTY_when_no_zombies(self) -> None:
        self.assertEqual(p.get_zombie_ptrs(_board(zombies=[])), [])

    def test_EMPTY_when_board_is_None(self) -> None:
        self.assertEqual(p.get_zombie_ptrs(None), [])


class TestGetInstances(unittest.TestCase):
    def test_RETURNS_instances_list(self) -> None:
        inst = [_instance("wither", "0xA", "0xB")]
        self.assertEqual(p.get_instances(_status(instances=inst)), inst)

    def test_EMPTY_when_no_instances(self) -> None:
        self.assertEqual(p.get_instances(_status()), [])

    def test_EMPTY_when_status_is_None(self) -> None:
        self.assertEqual(p.get_instances(None), [])


class TestGetResisted(unittest.TestCase):
    def test_COMBINES_snapshot_and_events(self) -> None:
        snap_resisted = [{"statusId": "wither", "reason": "PotencyFloor", "hostPtr": "0xS1"}]
        events = [
            _event(1, "debug.status.resisted", {"statusId": "wither", "reason": "PotencyFloor", "hostPtr": "0xS2"}),
        ]
        result = p.get_resisted(events, _status(resisted=snap_resisted))
        self.assertEqual(len(result), 2)

    def test_EVENT_payload_can_be_None(self) -> None:
        events = [_event(1, "debug.status.resisted", None)]
        result = p.get_resisted(events, _status())
        self.assertEqual(len(result), 1)
        self.assertIsNone(result[0])


class TestFindInstanceForBoard(unittest.TestCase):
    def test_FINDS_instance_on_board_zombie(self) -> None:
        inst = _instance("wither", "0xAAA", "0xBBB")
        result = p.find_instance_for_board([inst], "wither", ["0xBBB", "0xCCC"], "")
        self.assertIsNotNone(result)
        self.assertEqual(result["hostPtr"], "0xBBB")

    def test_SKIPS_instance_not_on_board(self) -> None:
        inst = _instance("wither", "0xAAA", "0xOFFBOARD")
        result = p.find_instance_for_board([inst], "wither", ["0xBBB"], "")
        self.assertIsNone(result)

    def test_SKIPS_instance_with_wrong_statusId(self) -> None:
        inst = _instance("other", "0xAAA", "0xBBB")
        result = p.find_instance_for_board([inst], "wither", ["0xBBB"], "")
        self.assertIsNone(result)

    def test_PREFERS_host_matching_preferHost(self) -> None:
        inst1 = _instance("wither", "0xAAA", "0xBBB")
        inst2 = _instance("wither", "0xAAA", "0xCCC")
        result = p.find_instance_for_board([inst1, inst2], "wither", ["0xBBB", "0xCCC"], "0xCCC")
        self.assertEqual(result["hostPtr"], "0xCCC")

    def test_RETURNS_last_when_no_prefer_match(self) -> None:
        inst1 = _instance("wither", "0xAAA", "0xBBB")
        inst2 = _instance("wither", "0xAAA", "0xCCC")
        result = p.find_instance_for_board([inst1, inst2], "wither", ["0xBBB", "0xCCC"], "")
        self.assertEqual(result["hostPtr"], "0xCCC")


class TestPlantOnBoard(unittest.TestCase):
    def test_TRUE_when_ptr_matches(self) -> None:
        board = _board(plants=[_plant("0xAAA")])
        self.assertIs(p.test_plant_on_board(board, "0xAAA"), True)

    def test_FALSE_when_no_match(self) -> None:
        board = _board(plants=[_plant("0xAAA")])
        self.assertIs(p.test_plant_on_board(board, "0xBBB"), False)

    def test_FALSE_when_board_is_None(self) -> None:
        self.assertIs(p.test_plant_on_board(None, "0xAAA"), False)


class TestPayloadHelpers(unittest.TestCase):
    def test_get_first_payload_returns_first_match(self) -> None:
        events = [
            _event(1, "debug.status", {"count": 1}),
            _event(2, "debug.status", {"count": 2}),
        ]
        self.assertEqual(p.get_first_payload(events, "debug.status"), {"count": 1})

    def test_get_last_payload_returns_last_match(self) -> None:
        events = [
            _event(1, "debug.status", {"count": 1}),
            _event(2, "debug.status", {"count": 2}),
        ]
        self.assertEqual(p.get_last_payload(events, "debug.status"), {"count": 2})

    def test_get_payload_returns_None_for_no_payload(self) -> None:
        self.assertIsNone(p.get_payload({"id": 1, "kind": "debug.status"}))

    def test_get_payload_parses_string_payload(self) -> None:
        ev = {"id": 1, "kind": "debug.status", "payload": '{"count": 5}'}
        self.assertEqual(p.get_payload(ev), {"count": 5})

    def test_get_payload_returns_None_for_unparseable_string(self) -> None:
        ev = {"id": 1, "kind": "debug.status", "payload": "not json"}
        self.assertIsNone(p.get_payload(ev))


class TestGetStatusAfterSynthetic(unittest.TestCase):
    def test_RETURNS_status_after_synthetic(self) -> None:
        events = [
            _event(1, "debug.effect.synthetic", _synthetic("0xA", "0xB")),
            _event(2, "debug.status", {"count": 1}),
        ]
        self.assertEqual(p.get_status_after_synthetic(events), {"count": 1})

    def test_NONE_when_no_synthetic(self) -> None:
        events = [_event(1, "debug.status", {"count": 1})]
        self.assertIsNone(p.get_status_after_synthetic(events))

    def test_NONE_when_status_before_synthetic(self) -> None:
        events = [
            _event(1, "debug.status", {"count": 1}),
            _event(2, "debug.effect.synthetic", _synthetic("0xA", "0xB")),
        ]
        self.assertIsNone(p.get_status_after_synthetic(events))


class TestAddMatchingEvents(unittest.TestCase):
    def test_COLLECTS_only_interesting_kinds(self) -> None:
        bucket = []
        items = [
            _event(1, "debug.status"),
            _event(2, "chatter.event"),
            _event(3, "debug.board-stats"),
        ]
        p.add_matching_events(bucket, items)
        self.assertEqual(len(bucket), 2)

    def test_IGNORES_non_dict_items(self) -> None:
        bucket = []
        p.add_matching_events(bucket, ["not a dict", 42, None])
        self.assertEqual(len(bucket), 0)


# ---------------------------------------------------------------------------
# Assertion function tests
# ---------------------------------------------------------------------------

class TestApplyAssert(unittest.TestCase):
    """The most complex assertion — every branch is pinned."""

    def test_PASSING_scenario(self) -> None:
        result = p.test_apply_assert(PASSING_APPLY_EVENTS, "wither", False)
        self.assertIs(result["pass"], True)

    def test_FAIL_when_no_plant(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xAAA", "0xZ")),
            _event(3, "debug.run-steps.done"),
        ]
        result = p.test_apply_assert(events, "wither", False)
        self.assertIs(result["pass"], False)
        self.assertIn("no plant", result["note"])

    def test_FAIL_when_no_zombies(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.run-steps.done"),
        ]
        result = p.test_apply_assert(events, "wither", False)
        self.assertIs(result["pass"], False)
        self.assertIn("no zombies", result["note"])

    def test_FAIL_when_no_synthetic(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.run-steps.done"),
        ]
        result = p.test_apply_assert(events, "wither", False)
        self.assertIs(result["pass"], False)
        self.assertIn("no debug.effect.synthetic", result["note"])

    def test_FAIL_when_actor_not_plant(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xWRONG", "0xZ")),
            _event(3, "debug.run-steps.done"),
        ]
        result = p.test_apply_assert(events, "wither", False)
        self.assertIs(result["pass"], False)
        self.assertIn("actorPtr", result["note"])

    def test_FAIL_when_target_not_zombie(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xNOTZOMBIE")),
            _event(3, "debug.run-steps.done"),
        ]
        result = p.test_apply_assert(events, "wither", False)
        self.assertIs(result["pass"], False)
        self.assertIn("not a board zombie", result["note"])

    def test_FAIL_when_actor_equals_target(self) -> None:
        """actor==target requires the plant and zombie to share a pointer."""
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xSAME")], zombies=[_zombie("0xSAME")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xSAME", "0xSAME")),
            _event(3, "debug.run-steps.done"),
        ]
        result = p.test_apply_assert(events, "wither", False)
        self.assertIs(result["pass"], False)
        self.assertIn("actorPtr==targetPtr", result["note"])

    def test_FAIL_when_cc_required_but_actions_zero(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ", 0)),
            _event(3, "debug.status", _status(instances=[_instance("butter", "0xP", "0xZ")])),
            _event(4, "debug.run-steps.done"),
        ]
        result = p.test_apply_assert(events, "butter", True)
        self.assertIs(result["pass"], False)
        self.assertIn("actions=0", result["note"])

    def test_PASS_when_cc_not_required_and_actions_zero(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ", 0)),
            _event(3, "debug.status", _status(instances=[_instance("wither", "0xP", "0xZ")])),
            _event(4, "debug.run-steps.done"),
        ]
        result = p.test_apply_assert(events, "wither", False)
        self.assertIs(result["pass"], True)

    def test_FAIL_when_no_instance_for_status(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.status", _status(instances=[])),
            _event(4, "debug.run-steps.done"),
        ]
        result = p.test_apply_assert(events, "wither", False)
        self.assertIs(result["pass"], False)
        self.assertIn("no instance", result["note"])

    def test_FAIL_when_attacker_equals_host(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.status", _status(instances=[_instance("wither", "0xZ", "0xZ")])),
            _event(4, "debug.run-steps.done"),
        ]
        result = p.test_apply_assert(events, "wither", False)
        self.assertIs(result["pass"], False)
        self.assertIn("attackerPtr==hostPtr", result["note"])

    def test_FAIL_when_attacker_not_plant_or_actor(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.status", _status(instances=[_instance("wither", "0xWRONG", "0xZ")])),
            _event(4, "debug.run-steps.done"),
        ]
        result = p.test_apply_assert(events, "wither", False)
        self.assertIs(result["pass"], False)
        self.assertIn("attackerPtr", result["note"])


class TestContagionAssert(unittest.TestCase):
    def _events(self, hosts: list, seed_count: int = 2, control_count: int = 0) -> list:
        """Build events for a contagion scenario."""
        zombies = [_zombie(h) for h in hosts]
        instances = [_instance("blight", "0xP", h) for h in hosts]
        return [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=zombies)),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", hosts[0] if hosts else "0xZ")),
            _event(3, "debug.status", _status(instances=instances)),
            _event(4, "debug.run-steps.done"),
        ]

    def test_PASSING_contagion(self) -> None:
        events = self._events(["0xH1", "0xH2"])
        result = p.test_contagion_assert(events, "blight", 2, -1, -1)
        self.assertIs(result["pass"], True)

    def test_FAIL_when_too_few_hosts(self) -> None:
        events = self._events(["0xH1"])
        result = p.test_contagion_assert(events, "blight", 2, -1, -1)
        self.assertIs(result["pass"], False)
        self.assertIn("hosts=1", result["note"])

    def test_PASS_when_seed_row_negative(self) -> None:
        """seed_row < 0 means no row check."""
        events = self._events(["0xH1", "0xH2"])
        result = p.test_contagion_assert(events, "blight", 2, -1, -1)
        self.assertIs(result["pass"], True)

    def test_FAIL_when_control_row_has_hosts(self) -> None:
        """Control row 3 has a host — should fail."""
        zombies = [_zombie("0xH1", row=2), _zombie("0xH2", row=2), _zombie("0xH3", row=3)]
        instances = [_instance("blight", "0xP", "0xH1"),
                     _instance("blight", "0xP", "0xH2"),
                     _instance("blight", "0xP", "0xH3")]
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=zombies)),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xH1")),
            _event(3, "debug.status", _status(instances=instances)),
            _event(4, "debug.run-steps.done"),
        ]
        result = p.test_contagion_assert(events, "blight", 2, 2, 3)
        self.assertIs(result["pass"], False)
        self.assertIn("control row 3", result["note"])

    def test_FAIL_when_seed_row_missing_hosts(self) -> None:
        """Seed row 2 has no hosts — should fail."""
        zombies = [_zombie("0xH1", row=1), _zombie("0xH2", row=1)]
        instances = [_instance("blight", "0xP", "0xH1"),
                     _instance("blight", "0xP", "0xH2")]
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=zombies)),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xH1")),
            _event(3, "debug.status", _status(instances=instances)),
            _event(4, "debug.run-steps.done"),
        ]
        result = p.test_contagion_assert(events, "blight", 2, 2, 3)
        self.assertIs(result["pass"], False)
        self.assertIn("seed row 2", result["note"])


class TestResistAssert(unittest.TestCase):
    def test_PASSING_resist(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.status.resisted", {"statusId": "wither", "reason": "PotencyFloor", "hostPtr": "0xZ"}),
            _event(4, "debug.status", _status(instances=[])),
            _event(5, "debug.run-steps.done"),
        ]
        result = p.test_resist_assert(events, "wither", "PotencyFloor")
        self.assertIs(result["pass"], True)

    def test_FAIL_when_no_resisted_event(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.status", _status(instances=[])),
            _event(4, "debug.run-steps.done"),
        ]
        result = p.test_resist_assert(events, "wither", "PotencyFloor")
        self.assertIs(result["pass"], False)
        self.assertIn("no resisted", result["note"])

    def test_FAIL_when_instance_present(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.status.resisted", {"statusId": "wither", "reason": "PotencyFloor", "hostPtr": "0xZ"}),
            _event(4, "debug.status", _status(instances=[_instance("wither", "0xP", "0xZ")])),
            _event(5, "debug.run-steps.done"),
        ]
        result = p.test_resist_assert(events, "wither", "PotencyFloor")
        self.assertIs(result["pass"], False)
        self.assertIn("instance present", result["note"])


class TestResistContagionAssert(unittest.TestCase):
    def test_PASSING(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.status.resisted", {"statusId": "blight", "reason": "PotencyFloor", "hostPtr": "0xZ"}),
            _event(4, "debug.status", _status(instances=[_instance("blight", "0xP", "0xZ")])),
            _event(5, "debug.run-steps.done"),
        ]
        result = p.test_resist_contagion_assert(events)
        self.assertIs(result["pass"], True)

    def test_FAIL_when_no_blight_instance(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.status.resisted", {"statusId": "blight", "reason": "PotencyFloor", "hostPtr": "0xZ"}),
            _event(4, "debug.status", _status(instances=[])),
            _event(5, "debug.run-steps.done"),
        ]
        result = p.test_resist_contagion_assert(events)
        self.assertIs(result["pass"], False)
        self.assertIn("seed blight missing", result["note"])

    def test_FAIL_when_no_resisted(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.status", _status(instances=[_instance("blight", "0xP", "0xZ")])),
            _event(4, "debug.run-steps.done"),
        ]
        result = p.test_resist_contagion_assert(events)
        self.assertIs(result["pass"], False)
        self.assertIn("did not resist", result["note"])


class TestBondAssert(unittest.TestCase):
    def test_PASSING_with_5_synthetics(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(4, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(5, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(6, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(7, "debug.status", _status(instances=[_instance("bond", "0xP", "0xZ")])),
            _event(8, "debug.run-steps.done"),
        ]
        result = p.test_bond_assert(events)
        self.assertIs(result["pass"], True)

    def test_FAIL_when_too_few_synthetics(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(4, "debug.status", _status(instances=[_instance("bond", "0xP", "0xZ")])),
            _event(5, "debug.run-steps.done"),
        ]
        result = p.test_bond_assert(events)
        self.assertIs(result["pass"], False)
        self.assertIn("synthetic hits=2", result["note"])

    def test_PASSING_with_fa10_burst(self) -> None:
        """fa10 burst is a NOTE, not an alternative to >=5 synthetics."""
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(4, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(5, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(6, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(7, "debug.combat.packet", {"fa10": 1}),
            _event(8, "debug.status", _status(instances=[_instance("bond", "0xP", "0xZ")])),
            _event(9, "debug.run-steps.done"),
        ]
        result = p.test_bond_assert(events)
        self.assertIs(result["pass"], True)
        self.assertIn("fa10 packet", result["note"])


class TestActorDerivedAssert(unittest.TestCase):
    def test_PASSING(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.actor-derived", {
                "ptr": "0xP",
                "channels": {"status.power.omni": 100},
            }),
            _event(4, "debug.status", _status(instances=[_instance("wither", "0xP", "0xZ")])),
            _event(5, "debug.run-steps.done"),
        ]
        result = p.test_actor_derived_assert(events)
        self.assertIs(result["pass"], True)

    def test_PASSING_nested_power(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.actor-derived", {
                "ptr": "0xP",
                "channels": {"status": {"power": {"omni": 150}}},
            }),
            _event(4, "debug.status", _status(instances=[_instance("wither", "0xP", "0xZ")])),
            _event(5, "debug.run-steps.done"),
        ]
        result = p.test_actor_derived_assert(events)
        self.assertIs(result["pass"], True)

    def test_FAIL_when_no_actor_derived(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.status", _status(instances=[_instance("wither", "0xP", "0xZ")])),
            _event(4, "debug.run-steps.done"),
        ]
        result = p.test_actor_derived_assert(events)
        self.assertIs(result["pass"], False)
        self.assertIn("no debug.actor-derived", result["note"])

    def test_FAIL_when_power_below_100(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.actor-derived", {
                "ptr": "0xP",
                "channels": {"status.power.omni": 50},
            }),
            _event(4, "debug.status", _status(instances=[_instance("wither", "0xP", "0xZ")])),
            _event(5, "debug.run-steps.done"),
        ]
        result = p.test_actor_derived_assert(events)
        self.assertIs(result["pass"], False)
        self.assertIn("caster pin not seen", result["note"])

    def test_FAIL_when_ptr_not_matching(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.actor-derived", {
                "ptr": "0xOTHER",
                "channels": {"status.power.omni": 100},
            }),
            _event(4, "debug.status", _status(instances=[_instance("wither", "0xP", "0xZ")])),
            _event(5, "debug.run-steps.done"),
        ]
        result = p.test_actor_derived_assert(events)
        self.assertIs(result["pass"], False)
        self.assertIn("caster pin not seen", result["note"])


class TestSnapshotAssert(unittest.TestCase):
    def test_PASSING(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.status", _status(instances=[_instance("wither", "0xP", "0xZ")],
                                              resisted=[{"statusId": "wither"}],
                                              count=1, resisted_count=1)),
            _event(4, "debug.run-steps.done"),
        ]
        result = p.test_snapshot_assert(events)
        self.assertIs(result["pass"], True)

    def test_FAIL_when_no_resisted_array(self) -> None:
        events = [
            _event(1, "debug.board-stats", _board(plants=[_plant("0xP")], zombies=[_zombie("0xZ")])),
            _event(2, "debug.effect.synthetic", _synthetic("0xP", "0xZ")),
            _event(3, "debug.status", _status(instances=[_instance("wither", "0xP", "0xZ")])),
            _event(4, "debug.run-steps.done"),
        ]
        result = p.test_snapshot_assert(events)
        self.assertIs(result["pass"], False)
        self.assertIn("missing resisted", result["note"])

    def test_FAIL_when_apply_fails(self) -> None:
        """When the apply assert fails, the snapshot assert returns that failure."""
        events = [
            _event(1, "debug.run-steps.done"),
        ]
        result = p.test_snapshot_assert(events)
        self.assertIs(result["pass"], False)
        self.assertIn("no plant", result["note"])


class TestUnityBypassAssert(unittest.TestCase):
    def test_PASSING_applied(self) -> None:
        events = [
            _event(1, "debug.status.applied", {"status": "butter", "method": True, "count": 1}),
            _event(2, "debug.run-steps.done"),
        ]
        result = p.test_unity_bypass_assert(events, "butter", True, False)
        self.assertIs(result["pass"], True)

    def test_PASSING_cleared(self) -> None:
        events = [
            _event(1, "debug.status.cleared", {"count": 3}),
            _event(2, "debug.run-steps.done"),
        ]
        result = p.test_unity_bypass_assert(events, "", False, True)
        self.assertIs(result["pass"], True)

    def test_FAIL_when_wrong_status(self) -> None:
        events = [
            _event(1, "debug.status.applied", {"status": "freeze", "method": True, "count": 1}),
            _event(2, "debug.run-steps.done"),
        ]
        result = p.test_unity_bypass_assert(events, "butter", True, False)
        self.assertIs(result["pass"], False)
        self.assertIn("applied status=freeze", result["note"])

    def test_FAIL_when_wrong_method(self) -> None:
        events = [
            _event(1, "debug.status.applied", {"status": "butter", "method": False, "count": 1}),
            _event(2, "debug.run-steps.done"),
        ]
        result = p.test_unity_bypass_assert(events, "butter", True, False)
        self.assertIs(result["pass"], False)
        self.assertIn("applied method=False", result["note"])

    def test_FAIL_when_no_applied_event(self) -> None:
        events = [_event(1, "debug.run-steps.done")]
        result = p.test_unity_bypass_assert(events, "butter", True, False)
        self.assertIs(result["pass"], False)
        self.assertIn("no debug.status.applied", result["note"])


# ---------------------------------------------------------------------------
# Scenario matrix tests
# ---------------------------------------------------------------------------

class TestScenarioMatrix(unittest.TestCase):
    """The matrix is a closed vocabulary — a port that adds or drops a row is a different tool."""

    def test_L2_matrix_has_27_scenarios(self) -> None:
        self.assertEqual(len(p.L2_SCENARIOS), 27)

    def test_UNITY_matrix_has_6_scenarios(self) -> None:
        self.assertEqual(len(p.UNITY_SCENARIOS), 6)

    def test_L2_scenario_ids_are_unique(self) -> None:
        ids = [s["id"] for s in p.L2_SCENARIOS]
        self.assertEqual(len(ids), len(set(ids)))

    def test_UNITY_scenario_ids_are_unique(self) -> None:
        ids = [s["id"] for s in p.UNITY_SCENARIOS]
        self.assertEqual(len(ids), len(set(ids)))

    def test_L2_first_scenario_is_wither(self) -> None:
        self.assertEqual(p.L2_SCENARIOS[0]["id"], "status-l2-wither")

    def test_L2_last_scenario_is_actor_derived(self) -> None:
        self.assertEqual(p.L2_SCENARIOS[-1]["id"], "status-l2-actor-derived")

    def test_UNITY_first_scenario_is_butter(self) -> None:
        self.assertEqual(p.UNITY_SCENARIOS[0]["id"], "status-butter")

    def test_UNITY_last_scenario_is_clear(self) -> None:
        self.assertEqual(p.UNITY_SCENARIOS[-1]["id"], "status-clear")

    def test_L2_kinds_are_a_closed_set(self) -> None:
        kinds = {s["kind"] for s in p.L2_SCENARIOS}
        self.assertEqual(kinds, {"apply", "snapshot", "resist", "resist-contagion",
                                  "contagion-row", "contagion", "bond", "actor-derived"})

    def test_UNITY_kinds_are_all_unity(self) -> None:
        kinds = {s["kind"] for s in p.UNITY_SCENARIOS}
        self.assertEqual(kinds, {"unity"})

    def test_L2_cc_scenarios_are_the_second_half(self) -> None:
        """Scenarios 15-23 (butter through charm_pulse) have cc=True."""
        cc_scenarios = [s for s in p.L2_SCENARIOS if s.get("cc") is True]
        self.assertEqual(len(cc_scenarios), 9)
        self.assertEqual(cc_scenarios[0]["id"], "status-l2-butter")
        self.assertEqual(cc_scenarios[-1]["id"], "status-l2-charm-pulse")

    def test_L2_resist_scenarios_have_reason(self) -> None:
        resist = [s for s in p.L2_SCENARIOS if s["kind"] == "resist"]
        for s in resist:
            self.assertIn("reason", s, f"{s['id']} missing reason")

    def test_L2_contagion_row_has_row_fields(self) -> None:
        row = [s for s in p.L2_SCENARIOS if s["kind"] == "contagion-row"]
        self.assertEqual(len(row), 1)
        self.assertEqual(row[0]["seedRow"], 2)
        self.assertEqual(row[0]["controlRow"], 3)


# ---------------------------------------------------------------------------
# wait_and_collect tests
# ---------------------------------------------------------------------------

class TestWaitAndCollect(unittest.TestCase):
    """The polling loop — bounded, with a done marker and tail collection."""

    def test_RETURNS_got_done_when_marker_appears(self) -> None:
        pages = [
            [{"id": 1, "kind": "chatter"}],
            [{"id": 2, "kind": "debug.run-steps.done"}],
        ]
        seen = {"n": 0}

        def get_events(base_url, after_id, limit, timeout):
            page = pages[min(seen["n"], len(pages) - 1)]
            seen["n"] += 1
            return page

        with mock.patch.object(p.lib, "get_events", get_events), \
                mock.patch.object(p.lib, "invoke_debug_post", lambda *a, **k: {}):
            result = p.wait_and_collect("http://x", 0, 5.0, 0, True, "")
        self.assertIs(result.got_done, True)
        self.assertTrue(any(e["kind"] == "debug.run-steps.done" for e in result.events))

    def test_RETURNS_not_done_when_timeout_expires(self) -> None:
        def get_events(base_url, after_id, limit, timeout):
            return [{"id": 1, "kind": "chatter"}]

        with mock.patch.object(p.lib, "get_events", get_events), \
                mock.patch.object(p.lib, "invoke_debug_post", lambda *a, **k: {}):
            result = p.wait_and_collect("http://x", 0, 0.5, 0, True, "")
        self.assertIs(result.got_done, False)

    def test_EVERY_call_carries_a_timeout(self) -> None:
        calls = []

        def get_events(base_url, after_id, limit, timeout):
            calls.append({"afterId": after_id, "limit": limit, "timeout": timeout})
            return [{"id": 1, "kind": "chatter"}]

        with mock.patch.object(p.lib, "get_events", get_events), \
                mock.patch.object(p.lib, "invoke_debug_post", lambda *a, **k: {}):
            p.wait_and_collect("http://x", 0, 0.3, 0, True, "")
        self.assertTrue(calls)
        for call in calls:
            self.assertIsNotNone(call["timeout"], f"an unbounded poll: {call}")

    def test_refresh_status_posts_board_stats_and_status(self) -> None:
        posts = []

        def get_events(base_url, after_id, limit, timeout):
            return [{"id": 1, "kind": "debug.run-steps.done"}]

        with mock.patch.object(p.lib, "get_events", get_events), \
                mock.patch.object(p.lib, "invoke_debug_post",
                                 lambda url, path, body=None, timeout=15: posts.append(path) or {}):
            p.wait_and_collect("http://x", 0, 5.0, 0, True, "")
        self.assertIn("/board-stats", posts)
        self.assertIn("/status", posts)

    def test_no_refresh_when_refresh_status_is_False(self) -> None:
        posts = []

        def get_events(base_url, after_id, limit, timeout):
            return [{"id": 1, "kind": "debug.run-steps.done"}]

        with mock.patch.object(p.lib, "get_events", get_events), \
                mock.patch.object(p.lib, "invoke_debug_post",
                                 lambda url, path, body=None, timeout=15: posts.append(path) or {}):
            p.wait_and_collect("http://x", 0, 5.0, 0, False, "")
        self.assertEqual(posts, [])


# ---------------------------------------------------------------------------
# invoke_scenario_row tests
# ---------------------------------------------------------------------------

class TestInvokeScenarioRow(unittest.TestCase):
    """The scenario runner — cursor, POST, wait, assert."""

    def _passing_bundle(self):
        return p.CollectResult(
            events=list(PASSING_APPLY_EVENTS),
            got_done=True,
            cursor=100,
        )

    def test_PASSING_scenario(self) -> None:
        row = {"id": "status-l2-wither", "waitSec": 0, "kind": "apply",
               "statusId": "wither", "cc": False}
        with mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                mock.patch.object(p.lib, "invoke_debug_post", return_value={"ok": True}), \
                mock.patch.object(p, "wait_and_collect", return_value=self._passing_bundle()):
            result = p.invoke_scenario_row(row, "http://x")
        self.assertIs(result["pass"], True)
        self.assertEqual(result["id"], "status-l2-wither")

    def test_FAIL_when_scenario_queue_fails(self) -> None:
        row = {"id": "status-l2-wither", "waitSec": 0, "kind": "apply",
               "statusId": "wither", "cc": False}
        with mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                mock.patch.object(p.lib, "invoke_debug_post", return_value={"ok": False}):
            result = p.invoke_scenario_row(row, "http://x")
        self.assertIs(result["pass"], False)
        self.assertIn("scenario queue failed", result["note"])

    def test_FAIL_when_scenario_POST_refused(self) -> None:
        row = {"id": "status-l2-wither", "waitSec": 0, "kind": "apply",
               "statusId": "wither", "cc": False}

        def refuse(*a, **k):
            raise p.lib.Refusal("DEBUG-POST-FAILED", "connection refused")

        with mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                mock.patch.object(p.lib, "invoke_debug_post", refuse):
            result = p.invoke_scenario_row(row, "http://x")
        self.assertIs(result["pass"], False)
        self.assertIn("REFUSED", result["note"])

    def test_FAIL_when_no_done_marker(self) -> None:
        row = {"id": "status-l2-wither", "waitSec": 0, "kind": "apply",
               "statusId": "wither", "cc": False}
        bundle = p.CollectResult(events=[], got_done=False, cursor=0)
        with mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                mock.patch.object(p.lib, "invoke_debug_post", return_value={"ok": True}), \
                mock.patch.object(p, "wait_and_collect", return_value=bundle):
            result = p.invoke_scenario_row(row, "http://x")
        self.assertIs(result["pass"], False)
        self.assertIn("no debug.run-steps.done", result["note"])

    def test_FAIL_when_assertion_fails(self) -> None:
        row = {"id": "status-l2-wither", "waitSec": 0, "kind": "apply",
               "statusId": "wither", "cc": False}
        bad_events = [_event(1, "debug.run-steps.done")]
        bundle = p.CollectResult(events=bad_events, got_done=True, cursor=100)
        with mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                mock.patch.object(p.lib, "invoke_debug_post", return_value={"ok": True}), \
                mock.patch.object(p, "wait_and_collect", return_value=bundle):
            result = p.invoke_scenario_row(row, "http://x")
        self.assertIs(result["pass"], False)

    def test_unknown_kind_fails(self) -> None:
        row = {"id": "status-l2-bogus", "waitSec": 0, "kind": "bogus"}
        bundle = self._passing_bundle()
        with mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                mock.patch.object(p.lib, "invoke_debug_post", return_value={"ok": True}), \
                mock.patch.object(p, "wait_and_collect", return_value=bundle):
            result = p.invoke_scenario_row(row, "http://x")
        self.assertIs(result["pass"], False)
        self.assertIn("unknown kind", result["note"])


# ---------------------------------------------------------------------------
# CLI surface tests
# ---------------------------------------------------------------------------

class TestCliSurface(unittest.TestCase):
    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-BaseUrl", "-IncludeUnityBypass", "-SkipVisual", "-OutJson"):
            with self.subTest(flag=flag):
                proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"],
                                      capture_output=True, text=True, timeout=RUN_TIMEOUT)
                self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_IMPORTS_the_shared_library(self) -> None:
        code = code_without_docstrings(SCRIPT)
        self.assertIn("import live_lawn_setup as lib", code)
        for name in ("get_debug_max_event_id", "get_events", "invoke_debug_post"):
            self.assertIn(f"lib.{name}", code, f"{name} is used without the shared library")

    def test_the_PATH_insert_runs_BEFORE_the_import(self) -> None:
        lines = [ln.strip() for ln in SCRIPT.read_text(encoding="utf-8").splitlines()]
        insert = next(i for i, ln in enumerate(lines) if ln.startswith("sys.path.insert"))
        imported = next(i for i, ln in enumerate(lines) if ln.startswith("import live_lawn_setup"))
        self.assertLess(insert, imported, "the path insert must come before the import")

    def test_it_states_WHY_PowerShell_WAS_retired(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("prove-status-full.ps1", head)
        self.assertIn("WHY THE POWERSHELL FORM WAS RETIRED", head)
        lowered = head.lower()
        for reason in ("timeout", "hardcoded", "machine-readable", "catch"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        """The refusal reasons are declared in REFUSAL_REASONS and nowhere else."""
        source = SCRIPT.read_text(encoding="utf-8")
        import re
        # Find all string literals that look like refusal reasons (UPPERCASE_WITH_HYPHENS)
        found = set(re.findall(r'"([A-Z][A-Z-]+)"', source))
        # Filter to only those that are actually used as refusal reasons
        refusal_like = {r for r in found if r in p.REFUSAL_REASONS}
        self.assertTrue(refusal_like, "no refusal reasons found at all")
        # Any UPPERCASE_WITH_HYPHENS string that is NOT in REFUSAL_REASONS is a potential undeclared reason
        undeclared = {r for r in found if r not in p.REFUSAL_REASONS and "-" in r and len(r) > 3}
        # Allow common non-refusal strings
        allowed = {"GET", "POST", "UTF-8", "NO-BOM"}
        undeclared -= allowed
        self.assertEqual(undeclared, set(),
                         f"undeclared refusal reason(s) {sorted(undeclared)}")

    def test_the_defaults_survive_the_port(self) -> None:
        self.assertEqual(p.DONE_TIMEOUT_SEC, 20.0)
        self.assertEqual(p.EVENT_PAGE, 500)
        self.assertEqual(p.DONE_KIND, "debug.run-steps.done")
        self.assertEqual(p.EXIT_REFUSED, 64)

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        globals_seen = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "urllib", "http",
                        "socket", "threading", "time", "importlib", "ast", "re", "io"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Attribute) and target.attr == "lib":
                continue
            if isinstance(target, ast.Name) and target.id == "p":
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in globals_seen:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))


# ---------------------------------------------------------------------------
# End-to-end run tests (with mocked library)
# ---------------------------------------------------------------------------

class TestEndToEnd(unittest.TestCase):
    """Drive `run()` to a verdict with a scripted event stream."""

    def _run(self, health, scenario_results):
        """Run `run()` with mocked library functions. Returns (exit_code, payload)."""
        out_json = Path(os.environ.get("TEMP_DIR", "/tmp")) / "_prove-status-full-test.json"

        def get_events(base_url, after_id, limit, timeout):
            return [{"id": 1, "kind": "debug.run-steps.done"}]

        def invoke_debug_post(base_url, path, body=None, timeout=15):
            if path == "/session/end":
                return {}
            if path == "/session/start":
                return {}
            if path.startswith("/scenario/"):
                return {"ok": True}
            return {}

        with mock.patch.object(p.lib, "resolve_base_url", return_value=("http://x", "test")), \
                mock.patch.object(p.lib, "_get_json", return_value=health), \
                mock.patch.object(p.lib, "invoke_debug_post", invoke_debug_post), \
                mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                mock.patch.object(p.lib, "get_events", get_events), \
                mock.patch.object(p, "invoke_scenario_row",
                                 side_effect=lambda row, url, et=10: scenario_results.get(
                                     row["id"], {"id": row["id"], "pass": False, "note": "not mocked"})):
            out = io.StringIO()
            with redirect_stdout(out), redirect_stderr(io.StringIO()):
                code = p.run("", False, False, out_json)
        payload = json.loads(out_json.read_text(encoding="utf-8"))
        return code, payload

    def test_ALL_PASS_exits_ZERO(self) -> None:
        health = {"ok": True, "injectorConnected": True, "simEnabled": False, "source": "test"}
        results = {s["id"]: {"id": s["id"], "pass": True, "note": "ok"}
                   for s in p.L2_SCENARIOS}
        code, payload = self._run(health, results)
        self.assertEqual(code, 0)
        self.assertEqual(payload["status"], "PASS")
        self.assertEqual(payload["passed"], 27)
        self.assertEqual(payload["total"], 27)

    def test_ONE_FAIL_exits_ONE(self) -> None:
        health = {"ok": True, "injectorConnected": True, "simEnabled": False, "source": "test"}
        results = {s["id"]: {"id": s["id"], "pass": True, "note": "ok"}
                   for s in p.L2_SCENARIOS}
        results["status-l2-wither"] = {"id": "status-l2-wither", "pass": False, "note": "failed"}
        code, payload = self._run(health, results)
        self.assertEqual(code, 1)
        self.assertEqual(payload["status"], "FAIL")
        self.assertEqual(payload["passed"], 26)
        self.assertEqual(payload["total"], 27)

    def test_HEALTH_FAILURE_exits_ONE(self) -> None:
        health = {"ok": True, "injectorConnected": False, "simEnabled": False, "source": "test"}
        code, payload = self._run(health, {})
        self.assertEqual(code, 1)
        self.assertEqual(payload["status"], "FAIL")
        self.assertEqual(payload["passed"], 0)
        self.assertEqual(payload["total"], 0)
        self.assertIn("injectorConnected", payload["note"])

    def test_SIM_ENABLED_exits_ONE(self) -> None:
        health = {"ok": True, "injectorConnected": True, "simEnabled": True, "source": "test"}
        code, payload = self._run(health, {})
        self.assertEqual(code, 1)
        self.assertEqual(payload["status"], "FAIL")
        self.assertIn("simEnabled", payload["note"])

    def test_INCLUDE_UNITY_BYPASS_adds_unity_scenarios(self) -> None:
        health = {"ok": True, "injectorConnected": True, "simEnabled": False, "source": "test"}
        all_scenarios = {s["id"]: {"id": s["id"], "pass": True, "note": "ok"}
                         for s in p.L2_SCENARIOS + p.UNITY_SCENARIOS}
        out_json = Path(os.environ.get("TEMP_DIR", "/tmp")) / "_prove-status-full-test.json"

        def get_events(base_url, after_id, limit, timeout):
            return [{"id": 1, "kind": "debug.run-steps.done"}]

        def invoke_debug_post(base_url, path, body=None, timeout=15):
            return {"ok": True}

        with mock.patch.object(p.lib, "resolve_base_url", return_value=("http://x", "test")), \
                mock.patch.object(p.lib, "_get_json", return_value=health), \
                mock.patch.object(p.lib, "invoke_debug_post", invoke_debug_post), \
                mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                mock.patch.object(p.lib, "get_events", get_events), \
                mock.patch.object(p, "invoke_scenario_row",
                                 side_effect=lambda row, url, et=10: all_scenarios.get(
                                     row["id"], {"id": row["id"], "pass": False, "note": "not mocked"})):
            with redirect_stderr(io.StringIO()):
                code = p.run("", True, False, out_json)
        payload = json.loads(out_json.read_text(encoding="utf-8"))
        self.assertEqual(code, 0)
        self.assertEqual(payload["total"], 33)  # 27 L2 + 6 unity

    def test_SKIP_VISUAL_overrides_include_unity_bypass(self) -> None:
        health = {"ok": True, "injectorConnected": True, "simEnabled": False, "source": "test"}
        all_scenarios = {s["id"]: {"id": s["id"], "pass": True, "note": "ok"}
                         for s in p.L2_SCENARIOS + p.UNITY_SCENARIOS}
        out_json = Path(os.environ.get("TEMP_DIR", "/tmp")) / "_prove-status-full-test.json"

        def get_events(base_url, after_id, limit, timeout):
            return [{"id": 1, "kind": "debug.run-steps.done"}]

        def invoke_debug_post(base_url, path, body=None, timeout=15):
            return {"ok": True}

        with mock.patch.object(p.lib, "resolve_base_url", return_value=("http://x", "test")), \
                mock.patch.object(p.lib, "_get_json", return_value=health), \
                mock.patch.object(p.lib, "invoke_debug_post", invoke_debug_post), \
                mock.patch.object(p.lib, "get_debug_max_event_id", return_value=0), \
                mock.patch.object(p.lib, "get_events", get_events), \
                mock.patch.object(p, "invoke_scenario_row",
                                 side_effect=lambda row, url, et=10: all_scenarios.get(
                                     row["id"], {"id": row["id"], "pass": False, "note": "not mocked"})):
            with redirect_stderr(io.StringIO()):
                code = p.run("", True, True, out_json)  # skip_visual=True
        payload = json.loads(out_json.read_text(encoding="utf-8"))
        self.assertEqual(code, 0)
        self.assertEqual(payload["total"], 27)  # skip_visual overrides include_unity_bypass


# ---------------------------------------------------------------------------
# Against a real server (refusal path)
# ---------------------------------------------------------------------------

class TestAgainstRealServer(unittest.TestCase):
    """The refusal path is what every live script does until a game is running."""

    def test_unreachable_server_writes_FAIL_json(self) -> None:
        port = closed_port()
        out_json = Path(os.environ.get("TEMP_DIR", "/tmp")) / "_prove-status-full-test.json"
        out = io.StringIO()
        with redirect_stdout(out), redirect_stderr(io.StringIO()):
            code = p.main(["--json", "--base-url", f"http://127.0.0.1:{port}",
                            "--out-json", str(out_json)])
        self.assertEqual(code, 1)
        payload = json.loads(out_json.read_text(encoding="utf-8"))
        self.assertEqual(payload["status"], "FAIL")
        self.assertEqual(payload["passed"], 0)
        self.assertEqual(payload["total"], 0)

    def test_server_with_no_injector_writes_FAIL_json(self) -> None:
        server, base = serve_health({"ok": True, "injectorConnected": False, "simEnabled": False})
        try:
            out_json = Path(os.environ.get("TEMP_DIR", "/tmp")) / "_prove-status-full-test.json"
            out = io.StringIO()
            with redirect_stdout(out), redirect_stderr(io.StringIO()):
                code = p.main(["--json", "--base-url", base, "--out-json", str(out_json)])
        finally:
            server.shutdown()
        self.assertEqual(code, 1)
        payload = json.loads(out_json.read_text(encoding="utf-8"))
        self.assertEqual(payload["status"], "FAIL")
        self.assertIn("injectorConnected", payload["note"])


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def serve_health(health: dict):
    """A REAL HTTP server answering /health with `health`."""
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *a):
            return

        def do_GET(self):
            body = json.dumps(health).encode()
            self.send_response(200)
            self.send_header("Content-Type", "application/json")
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)

    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{port}"


def closed_port() -> int:
    """A port nothing is listening on: bound, read, released."""
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        return probe.getsockname()[1]


def code_without_docstrings(path: Path) -> str:
    """The module's CODE, with every docstring removed."""
    tree = ast.parse(path.read_text(encoding="utf-8"))
    for node in ast.walk(tree):
        if isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)):
            if (node.body and isinstance(node.body[0], ast.Expr)
                    and isinstance(node.body[0].value, ast.Constant)
                    and isinstance(node.body[0].value.value, str)):
                node.body.pop(0)
    return ast.unparse(tree)


if __name__ == "__main__":
    unittest.main()

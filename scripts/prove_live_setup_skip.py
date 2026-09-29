#!/usr/bin/env python3
"""PROVE: the live setup skip is acknowledged, and it is the acknowledged one rather than the sent one.

`/api/debug/setup/skip` is the endpoint a probe uses to get past the plant-selection panel. This script
sends the skip and then checks the game actually ACKNOWLEDGED it -- because a POST that returns 200 has
proved only that the server received a request, which is not the thing under test.

Usage:
  gk-core/scripts/prove_live_setup_skip.py
  gk-core/scripts/prove_live_setup_skip.py --method button --json

See `.claude/skills/live-lawn-quick-start/SKILL.md`.

Replaces `scripts/prove-live-setup-skip.ps1`.

WHAT IT PROVES, and why it is two checks and not one
-----------------------------------------------------
1. The server is up and an injector is connected -- otherwise a skip is a no-op and a 200 would be a lie.
2. The acknowledgement's OWN `method` is echoed back. **The method SENT and the method ACKNOWLEDGED are
   reported separately**, because they are separate facts: the server defaults an omitted method to
   `button`, while this script's own default is `quick`. A caller that reads only the sent value cannot
   tell which one the game acted on, and the acknowledgement is the one that describes the game.

WHY THE POWERSHELL FORM WAS RETIRED
-----------------------------------
* **THE ONLY BOUND ON THE SERVER'S ANSWER WAS REMOVABLE BY A PLAUSIBLE ARGUMENT, AND IT WAS NOT EVEN THE
  ARGUMENT YOU WOULD GUESS.** The POST timeout is `$TimeoutSec + 5`, so the value is SHIFTED BY FIVE
  before it reaches .NET. Measured against the running game:

      -TimeoutSec  0  ->  POST timeout  5s  ->  server 409, "did not ack within 0s"
      -TimeoutSec -5  ->  POST timeout  0s  ->  **zero, which .NET defines as INFINITE**
      -TimeoutSec -6  ->  POST timeout -1s  ->  PowerShell itself refuses (ConnectionTimeoutSeconds)

  So the infinite read is at **-5**, not at 0 -- and `-6` is caught by the framework while `-5` is not,
  because PowerShell validates the range it can see and 0 is a *legal* value meaning "no timeout". The
  dangerous argument therefore sits exactly one step inside the check, which is why nothing objected.
  A non-positive budget is now REFUSED before any request is made.

* **THE DEFAULT BASE URL WAS THE OWNER'S PORT, HARDCODED** -- `http://127.0.0.1:5088`, on a machine with a
  three-slot pool on 5101/5102/5103 where `5088` is the owner's own server. Resolved from
  `FUSIONRPG_SERVER_URL` -- the variable the Injector itself reads -- and its source is REPORTED.

* **EVERY FAILURE WAS A RAW `throw` WITH A POWERSHELL STACK TRACE.** `throw "injector is not connected"`
  surfaces as `Exception: ...prove-live-setup-skip.ps1:14` plus a source excerpt: a file, a line number and
  a caret, for a condition a caller has to act on. Every refusal here is NAMED, and the exit code names
  the stage.

* **THE METHOD VOCABULARY WAS ENFORCED ONLY CLIENT-SIDE, AND THE SERVER HAS A LARGER ONE.** The original's
  `[ValidateSet("quick","button")]` refuses before any request, which is right, but it does not match the
  server: an omitted method is accepted by the server and DEFAULTS to `button`, while an unknown one is
  refused with `unknown method — expected quick or button`. So the client set is a SUBSET of the server's
  accepted input, and the client's default is not the server's. Both facts are measured here and reported,
  because "which method did the game act on" is the question this script exists to answer.

* **THE SUCCESS LINE MIXED SENT AND ACKNOWLEDGED.** One `Write-Host` printed `$result.method` and a
  re-serialised acknowledgement on the same line, with no machine-readable form and no separation.

* **NO MACHINE-READABLE VERDICT.** `--json` reports the base URL and its source, the method sent, the
  method acknowledged, the acknowledgement itself, and the refusal when there is none.

DELIBERATELY UNCHANGED
----------------------
The same three inputs and defaults (`quick`, 15s), the same 5s health timeout, the same two refusal
conditions in the same order (health first, then injector -- checking the injector first would name the
wrong thing when there is no server at all), the same `ok` check, and **the human instruction on the last
two lines**, which is the step a person still has to take: confirm the plant-selection panel advanced and
the game is progressing before continuing the probe. An automated tool cannot observe that, and dropping
the line would leave a probe continuing against a board that never started.
"""
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent / "lib"))

import live_lawn_setup as lib  # noqa: E402  (the path insert above must run first)

TOOL_ID = "prove-live-setup-skip"

# The server's vocabulary, measured against a real injector rather than read off the script:
#   method "quick"  -> 200, acknowledgement.method == "quick"
#   method "button" -> 200, acknowledgement.method == "button"
#   method "toggle" -> 400 {"ok":false,"error":"unknown method — expected quick or button"}
#   method omitted  -> 200, acknowledgement.method == "button"   (the SERVER's default, not this tool's)
METHODS = ("quick", "button")
DEFAULT_METHOD = "quick"
# Recorded because it is a real asymmetry a caller can otherwise be surprised by: this tool defaults to
# `quick`, and a request that omits the method entirely is accepted by the server as `button`.
SERVER_DEFAULT_METHOD = "button"
SERVER_METHOD_ERROR = "unknown method"

DEFAULT_TIMEOUT_SEC = 15
HEALTH_TIMEOUT = 5
POST_TIMEOUT_SLACK_SEC = 5

REFUSAL_REASONS = {
    "INVALID-TIMEOUT", "INVALID-METHOD", "SERVER-UNREACHABLE", "HEALTH-NOT-OK", "INJECTOR-NOT-CONNECTED",
    "SETUP-SKIP-FAILED", "SETUP-SKIP-NOT-ACKNOWLEDGED", "UNREADABLE-ACKNOWLEDGEMENT", "METHOD-MISMATCH",
}

_HUMAN_STEP = ("Verify the plant-selection panel advanced and the game is progressing before continuing "
               "the probe.")


class Refusal(Exception):
    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


def skip_setup(base_url: str, method: str, timeout_sec: int) -> dict:
    """POST the skip and return the server's answer.

    The POST's own timeout is `timeout_sec + slack`, which is the original's arithmetic. It is now bounded
    because a non-positive `timeout_sec` is refused by the caller rather than becoming an infinite wait.
    """
    try:
        return lib.invoke_debug_post(base_url, "/setup/skip",
                                     {"method": method, "timeoutSec": timeout_sec},
                                     timeout=timeout_sec + POST_TIMEOUT_SLACK_SEC)
    except lib.Refusal as refusal:
        # The endpoint answers 4xx/409 with a JSON body carrying `error`, so the server's own words are
        # the most useful part of the message. They are kept, and the endpoint is named.
        if "answered 4" in refusal.detail or "answered 409" in refusal.detail:
            raise Refusal("SETUP-SKIP-FAILED",
                          f"POST /api/debug/setup/skip was refused by the server: {refusal.detail}") from refusal
        raise Refusal("SETUP-SKIP-FAILED", f"POST /api/debug/setup/skip failed: {refusal.detail}") from refusal


def check_health(base_url: str) -> dict:
    """Health first, then the injector. Order matters and is the original's: checking the injector first
    would name a missing injector when there is no server at all."""
    try:
        health = lib._get_json(f"{base_url}/health", HEALTH_TIMEOUT, "GET /health")
    except lib.Refusal as refusal:
        raise Refusal("SERVER-UNREACHABLE", f"GET /health on {base_url} failed: {refusal.detail}") from refusal
    if not isinstance(health, dict):
        raise Refusal("HEALTH-NOT-OK", f"/health answered with a {type(health).__name__}, not an object")
    if not health.get("ok"):
        raise Refusal("HEALTH-NOT-OK", "server health.ok=false")
    if not health.get("injectorConnected"):
        raise Refusal("INJECTOR-NOT-CONNECTED",
                      f"injector is not connected at {base_url} — start the game with the FusionRpg "
                      f"injector loaded (see the live-lawn-quick-start skill)")
    return health


def acknowledge(result: dict) -> dict:
    """The game's acknowledgement, as an object. A refusal here names the SHAPE, because a 200 whose
    body is not the object this script depends on is a different failure from a skip that did not ack."""
    if not isinstance(result, dict):
        raise Refusal("UNREADABLE-ACKNOWLEDGEMENT",
                      f"/setup/skip answered with a {type(result).__name__}, not an object")
    ack = result.get("acknowledgement")
    if ack is None:
        raise Refusal("SETUP-SKIP-NOT-ACKNOWLEDGED",
                      f"/setup/skip answered ok={result.get('ok')!r} with no acknowledgement field; "
                      f"keys={sorted(result)}")
    if not isinstance(ack, dict):
        # A JSON STRING here is the original's shape: it printed the acknowledgement through
        # ConvertTo-Json, so it arrives as text. It is PARSED rather than refused, because refusing a
        # shape the server actually sends would make this tool useless against the real endpoint.
        if isinstance(ack, str):
            try:
                ack = json.loads(ack)
            except json.JSONDecodeError as error:
                raise Refusal("UNREADABLE-ACKNOWLEDGEMENT",
                              f"the acknowledgement is a string but not JSON: {error}: {ack[:160]!r}") from error
        else:
            raise Refusal("UNREADABLE-ACKNOWLEDGEMENT",
                          f"the acknowledgement is a {type(ack).__name__}, not an object")
    if not isinstance(ack, dict):
        raise Refusal("UNREADABLE-ACKNOWLEDGEMENT", "the acknowledgement did not parse to an object")
    return ack


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        prog="prove-live-setup-skip",
        description="Prove the live setup skip is ACKNOWLEDGED by the game (replaces "
                    "prove-live-setup-skip.ps1).")
    parser.add_argument("--base-url", default="",
                        help=f"the server to prove against (default: resolved by the shared library from "
                             f"${lib.BASE_URL_ENV}, else {lib.DEFAULT_BASE_URL})")
    parser.add_argument("--method", default=DEFAULT_METHOD, choices=list(METHODS),
                        help=f"how to skip: {', '.join(METHODS)} (default {DEFAULT_METHOD!r})")
    parser.add_argument("--timeout-sec", type=int, default=DEFAULT_TIMEOUT_SEC,
                        help=f"the server's ack budget, also this tool's POST bound (default "
                             f"{DEFAULT_TIMEOUT_SEC})")
    parser.add_argument("--json", action="store_true", help="print the verdict as JSON")
    args = parser.parse_args(argv)

    if args.timeout_sec <= 0:
        # MEASURED, not assumed: the original's POST timeout is `$TimeoutSec + 5`, so the value is shifted
        # by five before it reaches .NET. 0 becomes a 5-second read; -5 becomes exactly 0, which .NET
        # defines as INFINITE; -6 is refused by PowerShell's own validation, so the framework catches
        # one step FURTHER from the danger than the one just inside it. A budget that cannot elapse was
        # also accepted and sent, and the server answered 409 about it.
        return _refuse("INVALID-TIMEOUT",
                       f"--timeout-sec {args.timeout_sec} must be positive. The original computed its POST "
                       f"timeout as this value + 5, so -5 became exactly 0 -- which .NET defines as "
                       f"INFINITE, removing the only bound on the server's answer -- while -6 was caught "
                       f"by the framework, so the dangerous value sat one step INSIDE the validation. A "
                       f"non-positive budget was also sent, and the server refused it with 409",
                       args.json)

    envelope: dict = {"tool": TOOL_ID, "methodSent": args.method}
    try:
        base_url, source = lib.resolve_base_url(args.base_url)
        envelope.update({"baseUrl": base_url, "baseUrlSource": source})
        print(f"Setup skip: method={args.method} against {base_url}...", file=sys.stderr)
        check_health(base_url)
        result = skip_setup(base_url, args.method, args.timeout_sec)
    except (Refusal, lib.Refusal) as refusal:
        # BOTH classes, and the omission is the bug a real run found. This tool declares its OWN
        # `Refusal`, `check_health` and `skip_setup` raise it, and the handler caught only `lib.Refusal`
        # -- so INJECTOR-NOT-CONNECTED, the single most common condition on this program, escaped `main`
        # as a traceback and exited 1. A refusal a caller cannot catch is not a refusal; it is a crash
        # wearing the reason as a costume. Every refusal below is raised by this file or by the library,
        # and the handler covers both or neither.
        if refusal.reason in lib.REFUSAL_REASONS or refusal.reason in REFUSAL_REASONS:
            return _refuse(refusal.reason, refusal.detail, args.json, envelope)
        return _refuse("SETUP-SKIP-FAILED", refusal.detail, args.json, envelope)

    if not result.get("ok"):
        # The endpoint is named here for the same reason it is named in `skip_setup`: the 409 path and the
        # 200-with-ok-false path are TWO failures of the SAME endpoint, and a refusal that named it in one
        # and not the other sent a reader looking in two places. Found by a case written after six
        # mutants survived, which is the only reason it was found at all.
        return _refuse("SETUP-SKIP-FAILED",
                       f"POST /api/debug/setup/skip failed: {result.get('error')!r} (the server said ok="
                       f"{result.get('ok')!r})", args.json, envelope)
    try:
        ack = acknowledge(result)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json, envelope)
    sent, acked = args.method, str(ack.get("method") or "")
    envelope.update({
        "methodAcknowledged": acked,
        "serverDefaultMethod": SERVER_DEFAULT_METHOD,
        "acknowledgement": ack,
        "verdict": "OK",
        "exitCode": 0,
    })
    if sent != acked:
        # Not a refusal: the skip DID ack. But the game acted on a different method than the one sent, and
        # a caller reading only `methodSent` would conclude the wrong thing, so it is called out by name.
        envelope["verdict"] = "ACKNOWLEDGED-DIFFERENT-METHOD"
        envelope["exitCode"] = 0
        print(f"  the game acknowledged method={acked!r}, not the {sent!r} that was sent", file=sys.stderr)

    if args.json:
        print(json.dumps(envelope, indent=2))
    else:
        print(f"LIVE setup skip succeeded: method={acked} "
              f"acknowledgement={json.dumps(ack, separators=(',', ':'))}")
        if sent != acked:
            print(f"  (sent method={sent}; the game's own acknowledgement says {acked})")
        print(_HUMAN_STEP)
    return envelope["exitCode"]


def _refuse(reason: str, detail: str, as_json: bool, envelope: dict | None = None) -> int:
    if as_json:
        payload = {"tool": TOOL_ID, "verdict": "REFUSED", "reason": reason, "detail": detail,
                   "exitCode": lib.EXIT_REFUSED}
        if envelope:
            payload.update(envelope)
        print(json.dumps(payload, indent=2))
    else:
        print(f"[{TOOL_ID}] REFUSED: {reason}", file=sys.stderr)
        print(f"  {detail}", file=sys.stderr)
    return lib.EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())

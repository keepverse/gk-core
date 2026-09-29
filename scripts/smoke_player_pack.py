#!/usr/bin/env python3
"""Smoke-test an unpacked player pack (default: `dist/FusionRpg`).

Runs the pack's own probe (`gk-fusion/tools/FusionRpg.PackSmoke`) over the pack layout, then — unless told to
skip — boots the PACKAGED server on its own port against a private data directory and asserts the three
properties a player pack must hold:

  * `/health` reports `contentSource == "imported"`. A pack that does not ship `Server/data/seed` beside
    the exe runs its whole content layer on the code fallback, and the server says so itself — so this
    reads the real boot rather than the presence of a directory.
  * `simEnabled` is NOT true. A player pack must leave SIM off.
  * `/api/test/snapshot` is the SPA fallback and not SIM JSON. With SIM off and an SPA fallback in
    place, an unknown path returns `index.html` with 200, so the shape of the body is the assertion.

Replaces `scripts/smoke-player-pack.ps1`.

WHY THE POWERSHELL FORM WAS RETIRED
------------------------------------
* **A MALFORMED PROBE OUTPUT READ AS A PASS.** `$probeJson | ConvertFrom-Json` was wrapped in
  `try { } catch { $probeObj = @{ ok = ($probeExit -eq 0) } }`. So if the probe emitted anything that
  was not JSON — a build banner on stdout, a truncated stream, a locale-formatted number — the smoke
  read `ok = (exit 0)` and carried on, and a `dotnet run` that exited 0 having proved nothing was
  recorded as a passing probe with an EMPTY step list. A malformed report is now a named refusal, and
  the probe's own steps are required to be present.

* **BOTH DELETES WERE SWALLOWED.** The `finally` did `$proc.Kill($true)` inside `try { } catch { try {
  Stop-Process ... -ErrorAction SilentlyContinue } catch { } }`, and
  `Remove-Item $dataDir -Recurse -Force -ErrorAction SilentlyContinue` inside `try { } catch { }`. A
  data directory that will not delete is a leak of the smoke's own making, and this repository's
  testing standard names a swallowed delete as the cause of a 65.5 GB leak. Both are now REPORTED, and
  a data directory that survives is a named failure of the run rather than a line nobody reads.

* **NEITHER `dotnet` CALL WAS BOUNDED.** `dotnet build` and `dotnet run --project` both had no timeout,
  and the server boot's own kill is what stopped a wedged child — after the fact.

* **THE PORT WAS A RANDOM NUMBER, NOT CONFIGURATION.** `Get-Random -Minimum 5200 -Maximum 5800` picks
  a port at random, so a caller cannot point the smoke at a known-free port and cannot reproduce a run.
  This repository's rule is that a port is configuration. `--port` now exists, and the random choice
  survives only as the DEFAULT, labelled as such.

* **`Set-Location $Root` CHANGED THE PROCESS'S WORKING DIRECTORY** as a side effect, for every caller
  in the same process.

WHAT IS DELIBERATELY KEPT
-------------------------
The server is booted, health-polled, asserted, killed, and its data directory removed — the same
sequence, in the same order, with the same three assertions and the same wording for each failure. This
port changes how the steps are BOUNDED and how failures are REPORTED, not which steps exist.
"""
from __future__ import annotations

import argparse
import json
import os
import random
import shutil
import signal
import subprocess
import sys
import time
import urllib.error
import urllib.request
import uuid
from dataclasses import dataclass, field
from pathlib import Path

TOOL_ID = "smoke-player-pack"

EXIT_PASSED = 0
EXIT_FAILED = 1
EXIT_REFUSED = 64

DEFAULT_PACK_DIR = ("dist", "FusionRpg")
RELATIVE_PROBE_PROJECT = ("tools", "FusionRpg.PackSmoke", "FusionRpg.PackSmoke.csproj")
RELATIVE_SUMMARY = ("artifacts", "player-pack-smoke.json")
SERVER_RELATIVE = ("Server", "FusionRpg.Server.exe")

# The owner never hardcodes a port; this range is the original's and is now a DEFAULT, not the value.
PORT_RANGE = (5200, 5800)
DEFAULT_BUILD_TIMEOUT = 900
DEFAULT_PROBE_TIMEOUT = 900
DEFAULT_BOOT_TIMEOUT = 45
HEALTH_POLL_INTERVAL = 0.4
HEALTH_POLL_TIMEOUT = 5

REFUSAL_REASONS = {
    "PACK-DIR-MISSING", "PROBE-PROJECT-MISSING", "PROBE-BUILD-FAILED", "PROBE-OUTPUT-NOT-JSON",
    "PROBE-OUTPUT-NOT-AN-OBJECT", "PROBE-STEPS-MISSING", "SERVER-EXE-MISSING", "DOTNET-NOT-ON-PATH",
    "INVALID-TIMEOUT", "PORT-IN-USE", "SUMMARY-NOT-WRITTEN", "PROBE-TIMED-OUT",
}

# Bound once, module-private: the process-wide `subprocess` and `shutil` must never be patched by a test
# of this tool. Proven the hard way twice in this program, where a suite that patched `subprocess.run`
# made 506 unrelated failures in `test_ps1_port_census.py`.
_RUN = subprocess.run
_POPEN = subprocess.Popen
_WHICH = shutil.which
_RMTREE = shutil.rmtree

# Bound once for the same reason: a test that patches the process-wide `urllib` reaches every other
# test in the project.
_URLOPEN = urllib.request.urlopen


class Refusal(Exception):
    def __init__(self, reason: str, detail: str) -> None:
        super().__init__(f"{reason}: {detail}")
        self.reason = reason
        self.detail = detail


@dataclass
class ServerStep:
    name: str = "server_boot"
    ok: bool = True
    message: str = "Skipped (--skip-server-boot)."

    def as_dict(self) -> dict:
        return {"name": self.name, "ok": self.ok, "message": self.message}


@dataclass
class Report:
    pack_dir: str = ""
    probe_exit: int = EXIT_REFUSED
    probe: dict = field(default_factory=dict)
    server: ServerStep = field(default_factory=ServerStep)
    probe_timed_out: bool = False
    refused: tuple[str, str] | None = None
    url: str = ""
    data_dir: str = ""
    data_dir_removed: bool | None = None
    process_stopped: bool | None = None
    summary_path: str = ""
    summary_written: bool = False

    @property
    def ok(self) -> bool:
        # The steps list must be NON-EMPTY. A probe that reported nothing has proved nothing, and
        # `ok` that does not say so lets a probe returning `{"ok": true}` with no steps read as a pass.
        steps = self.probe.get("steps")
        return (self.refused is None and not self.probe_timed_out
                and self.probe_exit == 0 and bool(self.probe.get("ok"))
                and isinstance(steps, list) and bool(steps)
                and self.server.ok and self.data_dir_removed is not False
                and self.process_stopped is not False)

    @property
    def reasons(self) -> list[str]:
        out: list[str] = []
        if self.probe_timed_out:
            out.append("the probe exceeded its timeout")
        if self.probe_exit != 0:
            out.append(f"the pack probe exited {self.probe_exit}")
        if not self.probe.get("ok"):
            for step in self.probe.get("steps", []):
                if isinstance(step, dict) and not step.get("ok"):
                    out.append(f"probe step {step.get('name')!r} failed: {step.get('message', '')}")
        if not self.server.ok:
            out.append(f"server_boot: {self.server.message}")
        if self.data_dir_removed is False:
            out.append(f"the smoke's own data directory could not be removed: {self.data_dir}")
        if self.process_stopped is False:
            out.append("the server process could not be stopped and may still be running")
        return out


def resolve_dotnet() -> str:
    """The interpreter, resolved ONCE, and named when absent."""
    found = _WHICH("dotnet")
    if not found:
        raise Refusal("DOTNET-NOT-ON-PATH", "dotnet is not on PATH")
    return found


def http_get(url: str, timeout: int) -> tuple[int, str, str]:
    """One bounded GET. Returns (status, body, content_type)."""
    request = urllib.request.Request(url)
    try:
        with _URLOPEN(request, timeout=timeout) as response:  # noqa: S310 - a 127.0.0.1 URL this tool built
            raw = response.read()
            content_type = response.headers.get("Content-Type", "") or ""
            return response.status, raw.decode("utf-8", errors="replace"), content_type
    except urllib.error.HTTPError as error:
        return error.code, "", ""
    except (urllib.error.URLError, OSError) as error:
        raise Refusal("PROBE-TIMED-OUT", f"{url} -> {error}") from error


def boot_and_check(server_exe: Path, port: int, boot_timeout: int,
                   report: Report) -> ServerStep:
    """Boot the packaged server, poll `/health`, assert the three properties, then stop it.

    The process is started with `Popen` rather than `run` because it is long-lived by design; every
    wait on it is BOUNDED, and the stop is a kill of the whole tree, which is what the original's
    `Kill($true)` meant.
    """
    data_dir = Path(os.environ.get("TEMP", ".")) / f"FusionRpgSmokeData-{uuid.uuid4().hex}"
    data_dir.mkdir(parents=True, exist_ok=True)
    url = f"http://127.0.0.1:{port}"
    report.url, report.data_dir = url, str(data_dir)

    environment = dict(os.environ)
    environment["FUSIONRPG_NO_BROWSER"] = "1"
    environment["FUSIONRPG_URLS"] = url
    environment["FUSIONRPG_DATA"] = str(data_dir)

    try:
        process = _POPEN([str(server_exe)], cwd=str(server_exe.parent), env=environment,
                         stdout=subprocess.PIPE, stderr=subprocess.PIPE)
    except OSError as error:
        raise Refusal("SERVER-EXE-MISSING", f"{server_exe} could not be started: {error}") from error

    try:
        deadline = time.monotonic() + boot_timeout
        healthy = False
        last = ""
        while time.monotonic() < deadline:
            try:
                status, body, _ = http_get(f"{url}/health", HEALTH_POLL_TIMEOUT)
                if status == 200 and '"ok": true' in body.replace('"ok":true', '"ok": true'):
                    healthy, last = True, body
                    break
                last = f"status {status}"
            except Refusal as error:
                # A refused GET during the poll is the server not up YET, not a failure of the smoke:
                # the loop is the bound, and the last reading is what the verdict names.
                last = str(error.detail)
            time.sleep(HEALTH_POLL_INTERVAL)

        if not healthy:
            return ServerStep(ok=False,
                              message=f"GET /health did not return ok within {boot_timeout}s "
                                      f"(last: {last}).")
        return assert_pack_properties(url, last)
    finally:
        report.process_stopped = _stop_tree(process)
        try:
            _RMTREE(data_dir)
            report.data_dir_removed = True
        except OSError:
            # REPORTED, not swallowed. `Remove-Item ... -ErrorAction SilentlyContinue` inside a
            # `try { } catch { }` is the shape this repository's testing standard names as the cause of
            # a 65.5 GB leak, and a data directory that survives is a real cost.
            report.data_dir_removed = False


def _stop_tree(process) -> bool:
    """Stop the server and everything it started. Returns whether it is gone.

    A process that exits on its own is a success, not a failure — the smoke did not have to kill it.
    """
    try:
        if process.poll() is not None:
            return True
    except OSError:
        return True
    try:
        if hasattr(process, "pid"):
            # `taskkill /T /F` is the Windows tree kill, which is what `Kill($true)` meant. Where it is
            # unavailable the single process is terminated, and the return value says which happened.
            killed = _RUN(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                          capture_output=True, text=True, timeout=30)
            if killed.returncode == 0:
                return True
        process.terminate()
        process.wait(timeout=15)
        return True
    except (OSError, subprocess.TimeoutExpired):
        try:
            process.kill()
            process.wait(timeout=15)
            return True
        except (OSError, subprocess.TimeoutExpired):
            return False


def assert_pack_properties(url: str, health_body: str) -> ServerStep:
    """The three properties, in the original's order, with its wording for each failure."""
    try:
        health = json.loads(health_body)
    except json.JSONDecodeError:
        return ServerStep(ok=False, message="/health did not return JSON.")
    if health.get("simEnabled") is True:
        return ServerStep(ok=False,
                          message="Health reports simEnabled=true (player pack must leave SIM off).")
    content_source = str(health.get("contentSource", ""))
    if content_source != "imported":
        return ServerStep(
            ok=False,
            message=f"Health reports contentSource='{content_source}' (expected 'imported'): the pack "
                    f"does not ship Server\\data\\seed beside the exe, so the whole content layer runs "
                    f"on the code fallback.")
    status, body, content_type = http_get(f"{url}/api/test/snapshot", HEALTH_POLL_TIMEOUT)
    looks_like_sim = '"eventCount"' in body or '"simEnabled"' in body
    if looks_like_sim and "text/html" not in content_type:
        return ServerStep(ok=False,
                          message="/api/test/snapshot returned SIM JSON (expected SPA fallback / SIM off).")
    return ServerStep(ok=True,
                      message="Health ok; contentSource='imported'; simEnabled off; /api/test/snapshot "
                              "is SPA fallback (not SIM).")


def run_probe(dotnet: str, project: Path, pack_dir: Path, root: Path,
              build_timeout: int, probe_timeout: int, report: Report) -> dict:
    """Build then run the pack probe. Its output must be JSON with a STEPS list, or the run refuses."""
    build = _RUN([dotnet, "build", str(project), "-c", "Release", "--verbosity", "quiet"],
                 capture_output=True, text=True, timeout=build_timeout, cwd=str(root))
    if build.returncode != 0:
        raise Refusal("PROBE-BUILD-FAILED",
                      f"PackSmoke build exited {build.returncode}:\n"
                      f"{(build.stdout + build.stderr)[-1500:]}")
    try:
        probe = _RUN([dotnet, "run", "--project", str(project), "-c", "Release", "--no-build", "--",
                      str(pack_dir)],
                     capture_output=True, text=True, timeout=probe_timeout, cwd=str(root))
    except subprocess.TimeoutExpired as expired:
        partial = expired.stdout or b""
        text = partial.decode("utf-8", errors="replace") if isinstance(partial, bytes) \
            else str(partial)
        report.probe_timed_out = True
        report.probe = {"ok": False, "steps": [],
                        "raw": f"{text}\n[no result within {probe_timeout}s]"}
        return report.probe
    report.probe_exit = probe.returncode
    raw = (probe.stdout or "") + (probe.stderr or "")
    try:
        payload = json.loads(probe.stdout)
    except json.JSONDecodeError as error:
        # THE SILENT GREEN, refused. The original caught this parse failure and substituted
        # `ok = (exit 0)`, so a probe that proved nothing and exited cleanly was recorded as a pass.
        raise Refusal("PROBE-OUTPUT-NOT-JSON",
                      f"PlayerPackProbe did not emit JSON ({error}). Its output was:\n{raw[-1500:]}") \
            from error
    if not isinstance(payload, dict):
        raise Refusal("PROBE-OUTPUT-NOT-AN-OBJECT",
                      f"PlayerPackProbe emitted a {type(payload).__name__}, not an object")
    if not isinstance(payload.get("steps"), list):
        raise Refusal("PROBE-STEPS-MISSING",
                      "PlayerPackProbe emitted no `steps` list, so there is nothing to have proved. A "
                      "report with no steps is not a passing report.")
    return payload


def execute(root: Path, pack_dir: Path, skip_boot: bool, port: int | None, boot_timeout: int,
            build_timeout: int, probe_timeout: int, summary_path: Path) -> Report:
    if not pack_dir.is_dir():
        raise Refusal("PACK-DIR-MISSING",
                      f"Pack directory not found: {pack_dir} (run scripts/publish_player.py first)")
    project = root.joinpath(*RELATIVE_PROBE_PROJECT)
    if not project.is_file():
        raise Refusal("PROBE-PROJECT-MISSING", f"PackSmoke project not found: {project}")

    dotnet = resolve_dotnet()
    report = Report(pack_dir=str(pack_dir))
    report.probe = run_probe(dotnet, project, pack_dir, root, build_timeout, probe_timeout, report)
    if not report.probe_timed_out:
        if not skip_boot:
            server_exe = pack_dir.joinpath(*SERVER_RELATIVE)
            if not server_exe.is_file():
                report.server = ServerStep(ok=False, message="Missing Server\\FusionRpg.Server.exe")
            else:
                chosen = port if port is not None else random.randint(*PORT_RANGE)
                if _port_in_use(chosen):
                    raise Refusal("PORT-IN-USE",
                                  f"{chosen} is already in use. Pass --port with a free port: a port "
                                  f"is configuration, not something to discover at random.")
                report.server = boot_and_check(server_exe, chosen, boot_timeout, report)

    summary = {"ok": report.ok, "packDir": report.pack_dir,
               "probeExitCode": report.probe_exit, "probe": report.probe,
               "server": report.server.as_dict(),
               "dataDirRemoved": report.data_dir_removed,
               "processStopped": report.process_stopped,
               "utc": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
    try:
        summary_path.parent.mkdir(parents=True, exist_ok=True)
        summary_path.write_text(json.dumps(summary, indent=2), encoding="utf-8")
        report.summary_path, report.summary_written = str(summary_path), True
    except OSError as error:
        raise Refusal("SUMMARY-NOT-WRITTEN", f"{summary_path} could not be written: {error}") from error
    return report


def _port_in_use(port: int) -> bool:
    """Whether something is already listening. A refused refusal, so a collision is named not hit."""
    with socket_closing() as sock:
        try:
            sock.bind(("127.0.0.1", port))
            return False
        except OSError:
            return True


def socket_closing():
    import socket
    return socket.socket(socket.AF_INET, socket.SOCK_STREAM)


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="smoke-player-pack",
        description="Smoke-test an unpacked player pack (replaces smoke-player-pack.ps1).")
    parser.add_argument("--root", default=None,
                        help="repository root (default: two levels above this file)")
    parser.add_argument("--pack-dir", default="",
                        help=f"the unpacked pack (default: {'/'.join(DEFAULT_PACK_DIR)})")
    parser.add_argument("--skip-server-boot", action="store_true",
                        help="run the pack probe only, and record the server step as skipped")
    parser.add_argument("--port", type=int, default=None,
                        help=f"port for the server boot (default: a random {PORT_RANGE[0]}-"
                             f"{PORT_RANGE[1]})")
    parser.add_argument("--summary-path", default="",
                        help=f"where to write the summary (default: {'/'.join(RELATIVE_SUMMARY)})")
    parser.add_argument("--boot-timeout", type=int, default=DEFAULT_BOOT_TIMEOUT,
                        help=f"seconds to wait for /health (default {DEFAULT_BOOT_TIMEOUT})")
    parser.add_argument("--build-timeout", type=int, default=DEFAULT_BUILD_TIMEOUT,
                        help=f"seconds for the probe build (default {DEFAULT_BUILD_TIMEOUT})")
    parser.add_argument("--probe-timeout", type=int, default=DEFAULT_PROBE_TIMEOUT,
                        help=f"seconds for the probe run (default {DEFAULT_PROBE_TIMEOUT})")
    parser.add_argument("--json", action="store_true")
    return parser


def render(report: Report, as_json: bool) -> None:
    if as_json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "OK" if report.ok else "FAILED",
                          "exitCode": EXIT_PASSED if report.ok else EXIT_FAILED,
                          "packDir": report.pack_dir, "probeExit": report.probe_exit,
                          "probe": report.probe, "server": report.server.as_dict(),
                          "url": report.url, "dataDirRemoved": report.data_dir_removed,
                          "processStopped": report.process_stopped,
                          "summaryPath": report.summary_path, "summaryWritten": report.summary_written,
                          "reasons": report.reasons}, indent=2))
        return
    print(f"==> PlayerPackProbe on {report.pack_dir}")
    for step in report.probe.get("steps", []):
        if isinstance(step, dict):
            print(f"  [{'ok' if step.get('ok') else 'FAIL'}] {step.get('name')}: "
                  f"{str(step.get('message', ''))[:100]}")
    print(f"==> server_boot: {'ok' if report.server.ok else 'FAIL'} -- {report.server.message}")
    if report.summary_written:
        print(f"==> Wrote {report.summary_path}")
    if report.ok:
        print("SMOKE PASSED")
    else:
        print("SMOKE FAILED")
        for reason in report.reasons:
            print(f"  {reason}")


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    for name, value in (("--boot-timeout", args.boot_timeout),
                        ("--build-timeout", args.build_timeout),
                        ("--probe-timeout", args.probe_timeout)):
        if value <= 0:
            return _refuse("INVALID-TIMEOUT", f"{name} must be positive", args.json)
    root = Path(args.root).expanduser().resolve() if args.root else \
        Path(__file__).resolve().parent.parent
    pack_dir = Path(args.pack_dir).expanduser().resolve() if args.pack_dir else \
        root.joinpath(*DEFAULT_PACK_DIR)
    summary_path = Path(args.summary_path).expanduser() if args.summary_path else \
        root.joinpath(*RELATIVE_SUMMARY)
    try:
        report = execute(root, pack_dir, args.skip_server_boot, args.port, args.boot_timeout,
                         args.build_timeout, args.probe_timeout, summary_path)
    except Refusal as refusal:
        return _refuse(refusal.reason, refusal.detail, args.json)
    render(report, args.json)
    return EXIT_PASSED if report.ok else EXIT_FAILED


def _refuse(reason: str, detail: str, as_json: bool) -> int:
    if as_json:
        print(json.dumps({"tool": TOOL_ID, "verdict": "REFUSED", "reason": reason, "detail": detail,
                          "exitCode": EXIT_REFUSED}, indent=2))
    else:
        print(f"[{TOOL_ID}] REFUSED: {reason}", file=sys.stderr)
        print(f"  {detail}", file=sys.stderr)
    return EXIT_REFUSED


if __name__ == "__main__":
    sys.exit(main())

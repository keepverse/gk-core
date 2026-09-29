"""Contract tests for `gk-core/scripts/prepare_injector_refs.py`.

THE HEADLINE IS ABOUT THE CHILD, NOT THIS TOOL. The original ran its refs fetcher with
`& (Join-Path $PSScriptRoot "fetch-bepinex-refs.ps1")` and NEVER CHECKED `$LASTEXITCODE`, under
`$ErrorActionPreference = "Stop"`. A child process exiting nonzero is not a PowerShell exception, so a
FAILED fetch fell straight through to the interop logic and could still report success — on whatever
stale tree happened to be on disk. So the property under test is that a non-zero fetcher becomes a named
refusal, and it is exercised by making a REAL fetcher fail: `--fetch-arg` forwards `--api-base` to a
loopback server that answers 500. The child is a real Python process doing a real HTTP request; nothing
about the composition is stubbed.

THE `interopSource` VOCABULARY IS CLOSED, NOT PROSE. An assembly copied from the wrong place is the one
failure this tool cannot otherwise detect, so the verdict reports which of four sources was used, and the
suite pins the four names and refuses an undeclared one.

THE BRANCHES ARE EXERCISED, NOT ASSUMED. Branches (a), (b) and (c) each get a case, with the zip branch
over a real loopback HTTP server serving a real zip. The equal-tree differential against the original's
own logic — over a real game install's interop — is a separate harness, because a suite that stubs the
thing under test is testing its own arithmetic.

THE ENV VAR IS REPORTED, NOT EXPORTED. The original's closing `$env:FUSIONRPG_GAME_DIR = $Refs` assigned
the variable in a process that exited immediately, so no caller ever inherited it. Pinned as
`gameDirExported: false` with a note, because a caller that assumes otherwise is the bug this removes.
"""
from __future__ import annotations

import ast
import http.server
import importlib.util
import io
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import tempfile
import threading
import time
import unittest
import zipfile
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path
from unittest import mock

REPO = Path(__file__).resolve().parents[2]
SCRIPT = Path(os.environ.get("PREPARE_INJECTOR_REFS_SCRIPT",
                             REPO / "scripts" / "prepare_injector_refs.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_prepare_injector_refs.py"
MARKER = "Assembly-CSharp.dll"
GAME_ENV = "FUSIONRPG_GAME_DIR"
ZIP_ENV = "FUSIONRPG_INTEROP_ZIP_URL"
RUN_TIMEOUT = 300
BRANCHES = {"game-dir-env", "interop-zip-url", "already-present-under-refs",
            "parent-of-repo-root", "none"}

_spec = importlib.util.spec_from_file_location("prepare_injector_refs", SCRIPT)
prep = importlib.util.module_from_spec(_spec)
sys.modules["prepare_injector_refs"] = prep
_spec.loader.exec_module(prep)


# ───────────────────────────────────────────────── the refs tree, and a server that can fail the fetch


def refs_tree(root: Path, with_interop: bool = False, interop_files: int = 3) -> Path:
    """A minimal reference tree: `BepInEx/core` is the post-condition the fetcher guarantees."""
    core = root / "artifacts" / "bepinex-refs" / "BepInEx" / "core"
    core.mkdir(parents=True, exist_ok=True)
    (core / "BepInEx.Core.dll").write_bytes(b"MZ core")
    if with_interop:
        interop = root / "artifacts" / "bepinex-refs" / "BepInEx" / "interop"
        interop.mkdir(parents=True, exist_ok=True)
        (interop / MARKER).write_bytes(b"MZ interop")
        for i in range(interop_files - 1):
            (interop / f"Other{i}.dll").write_bytes(b"MZ")
    return root / "artifacts" / "bepinex-refs"


class _Refs:
    """Serves the sibling fetcher over loopback: a real release document, a real flat zip, or a 500."""
    status = 200
    zip_payload = b""
    requests: list[str] = []


def _refs_zip() -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        for name in ("BepInEx/core/BepInEx.Core.dll", "BepInEx/core/0Harmony.dll",
                     "changelog.txt", "doorstop_config.ini"):
            archive.writestr(name, f"content for {name}\n".encode())
    return buffer.getvalue()


class _RefsHandler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *a):
        return

    def do_GET(self) -> None:
        _Refs.requests.append(self.path)
        if _Refs.status != 200:
            self.send_error(_Refs.status, "fixture refuses")
            return
        if "/releases/tags/" in self.path:
            host = self.headers.get("Host", "127.0.0.1")
            body = json.dumps({"tag_name": "v6.0.0-pre.2", "assets": [
                {"name": "BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip",
                 "size": len(_Refs.zip_payload),
                 "browser_download_url": f"http://{host}/asset/x.zip"}]}).encode()
            self._send("application/json", body)
            return
        self._send("application/zip", _Refs.zip_payload)

    def _send(self, content_type, body) -> None:
        self.send_response(200)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def serve_refs(status: int = 200):
    _Refs.status = status
    _Refs.zip_payload = _refs_zip()
    _Refs.requests = []
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), _RefsHandler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{port}"


def serve_bytes(payload: bytes, content_type: str = "application/zip"):
    class Handler(http.server.BaseHTTPRequestHandler):
        def log_message(self, *a):
            return

        def do_GET(self) -> None:
            self.send_response(200)
            self.send_header("Content-Type", content_type)
            self.send_header("Content-Length", str(len(payload)))
            self.end_headers()
            self.wfile.write(payload)

    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{port}"


def make_zip(entries, wrap_in: str | None = None) -> bytes:
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, payload in entries:
            archive.writestr(f"{wrap_in}/{name}" if wrap_in else name, payload)
    return buffer.getvalue()


def clean_env(**extra) -> dict:
    env = {k: v for k, v in os.environ.items() if k not in (GAME_ENV, ZIP_ENV)}
    env.update(extra)
    return env


def run_tool(root: Path, *extra: str, env: dict | None = None):
    proc = subprocess.run([sys.executable, str(SCRIPT), "--json", "--root", str(root), *extra],
                          capture_output=True, text=True, timeout=RUN_TIMEOUT, cwd=str(REPO),
                          env=env if env is not None else clean_env())
    try:
        return proc.returncode, json.loads(proc.stdout)
    except json.JSONDecodeError:
        raise AssertionError(f"no JSON verdict.\nstdout: {proc.stdout[:400]}\nstderr: {proc.stderr[:400]}")


def tmp_root(test, with_interop: bool = False, interop_files: int = 3) -> Path:
    root = Path(tempfile.mkdtemp(prefix="pir-contract-"))
    test.addCleanup(lambda: shutil.rmtree(root, ignore_errors=True))
    refs_tree(root, with_interop=with_interop, interop_files=interop_files)
    return root


def fetch_arg(base: str) -> list[str]:
    """Forward the loopback seam to the real fetcher, so the child is a real process doing real HTTP.

    ONE token, not a pair. `--fetch-arg --api-base --fetch-arg URL` dies with argparse SystemExit 2
    before the tool's body runs, because argparse will not accept a value beginning with '-'. The
    arguments worth forwarding are exactly the ones that begin with '-', which is why the flag takes a
    single shell-quoted string and splits it itself."""
    return ["--fetch-arg", f"--api-base {base}"]


class TheChildsExitCodeIsChecked(unittest.TestCase):
    """THE HEADLINE. A failed fetch must NOT fall through to the interop logic."""

    def test_a_REFUSING_fetcher_is_a_named_refusal_not_a_FALL_THROUGH(self) -> None:
        root = tmp_root(self)
        server, base = serve_refs(status=500)
        try:
            code, payload = run_tool(root, "--no-parent-lookup", *fetch_arg(base))
            self.assertEqual(code, prep.EXIT_REFUSED,
                             "a failed fetch fell through and reported a result")
            self.assertEqual(payload["reason"], "FETCHER-FAILED")
            self.assertIs(payload["prepared"], False)
        finally:
            server.shutdown()

    def test_the_refusal_NAMES_the_fetcher_and_its_reason(self) -> None:
        root = tmp_root(self)
        server, base = serve_refs(status=500)
        try:
            _, payload = run_tool(root, "--no-parent-lookup", *fetch_arg(base))
            self.assertIn(prep.FETCHER, payload["detail"])
            self.assertIn("exit code", payload["detail"],
                          "the original's defect was not checking this; the refusal must say so")
        finally:
            server.shutdown()

    def test_a_SUCCEEDING_fetcher_is_not_refused_and_reports_its_own_verdict(self) -> None:
        root = tmp_root(self)
        # No interop anywhere, so the only way past the fetch is the interop refusal -- which proves
        # the fetch itself succeeded and the run continued.
        server, base = serve_refs()
        try:
            code, payload = run_tool(root, "--no-parent-lookup", *fetch_arg(base))
            self.assertEqual(payload["reason"], "NO-INTEROP",
                             "the run did not get past a successful fetch")
            self.assertIs(payload["refsFetch"]["published"], True)
        finally:
            server.shutdown()

    def test_a_MISSING_fetcher_is_a_named_refusal_rather_than_a_HALF_PREPARED_tree(self) -> None:
        """Run the tool from a directory with no fetcher beside it, as a real subprocess: the fetch is
        composed, not reimplemented, so a missing fetcher must refuse rather than proceed."""
        root = tmp_root(self)
        lonely = Path(tempfile.mkdtemp(prefix="pir-lonely-"))
        self.addCleanup(lambda: shutil.rmtree(lonely, ignore_errors=True))
        (lonely / SCRIPT.name).write_bytes(SCRIPT.read_bytes())
        proc = subprocess.run([sys.executable, str(lonely / SCRIPT.name), "--json",
                               "--root", str(root), "--no-parent-lookup"],
                              capture_output=True, text=True, timeout=RUN_TIMEOUT, cwd=str(REPO),
                              env=clean_env())
        payload = json.loads(proc.stdout)
        self.assertEqual(payload["reason"], "FETCHER-MISSING")
        self.assertEqual(proc.returncode, prep.EXIT_REFUSED)

    def test_SKIP_refs_fetch_reuses_the_tree_without_a_child(self) -> None:
        root = tmp_root(self, with_interop=True)
        code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup")
        self.assertEqual(code, 0, payload)
        self.assertIs(payload["refsFetch"]["skipped"], True)
        self.assertEqual(payload["interopSource"], "already-present-under-refs")

    def test_SKIPPING_the_fetch_on_an_EMPTY_tree_refuses_rather_than_claiming_success(self) -> None:
        root = tmp_root(self)
        shutil.rmtree(root / "artifacts" / "bepinex-refs" / "BepInEx" / "core")
        code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup")
        self.assertEqual(code, prep.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "FETCHER-FAILED")
        self.assertIn("no BepInEx reference tree", payload["detail"])


class TheBranches(unittest.TestCase):
    """Each source is exercised, and the one used is reported from a CLOSED vocabulary."""

    def test_a_GAME_DIR_with_interop_is_used_and_REPORTED(self) -> None:
        root = tmp_root(self)
        game = Path(tempfile.mkdtemp(prefix="pir-game-"))
        self.addCleanup(lambda: shutil.rmtree(game, ignore_errors=True))
        interop = game / "BepInEx" / "interop"
        interop.mkdir(parents=True)
        (interop / MARKER).write_bytes(b"MZ from the game")
        (interop / "UnityEngine.dll").write_bytes(b"MZ unity")
        code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup",
                                 env=clean_env(**{GAME_ENV: str(game)}))
        self.assertEqual(code, 0, payload)
        self.assertEqual(payload["interopSource"], "game-dir-env")
        published = root / "artifacts" / "bepinex-refs" / "BepInEx" / "interop"
        self.assertEqual((published / MARKER).read_bytes(), b"MZ from the game")

    def test_a_ZIP_URL_is_used_and_the_ROOT_INSIDE_it_is_reported_with_POSIX_separators(self) -> None:
        root = tmp_root(self)
        body = make_zip([(f"BepInEx/interop/{MARKER}", b"MZ zipped"),
                         ("BepInEx/interop/UnityEngine.dll", b"MZ unity")])
        server, base = serve_bytes(body)
        try:
            code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup",
                                     env=clean_env(**{ZIP_ENV: f"{base}/interop.zip"}))
            self.assertEqual(code, 0, payload)
            self.assertEqual(payload["interopSource"], "interop-zip-url")
            self.assertEqual(payload["interopFrom"], "BepInEx/interop")
        finally:
            server.shutdown()

    def test_a_zip_WITH_the_interop_at_its_ROOT_is_found(self) -> None:
        root = tmp_root(self)
        body = make_zip([(MARKER, b"MZ at the root"), ("other.txt", b"x")])
        server, base = serve_bytes(body)
        try:
            code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup",
                                     env=clean_env(**{ZIP_ENV: f"{base}/interop.zip"}))
            self.assertEqual(code, 0, payload)
            self.assertEqual(payload["interopFrom"], ".")
        finally:
            server.shutdown()

    def test_an_AMBIGUOUS_zip_is_a_REFUSAL_listing_where_it_found_the_marker(self) -> None:
        root = tmp_root(self)
        # NESTED hit directories on purpose. With single-segment names (`a/`, `b/`) a native rendering
        # and a POSIX rendering are the SAME TEXT, so a case about separator portability cannot tell
        # them apart -- the first version of this fixture was exactly that blind, and a mutant that
        # reverted the refusal to native separators survived through it.
        body = make_zip([(f"pkg/first/{MARKER}", b"MZ a"), (f"other/second/{MARKER}", b"MZ b")])
        server, base = serve_bytes(body)
        try:
            code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup",
                                     env=clean_env(**{ZIP_ENV: f"{base}/interop.zip"}))
            self.assertEqual(code, prep.EXIT_REFUSED)
            self.assertEqual(payload["reason"], "INTEROP-AMBIGUOUS")
            # The candidates EXACTLY, not as single letters. `assertIn("a", detail)` and
            # `assertIn("b", detail)` passed against a refusal that listed nothing, because both letters
            # occur in ordinary words in the message ("places", "Repack") -- a case that cannot tell
            # "listed the candidates" from "happened to contain these characters" is not a case.
            # Sorted, because `hits` is a sorted set of directories and the rendering preserves that
            # order -- so the expected text is the ALPHABETICAL one, not the order the zip listed.
            self.assertIn("other/second, pkg/first", payload["detail"])
            self.assertIn("2 places", payload["detail"])
        finally:
            server.shutdown()

    def test_a_refusal_renders_its_paths_the_SAME_way_on_EVERY_host(self) -> None:
        """The port had this inconsistency and no case caught it: the envelope's `interopFrom` was
        switched to POSIX separators while the refusal text still used `str(PurePath)`, which yields
        backslashes on Windows. A refusal a consumer has to parse differently per platform is a
        portability bug wearing a message."""
        root = tmp_root(self)
        body = make_zip([(f"pkg/first/{MARKER}", b"MZ a"), (f"other/second/{MARKER}", b"MZ b")])
        server, base = serve_bytes(body)
        try:
            _, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup",
                                  env=clean_env(**{ZIP_ENV: f"{base}/interop.zip"}))
            # Sorted, because `hits` is a sorted set of directories and the rendering preserves that
            # order -- so the expected text is the ALPHABETICAL one, not the order the zip listed.
            self.assertIn("other/second, pkg/first", payload["detail"])
            self.assertNotIn("\\", payload["detail"],
                             "a refusal rendered platform-dependent separators")
        finally:
            server.shutdown()

    def test_the_TERMINAL_refusal_still_names_HOW_to_get_out(self) -> None:
        """`publish_player.py`'s own suite pins exactly this for its NO-REFERENCE-TREE refusal, so the
        pattern already exists in this repository and this case was simply missing. A terminal refusal
        that stops naming the ways out is a support ticket waiting to happen."""
        root = tmp_root(self)
        code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup")
        self.assertEqual(code, prep.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "NO-INTEROP")
        for hint in (GAME_ENV, ZIP_ENV):
            self.assertIn(hint, payload["detail"],
                          f"the terminal refusal no longer names {hint}, so it no longer says how to proceed")

    def test_a_zip_with_NO_marker_refuses_and_reports_what_it_had(self) -> None:
        root = tmp_root(self)
        body = make_zip([("readme.txt", b"hi"), ("LICENSE", b"MIT")])
        server, base = serve_bytes(body)
        try:
            code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup",
                                     env=clean_env(**{ZIP_ENV: f"{base}/interop.zip"}))
            self.assertEqual(payload["reason"], "INTEROP-NOT-FOUND")
            self.assertIn("readme.txt", payload["detail"])
        finally:
            server.shutdown()

    def test_a_NOT_A_ZIP_download_is_REFUSED_as_a_BAD_download(self) -> None:
        root = tmp_root(self)
        server, base = serve_bytes(b"<html>login page</html>", content_type="text/html")
        try:
            code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup",
                                     env=clean_env(**{ZIP_ENV: f"{base}/interop.zip"}))
            self.assertEqual(payload["reason"], "INTEROP-NOT-A-ZIP")
            self.assertIn("PK", payload["detail"])
        finally:
            server.shutdown()

    def test_the_PARENT_branch_fires_and_is_reported(self) -> None:
        """Kept from the original even though it is unlikely to find anything: it is the last branch,
        and the final check still refuses. `--no-parent-lookup` is what lets a case avoid it."""
        root = tmp_root(self)
        parent_interop = root.parent / "BepInEx" / "interop"
        parent_interop.mkdir(parents=True, exist_ok=True)
        (parent_interop / MARKER).write_bytes(b"MZ from the parent")
        self.addCleanup(lambda: shutil.rmtree(root.parent / "BepInEx", ignore_errors=True))
        code, payload = run_tool(root, "--skip-refs-fetch")
        self.assertEqual(code, 0, payload)
        self.assertEqual(payload["interopSource"], "parent-of-repo-root")

    def test_the_PARENT_branch_is_SKIPPED_when_asked(self) -> None:
        root = tmp_root(self)
        code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup")
        self.assertEqual(payload["reason"], "NO-INTEROP")
        self.assertIn("skipped by --no-parent-lookup", payload["detail"])


class TheSilentFallThroughs(unittest.TestCase):
    """The two places the original fell through quietly."""

    def test_a_SET_but_NONEXISTENT_game_dir_REFUSES_instead_of_using_another_source(self) -> None:
        root = tmp_root(self, with_interop=True)
        missing = str(root / "not-here")
        code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup",
                                 env=clean_env(**{GAME_ENV: missing}))
        self.assertEqual(code, prep.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "GAME-DIR-MISSING")
        self.assertIn("--ignore-missing-game-dir", payload["detail"],
                      "the refusal must offer the escape hatch, not just refuse")

    def test_IGNORE_MISSING_game_dir_restores_the_ORIGINALS_FALL_THROUGH(self) -> None:
        root = tmp_root(self, with_interop=True)
        code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup",
                                 "--ignore-missing-game-dir",
                                 env=clean_env(**{GAME_ENV: str(root / "not-here")}))
        self.assertEqual(code, 0, payload)
        self.assertEqual(payload["interopSource"], "already-present-under-refs")

    def test_a_GAME_DIR_with_CORE_but_NO_interop_is_a_DISTINCT_refusal(self) -> None:
        root = tmp_root(self)
        game = Path(tempfile.mkdtemp(prefix="pir-coreonly-"))
        self.addCleanup(lambda: shutil.rmtree(game, ignore_errors=True))
        (game / "BepInEx" / "core").mkdir(parents=True)
        (game / "BepInEx" / "core" / "BepInEx.Core.dll").write_bytes(b"MZ")
        code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup",
                                 env=clean_env(**{GAME_ENV: str(game)}))
        self.assertEqual(payload["reason"], "GAME-DIR-NO-INTEROP")
        self.assertIn("per game", payload["detail"],
                      "the reason interop is per-game is the reason this cannot fall back")

    def test_an_EMPTY_interop_source_is_REFUSED_rather_than_copying_NOTHING(self) -> None:
        root = tmp_root(self)
        game = Path(tempfile.mkdtemp(prefix="pir-empty-"))
        self.addCleanup(lambda: shutil.rmtree(game, ignore_errors=True))
        interop = game / "BepInEx" / "interop"
        interop.mkdir(parents=True)
        (interop / MARKER).write_bytes(b"MZ")          # the marker is a FILE, so the dir is not empty
        empty = game / "BepInEx" / "interop-empty"
        empty.mkdir()
        with self.assertRaises(prep.Refusal) as caught:
            prep.copy_interop(empty, root / "published")
        self.assertEqual(caught.exception.reason, "INTEROP-EMPTY-SOURCE")


class TheEnvironmentIsReportedNotExported(unittest.TestCase):
    """The original's closing assignment set the variable in a process that exited immediately."""

    def test_the_game_dir_is_reported_and_NOT_claimed_to_be_EXPORTED(self) -> None:
        root = tmp_root(self, with_interop=True)
        code, payload = run_tool(root, "--skip-refs-fetch", "--no-parent-lookup")
        self.assertEqual(code, 0, payload)
        self.assertIs(payload["gameDirExported"], False)
        self.assertIn("not exported", payload["gameDirNote"].lower())
        self.assertEqual(Path(payload["gameDir"]),
                         (root / "artifacts" / "bepinex-refs").resolve())

    def test_the_ENVIRONMENT_of_the_caller_is_UNCHANGED(self) -> None:
        root = tmp_root(self, with_interop=True)
        before = dict(os.environ)
        run_tool(root, "--skip-refs-fetch", "--no-parent-lookup")
        self.assertEqual(dict(os.environ), before,
                         "the tool changed the caller's environment, which the original only appeared to")

    def test_the_report_names_the_variable_an_Injector_build_needs(self) -> None:
        root = tmp_root(self, with_interop=True)
        proc = subprocess.run([sys.executable, str(SCRIPT), "--root", str(root), "--skip-refs-fetch",
                               "--no-parent-lookup"], capture_output=True, text=True,
                              timeout=RUN_TIMEOUT, cwd=str(REPO), env=clean_env())
        self.assertEqual(proc.returncode, 0, proc.stderr[-400:])
        self.assertIn(f"{GAME_ENV}=", proc.stdout, "the value must be usable by a caller as a line")


class TheBounds(unittest.TestCase):
    def test_an_UNPARSEABLE_fetch_arg_is_a_named_refusal(self) -> None:
        root = tmp_root(self)
        code, payload = run_tool(root, "--no-parent-lookup", "--fetch-arg", "'unbalanced")
        self.assertEqual(code, prep.EXIT_REFUSED)
        self.assertEqual(payload["reason"], "INVALID-FETCH-ARG")

    def test_a_NON_POSITIVE_TIMEOUT_REFUSES_before_any_work(self) -> None:
        for flag in ("--fetch-timeout", "--download-timeout"):
            with self.subTest(flag=flag):
                out = io.StringIO()
                with redirect_stdout(out):
                    code = prep.main([flag, "0", "--json"])
                self.assertEqual(code, prep.EXIT_REFUSED)
                self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT")

    def test_the_interop_DOWNLOAD_carries_a_TIMEOUT(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        calls = re.findall(r"_URLOPEN\((.*?)\)", source, re.DOTALL)
        self.assertGreaterEqual(len(calls), 1, "no network call found at all")
        for call in calls:
            self.assertIn("timeout=", call, f"an unbounded download: _URLOPEN({call[:90]})")

    def test_the_FETCHER_child_carries_a_TIMEOUT_and_CAPTURE(self) -> None:
        seen: list[dict] = []

        def run(cmd, **kwargs):
            seen.append({"cmd": list(cmd), "kwargs": kwargs})
            return subprocess.CompletedProcess(cmd, 1, json.dumps({"reason": "API-REFUSED"}), "")

        with mock.patch.object(prep, "_RUN", run):
            with self.assertRaises(prep.Refusal) as caught:
                prep.run_fetcher(Path("."), 60, [])
        self.assertEqual(caught.exception.reason, "FETCHER-FAILED")
        call = seen[0]
        self.assertIn(prep.FETCHER, " ".join(call["cmd"]))
        self.assertEqual(call["kwargs"].get("timeout"), 60)
        self.assertIsNotNone(call["kwargs"].get("capture_output"))

    def test_a_FETCHER_TIMEOUT_is_a_named_refusal_that_says_it_is_not_retried(self) -> None:
        with mock.patch.object(prep, "_RUN", side_effect=subprocess.TimeoutExpired("dotnet", 60)):
            with self.assertRaises(prep.Refusal) as caught:
                prep.run_fetcher(Path("."), 60, [])
        self.assertEqual(caught.exception.reason, "FETCHER-FAILED")
        self.assertIn("never retried", caught.exception.detail)

    def test_fetch_args_are_forwarded_to_the_CHILD_verbatim(self) -> None:
        seen: list[list[str]] = []

        def run(cmd, **kwargs):
            seen.append(list(cmd))
            return subprocess.CompletedProcess(cmd, 0, json.dumps({"published": True}), "")

        with mock.patch.object(prep, "_RUN", run):
            prep.run_fetcher(Path("."), 60, ["--api-base", "http://127.0.0.1:9"])
        argv = seen[0]
        self.assertEqual(argv[argv.index("--api-base") + 1], "http://127.0.0.1:9")


class Surface(unittest.TestCase):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - prep.REFUSAL_REASONS, set(),
                         f"undeclared refusal reason(s) {sorted(found - prep.REFUSAL_REASONS)}")

    def test_the_interop_SOURCE_values_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        # The DECLARATIONS are the (name, value) pairs; `used` is a set of NAMES. The first version
        # compared names against values, so every source came out "undeclared" and the case was
        # measuring nothing.
        pairs = dict(re.findall(r'^(SOURCE_[A-Z_]+) = "([a-z-]+)"', source, re.MULTILINE))
        self.assertTrue(pairs, "no interop source constants found")
        self.assertEqual(set(pairs.values()), BRANCHES,
                         f"the closed vocabulary drifted: {sorted(set(pairs.values()) ^ BRANCHES)}")
        used = set(re.findall(r'envelope\["interopSource"\] = (SOURCE_[A-Z_]+)', source))
        self.assertTrue(used <= set(pairs), f"undeclared source used: {sorted(used - set(pairs))}")
        self.assertTrue(used, "no branch assigns interopSource at all")

    def test_the_EXIT_code_vocabulary_is_declared(self) -> None:
        self.assertEqual(prep.EXIT_REFUSED, 64)

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--root", "--refs-dir", "--skip-refs-fetch", "--fetch-arg",
                     "--ignore-missing-game-dir", "--no-parent-lookup", "--keep-interop-extract",
                     "--fetch-timeout", "--download-timeout", "--json"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        for flag in ("-Root", "-Refs", "-TimeoutSec"):
            with self.subTest(flag=flag):
                proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                      text=True, timeout=RUN_TIMEOUT)
                self.assertNotEqual(proc.returncode, 0, flag)
                self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_PowerShell_WAS_retired(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("prepare-injector-refs.ps1", head)
        lowered = head.lower()
        for reason in ("exit code", "unbounded", "arbitrary", "silently", "inert"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_PARENT_LOOKOUT_is_disclosed_rather_than_silently_kept(self) -> None:
        """A branch that reaches outside the repository looks like a bug, so the docstring has to say
        it was kept DELIBERATELY -- an unrequested behaviour change dressed as a fix is worse."""
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("ABOVE the repository root", head)
        self.assertIn("THAT LOOKS LIKE A BUG", head)

    def test_it_composes_the_FETCHER_rather_than_reimplementing_the_fetch(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("fetch_bepinex_refs.py", source)
        # One implementation of the fetch: this tool must not build the core tree itself.
        self.assertNotIn("BepInEx-Unity.IL2CPP", source,
                         "this tool appears to implement its own fetch, which would be a second copy")

    def test_NO_case_patches_a_process_WIDE_module(self) -> None:
        tree = ast.parse(SUITE.read_text(encoding="utf-8"))
        globals_seen = {"subprocess", "shutil", "tempfile", "os", "sys", "json", "urllib", "http",
                        "zipfile", "socket", "threading", "time", "importlib", "ast", "re", "io"}
        offences = []
        for node in ast.walk(tree):
            if not (isinstance(node, ast.Call) and isinstance(node.func, ast.Attribute)
                    and node.func.attr == "object"):
                continue
            if not (isinstance(node.func.value, ast.Attribute) and node.func.value.attr == "patch"):
                continue
            target = node.args[0] if node.args else None
            if isinstance(target, ast.Name) and target.id == "prep":
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in globals_seen:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))

    def test_every_case_writes_UNDER_a_temporary_directory_and_nothing_else(self) -> None:
        """MEASURED from the roots the cases actually built, not scanned from this file. The disk IS
        the thing under test here, so the roots must be temporary -- and the parent-lookup case is the
        one that reaches outside its own root, so it is checked to clean up after itself."""
        roots = [p for p in Path(tempfile.gettempdir()).glob("pir-contract-*")]
        temp_root = Path(tempfile.gettempdir()).resolve()
        outside = [str(p) for p in roots if temp_root not in p.resolve().parents]
        self.assertEqual(outside, [], f"a case wrote outside a temporary directory: {outside}")
        inside_repo = [str(p) for p in roots if REPO in p.resolve().parents]
        self.assertEqual(inside_repo, [], f"a case wrote inside the repository: {inside_repo}")


if __name__ == "__main__":
    unittest.main()

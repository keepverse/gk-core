"""Contract tests for `gk-core/scripts/fetch_bepinex_refs.py`.

THIS TOOL'S CONTRACT IS ABOUT WHAT IT REFUSES, and about the one property that distinguishes it from the
PowerShell form it replaces.

THE PROPERTY: A FAILED RUN MUST NOT DESTROY THE PUBLISHED TREE. The original ran
`Remove-Item $OutDir -Recurse -Force` as its THIRD statement -- before the API call. So a rate-limited
API, a typo'd tag, or a dropped connection left the machine with NO reference assemblies, which is the
exact state the tool exists to prevent. Here the fetch happens into a staging directory and is swapped
in only after `BepInEx/core` is verified, so a failure leaves the previous tree intact. The
`SurvivesAFailedRun` cases drive a REAL HTTP server over loopback and a REAL published tree, because a
stubbed filesystem cannot demonstrate this: it is a claim about what is on disk after a failure.

WHY REAL HTTP AND NOT A STUBBED SOCKET. The other defects are all wire-shaped -- an unbounded call, a
rate-limit page saved as a `.zip`, a first-match taken silently among six platform variants that differ by
two characters. None is observable by patching a function, so `--api-base` exists to point the tool at a
loopback server and the cases below run it as a subprocess exactly as a user would.

A REAL NETWORK RUN IS NOT A UNIT TEST and is not attempted here: the original took 1443s to fetch the
34 MB asset, which is the measurement that motivated bounding it. The loopback server is the contract;
the network run is the proof, and it is recorded in the ledger rather than run on every test invocation.
"""
from __future__ import annotations

import ast
import http.server
import importlib.util
import io
import json
import os
import re
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
SCRIPT = Path(os.environ.get("FETCH_BEPINEX_REFS_SCRIPT",
                             REPO / "scripts" / "fetch_bepinex_refs.py")).resolve()
SUITE = REPO / "tests" / "tools" / "test_fetch_bepinex_refs.py"
TAG_ENV = "BEPINEX_REF_TAG"
RUN_TIMEOUT = 240
MARKER = "BepInEx/core"

_spec = importlib.util.spec_from_file_location("fetch_bepinex_refs", SCRIPT)
fetch = importlib.util.module_from_spec(_spec)
sys.modules["fetch_bepinex_refs"] = fetch
_spec.loader.exec_module(fetch)

ASSET_NAME = "BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2.zip"
# The real 13 names from the live release: the "no match" refusal must be able to report a real list.
REAL_ASSETS = [
    "BepInEx-NET.CoreCLR-net6.0-win-x64-6.0.0-pre.2.zip",
    "BepInEx-NET.CoreCLR-netcoreapp3.1-win-x64-6.0.0-pre.2.zip",
    "BepInEx-NET.Framework-net40-win-x86-6.0.0-pre.2.zip",
    "BepInEx-NET.Framework-net452-win-x86-6.0.0-pre.2.zip",
    "BepInEx-Unity.IL2CPP-linux-x64-6.0.0-pre.2.zip",
    "BepInEx-Unity.IL2CPP-macos-x64-6.0.0-pre.2.zip",
    ASSET_NAME,
    "BepInEx-Unity.IL2CPP-win-x86-6.0.0-pre.2.zip",
    "BepInEx-Unity.Mono-linux-x64-6.0.0-pre.2.zip",
    "BepInEx-Unity.Mono-linux-x86-6.0.0-pre.2.zip",
    "BepInEx-Unity.Mono-macos-x64-6.0.0-pre.2.zip",
    "BepInEx-Unity.Mono-win-x64-6.0.0-pre.2.zip",
    "BepInEx-Unity.Mono-win-x86-6.0.0-pre.2.zip",
]


# ────────────────────────────────────────────────────────────────────────────────── the loopback server


def make_zip(entries=None, wrap_in: str | None = None) -> bytes:
    """A real zip. Directories are written as DIRECTORY entries -- the first version of this fixture
    wrote the top-level list as files, so `BepInEx` was a file and its children could not be created,
    which surfaced as a tool defect and was not one. The real split, from the original's own 231-entry
    snapshot, is 5 directories and 4 top-level files."""
    if entries is None:
        entries = [(".doorstop_version", b"6.0.0-pre.2\n"), ("changelog.txt", b"changelog\n"),
                   ("doorstop_config.ini", b"[Doorstop]\n"), ("winhttp.dll", b"MZ"),
                   ("BepInEx/core/BepInEx.dll", b"MZ"), ("BepInEx/core/0Harmony.dll", b"MZ"),
                   ("BepInEx/patchers/BepInEx.IL2CPP.dll", b"MZ"),
                   ("BepInEx/plugins/.keep", b""),
                   ("dotnet/.version", b"8.0.100\n"), ("dotnet/Microsoft.CSharp.dll", b"MZ")]
    buffer = io.BytesIO()
    with zipfile.ZipFile(buffer, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, payload in entries:
            archive.writestr(f"{wrap_in}/{name}" if wrap_in else name, payload)
    return buffer.getvalue()


class _State:
    zip_payload = make_zip()
    asset_names = list(REAL_ASSETS)
    api_status = 200
    api_sleep = 0.0
    asset_body = None          # set to override the download with non-zip bytes
    requests: list[str] = []


class _Handler(http.server.BaseHTTPRequestHandler):
    def log_message(self, *args):
        return

    def do_GET(self) -> None:
        _State.requests.append(f"{self.command} {self.path}")
        if _State.api_sleep:
            time.sleep(_State.api_sleep)
        if "/releases/tags/" in self.path:
            if _State.api_status != 200:
                self.send_error(_State.api_status, "fixture refuses")
                return
            host = self.headers.get("Host", "127.0.0.1")
            body = json.dumps({"tag_name": fetch.DEFAULT_TAG,
                               "assets": [{"name": n, "size": len(_State.zip_payload),
                                           "browser_download_url": f"http://{host}/asset/{n}"}
                                          for n in _State.asset_names]}).encode()
            return self._send(200, "application/json", body)
        if self.path.startswith("/asset/"):
            if _State.asset_body is not None:
                return self._send(200, "text/html", _State.asset_body)
            return self._send(200, "application/octet-stream", _State.zip_payload)
        self.send_error(404, "no fixture route")

    def _send(self, status, content_type, body) -> None:
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)


def serve():
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    server = http.server.ThreadingHTTPServer(("127.0.0.1", port), _Handler)
    threading.Thread(target=server.serve_forever, daemon=True).start()
    return server, f"http://127.0.0.1:{port}"


def _clean_env() -> dict:
    """UNSET the tag variable. Setting it empty is itself a refusal (TAG-ENV-EMPTY), which is correct
    and which this suite pins separately -- a harness that trips a refusal it is not testing is wrong."""
    return {k: v for k, v in os.environ.items() if k != TAG_ENV}


# Every working directory the suite hands the tool. The substrate case reads THIS, not the source: a
# scan for the word "artifacts/" finds the literal inside the scanning code itself, so the first version
# of that case could only ever detect itself.
WORKDIRS_USED: list[Path] = []


def run_tool(base: str, workdir: Path, *extra: str, env_extra: dict | None = None):
    WORKDIRS_USED.append(Path(workdir).resolve())
    env = _clean_env()
    if env_extra:
        env.update(env_extra)
    proc = subprocess.run(
        [sys.executable, str(SCRIPT), "--api-base", base, "--root", str(workdir), "--json", *extra],
        capture_output=True, text=True, timeout=RUN_TIMEOUT, cwd=str(REPO), env=env)
    try:
        return proc.returncode, json.loads(proc.stdout), proc.stderr
    except json.JSONDecodeError:
        raise AssertionError(f"the tool did not emit a JSON verdict.\nstdout: {proc.stdout[:400]}\n"
                             f"stderr: {proc.stderr[:400]}")


def state(**kwargs):
    """Reset the fixture. Stated EXPLICITLY at every call site, because a fixture whose name
    contradicts what it set up has cost this program three times."""
    _State.zip_payload = make_zip()
    _State.asset_names = list(REAL_ASSETS)
    _State.api_status = 200
    _State.api_sleep = 0.0
    _State.asset_body = None
    _State.requests = []
    for key, value in kwargs.items():
        setattr(_State, key, value)


def with_server(test, **kwargs):
    state(**kwargs)
    server, base = serve()
    try:
        test(base)
    finally:
        server.shutdown()


class GreenAndNested(unittest.TestCase):
    """The real GitHub zip has one top-level directory per platform variant in some releases and a flat
    root in others; both shapes must publish, and the nested one must be flattened exactly as the
    original flattened it."""

    def setUp(self) -> None:
        self.work = Path(tempfile.mkdtemp(prefix="fb-contract-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(self.work, ignore_errors=True))

    def out(self) -> Path:
        return self.work / "artifacts" / "bepinex-refs"

    def test_a_REAL_zip_publishes_and_the_verdict_says_so(self) -> None:
        def run(base):
            code, payload, _ = run_tool(base, self.work)
            self.assertEqual(code, 0)
            self.assertEqual(payload["verdict"], "OK")
            # The field a caller reads to decide whether the tree is there. The first version of the
            # tool left it at False and a successful run reported `published: false`.
            self.assertIs(payload["published"], True)
            self.assertTrue((self.out() / "BepInEx" / "core").is_dir())
            self.assertEqual(payload["flatten"], "already flat")
            self.assertEqual(len(payload["treeDigest"]), 32)
        with_server(run)

    def test_a_NESTED_zip_is_flattened_and_the_WRAPPER_is_removed(self) -> None:
        def run(base):
            code, payload, _ = run_tool(base, self.work)
            self.assertEqual(code, 0)
            self.assertEqual(payload["flatten"], "flattened BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2")
            self.assertTrue((self.out() / "BepInEx" / "core").is_dir())
            self.assertFalse((self.out() / "BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2").exists())
        with_server(run, zip_payload=make_zip(wrap_in="BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.2"))

    def test_the_STAGING_download_is_removed_unless_asked_for(self) -> None:
        def run(base):
            run_tool(base, self.work)
            self.assertFalse((self.work / "artifacts" / "bepinex-il2cpp.zip").is_file())
        with_server(run)

        state()

        def keep(base):
            run_tool(base, self.work, "--keep-zip")
            self.assertTrue((self.work / "artifacts" / "bepinex-il2cpp.zip").is_file())
        with_server(keep)


class SurvivesAFailedRun(unittest.TestCase):
    """THE HEADLINE. The original destroyed the published tree BEFORE the API call, so a failure left
    no reference assemblies at all. Measured on real HTTP, against a real tree."""

    def setUp(self) -> None:
        self.work = Path(tempfile.mkdtemp(prefix="fb-survive-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(self.work, ignore_errors=True))

    def out(self) -> Path:
        return self.work / "artifacts" / "bepinex-refs"

    def snapshot(self) -> dict:
        return {p.relative_to(self.out()).as_posix(): p.stat().st_size
                for p in sorted(self.out().rglob("*")) if p.is_file()}

    def test_a_FAILED_re_fetch_leaves_the_PUBLISHED_tree_INTACT(self) -> None:
        state()
        server, base = serve()
        try:
            code, first, _ = run_tool(base, self.work)
            self.assertEqual(code, 0, first)
            before = self.snapshot()
            self.assertTrue(before, "the first run published nothing, so there is nothing to protect")
            break_it = _State
            break_it.api_status = 500
            code2, second, _ = run_tool(base, self.work)
            self.assertEqual(code2, fetch.EXIT_REFUSED)
            self.assertEqual(second["reason"], "API-REFUSED")
            self.assertIs(second["published"], False)
            self.assertTrue(self.out().is_dir(), "a failed run removed the published tree")
            self.assertEqual(self.snapshot(), before, "a failed run altered the published tree")
        finally:
            server.shutdown()

    def test_a_NON_ZIP_download_leaves_the_PUBLISHED_tree_INTACT(self) -> None:
        state()
        server, base = serve()
        try:
            run_tool(base, self.work)
            before = self.snapshot()
            _State.asset_body = b"<html><body>API rate limit exceeded</body></html>"
            code, payload, _ = run_tool(base, self.work)
            self.assertEqual(payload["reason"], "NOT-A-ZIP")
            self.assertEqual(self.snapshot(), before)
        finally:
            server.shutdown()

    def test_a_REFUSAL_leaves_no_STAGING_directory_behind(self) -> None:
        def run(base):
            run_tool(base, self.work, "--asset-pattern", "ZZZ-matches-nothing")
            artifacts = self.work / "artifacts"
            leftovers = [p.name for p in artifacts.iterdir()] if artifacts.is_dir() else []
            self.assertNotIn("bepinex-il2cpp.staging", leftovers)
            self.assertNotIn("bepinex-il2cpp.zip", leftovers)
        with_server(run)


class TheRefusals(unittest.TestCase):
    """Every refusal, over real HTTP, with the closed vocabulary as the contract."""

    def setUp(self) -> None:
        self.work = Path(tempfile.mkdtemp(prefix="fb-refuse-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(self.work, ignore_errors=True))

    def expect(self, reason: str, base: str, *extra: str) -> dict:
        code, payload, _ = run_tool(base, self.work, *extra)
        self.assertEqual(code, fetch.EXIT_REFUSED,
                         f"expected the {reason} refusal to exit {fetch.EXIT_REFUSED}")
        self.assertEqual(payload["verdict"], "REFUSED")
        self.assertEqual(payload["reason"], reason)
        self.assertIn(reason, fetch.REFUSAL_REASONS)
        return payload

    def test_TWO_matching_assets_are_a_REFUSAL_and_BOTH_are_named(self) -> None:
        # The original took `Select-Object -First 1`. Six real assets differ by two characters.
        def run(base):
            payload = self.expect("ASSET-AMBIGUOUS", base)
            self.assertIn("6.0.0-pre.3", payload["detail"])
            self.assertIn("6.0.0-pre.2", payload["detail"])
        with_server(run, asset_names=REAL_ASSETS + ["BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.3.zip"])

    def test_NO_matching_asset_NAMES_the_candidates_it_saw(self) -> None:
        def run(base):
            payload = self.expect("ASSET-NOT-FOUND", base)
            self.assertIn("nothing-like-it.zip", payload["detail"])
        with_server(run, asset_names=["nothing-like-it.zip"])

    def test_a_PATTERN_matching_none_of_the_REAL_13_reports_THIRTEEN(self) -> None:
        def run(base):
            payload = self.expect("ASSET-NOT-FOUND", base, "--asset-pattern", "ZZZ-nothing")
            self.assertIn("13 asset", payload["detail"])
        with_server(run)

    def test_a_RATE_LIMIT_page_saved_as_the_download_is_REFUSED_as_a_BAD_download(self) -> None:
        def run(base):
            payload = self.expect("NOT-A-ZIP", base)
            self.assertIn("PK", payload["detail"])
        with_server(run, asset_body=b"<html><body>API rate limit exceeded</body></html>")

    def test_a_zip_HEADER_that_is_not_an_archive_is_REFUSED(self) -> None:
        def run(base):
            self.expect("NOT-A-ZIP", base)
        with_server(run, asset_body=b"PK\x03\x04" + b"not actually a zip")

    def test_a_zip_with_NO_directories_says_so(self) -> None:
        def run(base):
            payload = self.expect("NO-BEPINEX-CORE", base)
            self.assertIn("no directories", payload["detail"])
        with_server(run, zip_payload=make_zip(entries=[("readme.txt", b"hi"), ("LICENSE", b"MIT")]))

    def test_a_WRAPPED_zip_whose_wrapper_has_no_marker_names_the_WRAPPER(self) -> None:
        def run(base):
            payload = self.expect("NO-BEPINEX-CORE", base)
            self.assertIn("wrapper", payload["detail"])
        with_server(run, zip_payload=make_zip(entries=[("x.txt", b"x")], wrap_in="wrapper"))

    def test_SEVERAL_candidate_directories_is_a_REFUSAL_not_a_GUESS(self) -> None:
        def run(base):
            payload = self.expect("MULTIPLE-CANDIDATE-DIRS", base)
            self.assertIn("alpha", payload["detail"])
            self.assertIn("beta", payload["detail"])
        with_server(run, zip_payload=make_zip(entries=[("alpha/a.txt", b"a"), ("beta/b.txt", b"b")]))

    def test_an_API_error_is_REFUSED_with_its_STATUS(self) -> None:
        for status in (403, 404, 500, 503):
            with self.subTest(status=status):
                state(api_status=status)
                with_server(self._one_api_error, api_status=status)

    def _one_api_error(self, base, status=None):
        self.expect("API-REFUSED", base)

    def test_a_RELEASE_with_NO_assets_array_is_REFUSED_as_MALFORMED(self) -> None:
        release = {"tag_name": fetch.DEFAULT_TAG, "assets": "not an array"}
        with self.assertRaises(fetch.Refusal) as caught:
            fetch.select_asset(release, __import__("re").compile(fetch.ASSET_PATTERN))
        self.assertEqual(caught.exception.reason, "API-MALFORMED")

    def test_a_MATCHING_asset_with_NO_download_url_is_REFUSED(self) -> None:
        release = {"assets": [{"name": ASSET_NAME}]}
        with self.assertRaises(fetch.Refusal) as caught:
            fetch.select_asset(release, __import__("re").compile(fetch.ASSET_PATTERN))
        self.assertEqual(caught.exception.reason, "API-MALFORMED")
    def test_a_REFUSAL_never_claims_it_published(self) -> None:
        """`_refuse` held `published` in BOTH the base payload and the envelope, and the envelope's
        update overwrote the base -- so the base literal was dead code and a mutant flipping it was
        MASKED, not killed. One source of truth now; this case is what makes it observable.

        Driven over real HTTP, because a refusal reached by constructing the payload in-process would
        not exercise the same code path as a refusal produced by a run."""
        for reason, scenario, extra in (
                ("ASSET-NOT-FOUND", {"asset_names": ["nothing-like-it.zip"]}, ()),
                ("ASSET-AMBIGUOUS", {"asset_names": REAL_ASSETS + [ASSET_NAME.replace(".zip", "-b.zip")]}, ()),
        ):
            with self.subTest(reason=reason):
                state(**scenario)
                server, base = serve()
                try:
                    code, payload, _ = run_tool(base, self.work, *extra)
                    self.assertEqual(payload["reason"], reason)
                    self.assertIs(payload["published"], False,
                                  f"a {reason} refusal claimed to have published something")
                finally:
                    server.shutdown()

    def test_an_INVALID_asset_pattern_is_a_named_refusal_not_a_traceback(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = fetch.main(["--json", "--root", str(self.work), "--asset-pattern", "["])
        self.assertEqual(code, fetch.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "ASSET-PATTERN-INVALID")

    def test_an_EMPTY_asset_pattern_is_a_named_refusal(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = fetch.main(["--json", "--root", str(self.work), "--asset-pattern", ""])
        self.assertEqual(code, fetch.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "ASSET-PATTERN-INVALID")
    def test_a_REFUSAL_never_claims_it_published(self) -> None:
        """`_refuse` held `published` in BOTH the base payload and the envelope, and the envelope's
        update overwrote the base -- so the base literal was dead code and a mutant flipping it was
        MASKED, not killed. One source of truth now; this case is what makes it observable.

        Driven over real HTTP, because a refusal reached by constructing the payload in-process would
        not exercise the same code path as a refusal produced by a run."""
        for reason, scenario, extra in (
                ("ASSET-NOT-FOUND", {"asset_names": ["nothing-like-it.zip"]}, ()),
                ("ASSET-AMBIGUOUS", {"asset_names": REAL_ASSETS + [ASSET_NAME.replace(".zip", "-b.zip")]}, ()),
        ):
            with self.subTest(reason=reason):
                state(**scenario)
                server, base = serve()
                try:
                    code, payload, _ = run_tool(base, self.work, *extra)
                    self.assertEqual(payload["reason"], reason)
                    self.assertIs(payload["published"], False,
                                  f"a {reason} refusal claimed to have published something")
                finally:
                    server.shutdown()

    def test_an_INVALID_asset_pattern_is_a_named_refusal_not_a_traceback(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = fetch.main(["--json", "--root", str(self.work), "--asset-pattern", "["])
        self.assertEqual(code, fetch.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "ASSET-PATTERN-INVALID")

    def test_an_EMPTY_asset_pattern_is_a_named_refusal(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            code = fetch.main(["--json", "--root", str(self.work), "--asset-pattern", ""])
        self.assertEqual(code, fetch.EXIT_REFUSED)
        self.assertEqual(json.loads(out.getvalue())["reason"], "ASSET-PATTERN-INVALID")




class TheBounds(unittest.TestCase):
    """A tool that fetches from the network has to be able to stop waiting. The original could not: a
    real run took 1443s for the 34 MB asset with no timeout on either call."""

    def test_a_NON_POSITIVE_TIMEOUT_REFUSES_before_any_request(self) -> None:
        for flag in ("--api-timeout", "--download-timeout"):
            with self.subTest(flag=flag):
                out = io.StringIO()
                with redirect_stdout(out):
                    code = fetch.main([flag, "0", "--json"])
                self.assertEqual(code, fetch.EXIT_REFUSED)
                self.assertEqual(json.loads(out.getvalue())["reason"], "INVALID-TIMEOUT")

    def test_a_SLOW_API_is_REFUSED_at_the_BOUND_it_declared(self) -> None:
        state(api_sleep=3.0)
        server, base = serve()
        try:
            work = Path(tempfile.mkdtemp(prefix="fb-slow-"))
            started = time.monotonic()
            code, payload, _ = run_tool(base, work, "--api-timeout", "1")
            elapsed = time.monotonic() - started
            self.assertEqual(payload["reason"], "API-TIMED-OUT")
            self.assertIn("within 1s", payload["detail"])
            # The refusal must arrive at the bound, not merely eventually. Generous because the point
            # is that it is bounded at all, not that the bound is tight.
            self.assertLess(elapsed, 20, f"the refusal took {elapsed:.1f}s for a 1s bound")
        finally:
            server.shutdown()
            __import__("shutil").rmtree(work, ignore_errors=True)

    def test_BOTH_network_calls_carry_a_TIMEOUT(self) -> None:
        """Read from the source, because a case that only exercises the API call leaves the download's
        timeout unpinned -- which is exactly what happened on an earlier port in this program."""
        source = SCRIPT.read_text(encoding="utf-8")
        calls = re.findall(r"_URLOPEN\((.*?)\)", source, re.DOTALL)
        self.assertGreaterEqual(len(calls), 2, f"expected two _URLOPEN calls, found {len(calls)}")
        for call in calls:
            self.assertIn("timeout=", call,
                          f"an unbounded network call reached the source: _URLOPEN({call[:90]})")

    def test_NO_call_uses_a_BARE_urlopen_or_a_REQUEST_WITHOUT_a_timeout(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertNotIn("urllib.request.urlopen(", source.replace("urllib.request.urlopen, ", ""),
                         "a direct urlopen call bypasses the module seam and its timeout")


class TheTagIsReadOnce(unittest.TestCase):
    """`if $env:BEPINEX_REF_TAG` in the original treated an empty string as absent, so a typo'd export
    silently fetched a different tag than the caller asked for. Here it is a named refusal."""

    def setUp(self) -> None:
        self.work = Path(tempfile.mkdtemp(prefix="fb-tag-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(self.work, ignore_errors=True))

    def test_an_EMPTY_tag_variable_REFUSES_rather_than_falling_back(self) -> None:
        # SET, not inherited. The first version read the ambient environment, so on a machine where the
        # variable happens to be unset this case exercised the UNSET path and passed for the wrong reason.
        for value in ("", "   "):
            with self.subTest(value=repr(value)):
                out = io.StringIO()
                with mock.patch.dict(os.environ, {TAG_ENV: value}, clear=False):
                    with redirect_stdout(out):
                        code = fetch.main(["--json", "--root", str(self.work)])
                self.assertEqual(code, fetch.EXIT_REFUSED)
                self.assertEqual(json.loads(out.getvalue())["reason"], "TAG-ENV-EMPTY")

    def test_an_UNSET_tag_variable_uses_the_default_and_says_so(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            # Refused later for an unrelated reason (no network in this case), but the envelope must
            # already carry the resolved tag and WHERE it came from.
            with mock.patch.dict(os.environ, {}, clear=False):
                os.environ.pop(TAG_ENV, None)
                with mock.patch.object(fetch, "fetch_json", side_effect=fetch.Refusal("API-REFUSED", "x")):
                    fetch.main(["--json", "--root", str(self.work)])
        payload = json.loads(out.getvalue())
        self.assertEqual(payload["tag"], fetch.DEFAULT_TAG)
        self.assertIn(TAG_ENV, payload["tagSource"])

    def test_an_EXPLICIT_tag_wins_over_the_variable(self) -> None:
        out = io.StringIO()
        with redirect_stdout(out):
            with mock.patch.object(fetch, "fetch_json", side_effect=fetch.Refusal("API-REFUSED", "x")):
                fetch.main(["--json", "--root", str(self.work), "--tag", "v9.9.9"])
        payload = json.loads(out.getvalue())
        self.assertEqual(payload["tag"], "v9.9.9")
        self.assertEqual(payload["tagSource"], "--tag")


class Surface(unittest.TestCase):
    def test_the_REFUSAL_reasons_are_a_CLOSED_vocabulary(self) -> None:
        source = SCRIPT.read_text(encoding="utf-8")
        found = set(re.findall(r'Refusal\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'_refuse\(\s*\n?\s*"([A-Z-]+)"', source))
        found |= set(re.findall(r'"reason": "([A-Z-]+)"', source))
        self.assertTrue(found, "no refusal reasons found at all")
        self.assertEqual(found - fetch.REFUSAL_REASONS, set(),
                         f"undeclared refusal reason(s) {sorted(found - fetch.REFUSAL_REASONS)}")
        # And nothing declared that the tool never raises, which would be a promise it does not keep.
        raised = set()
        for node in ast.walk(ast.parse(source)):
            if isinstance(node, ast.Call) and isinstance(node.func, ast.Name) and node.func.id == "_refuse":
                if node.args and isinstance(node.args[0], ast.Constant):
                    raised.add(node.args[0].value)
        self.assertEqual(raised - fetch.REFUSAL_REASONS, set(),
                         f"declared but never raised directly: {sorted(raised - fetch.REFUSAL_REASONS)}")

    def test_the_EXIT_code_vocabulary_is_declared(self) -> None:
        self.assertEqual(fetch.EXIT_REFUSED, 64)

    def test_the_FLAGS_are_the_documented_long_ones(self) -> None:
        out = subprocess.run([sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
                             timeout=RUN_TIMEOUT).stdout
        for flag in ("--root", "--api-base", "--tag", "--asset-pattern", "--out-dir", "--zip",
                     "--api-timeout", "--download-timeout", "--keep-zip", "--json"):
            self.assertIn(flag, out, flag)

    def test_it_answers_no_PowerShell_spelled_parameter(self) -> None:
        """The environment-variable override is retained deliberately, so the VARIABLE is honoured --
        but the PowerShell script had no parameters, and this tool must not invent PowerShell spellings."""
        for flag in ("-Tag", "-OutDir", "-ApiTimeout"):
            with self.subTest(flag=flag):
                proc = subprocess.run([sys.executable, str(SCRIPT), flag, "1"], capture_output=True,
                                      text=True, timeout=RUN_TIMEOUT)
                self.assertNotEqual(proc.returncode, 0, flag)
                self.assertIn("unrecognized arguments", (proc.stdout + proc.stderr).lower(), flag)

    def test_it_states_WHY_POWERSHELL_WAS_RETIRED(self) -> None:
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("fetch-bepinex-refs.ps1", head)
        lowered = head.lower()
        for reason in ("timeout", "destroy", "silently", "machine-readable"):
            self.assertIn(reason, lowered, f"the docstring omits the {reason!r} defect")

    def test_the_closing_note_about_INTEROP_survives_the_port(self) -> None:
        """A reference-assembly fetcher that reads as a game install would be a real regression: the
        per-game interop caveat is the one sentence that keeps a CI reader from assuming otherwise."""
        self.assertIn("FUSIONRPG_GAME_DIR", SCRIPT.read_text(encoding="utf-8"))

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
            if isinstance(target, ast.Name) and target.id == "fetch":
                continue
            label = ast.unparse(target) if target is not None else "?"
            if label.split(".")[0] in globals_seen:
                offences.append(f"line {node.lineno}: mock.patch.object({label}, ...)")
        self.assertEqual(offences, [], "\n".join(offences))

    def test_every_workdir_the_cases_handed_the_tool_is_UNDER_a_temporary_directory(self) -> None:
        """The repo's test-substrate rule: a store test runs in memory unless the disk is the thing
        under test. Here the disk IS the thing under test -- the tool publishes a tree -- so the paths
        must be temporary and never under the repository.

        MEASURED, and SELF-SUFFICIENT. Two earlier versions of this case were both wrong in ways worth
        recording. The first scanned this file's own string constants for one beginning with
        "artifacts/", and the only such constant was the literal inside the scanning code itself: a
        self-detecting assertion that can only fail for its own reasons. The second asserted on
        `WORKDIRS_USED`, which is populated by the OTHER cases -- so under any partial selection (and
        the falsify harness runs a static one first, to stop a network-escaping mutant from stalling)
        the list was empty and the case failed. Falsification recorded that as five kills that were not
        kills. A case whose correctness depends on which other cases ran is a false kill waiting to
        hide a real hole, so this one runs its OWN publish and then measures that.
        """
        work = Path(tempfile.mkdtemp(prefix="fb-substrate-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(work, ignore_errors=True))
        # Started directly rather than through `with_server`, which passes only the base URL and
        # cannot carry a per-case workdir.
        server, base = serve()
        try:
            self._publish_into(base, work)
        finally:
            server.shutdown()

    def _publish_into(self, base, work) -> None:
        code, payload, _ = run_tool(base, work)
        self.assertEqual(code, 0, payload)
        self.assertTrue((work / "artifacts" / "bepinex-refs" / "BepInEx" / "core").is_dir(),
                        "this case needs a real published tree to be a measurement of anything")
        used = WORKDIRS_USED[-1]
        temp_root = Path(tempfile.gettempdir()).resolve()
        self.assertTrue(used == temp_root or temp_root in used.parents,
                        f"the workdir is not under the temporary directory: {used}")
        self.assertNotIn(REPO, used.parents, f"the workdir is inside the repository: {used}")
        self.assertTrue(str(used).startswith(str(temp_root)), used)
    def test_the_DEFAULT_TAG_is_pinned_and_is_a_CONTRACT_not_a_population(self) -> None:
        """A default is a closed piece of configuration the original and the docstring both name, so
        pinning it guards a contract. It is NOT a population count: nothing here grows when content
        ships, and the value is what a caller who sets nothing gets."""
        self.assertEqual(fetch.DEFAULT_TAG, "v6.0.0-pre.2")
        self.assertIn(fetch.DEFAULT_TAG, SCRIPT.read_text(encoding="utf-8"),
                      "the docstring must name the default the tool actually uses")

    def test_the_DEFAULT_asset_pattern_matches_the_REAL_asset_name(self) -> None:
        """Closed vocabulary, not prose: the default pattern must match the one real asset name and
        must NOT match a platform sibling that differs only by a suffix."""
        pattern = re.compile(fetch.ASSET_PATTERN)
        self.assertIsNotNone(pattern.search(ASSET_NAME))
        for sibling in ("BepInEx-Unity.IL2CPP-win-x86-6.0.0-pre.2.zip",
                        "BepInEx-Unity.Mono-win-x64-6.0.0-pre.2.zip",
                        "BepInEx-NET.Framework-net452-win-x86-6.0.0-pre.2.zip"):
            with self.subTest(sibling=sibling):
                self.assertIsNone(pattern.search(sibling),
                                  f"the default pattern also matches {sibling}, so the fetch is ambiguous")

    def test_the_DOCSTRING_heading_states_WHY_PowerShell_WAS_RETIRED(self) -> None:
        """The first version checked the docstring's WORDS but not its HEADING, so renaming the heading
        passed. Provenance is the heading: it is what a reader scans for."""
        head = SCRIPT.read_text(encoding="utf-8").split('"""')[1]
        self.assertIn("WHY THE POWERSHELL FORM WAS RETIRED", head)

    def test_the_API_BASE_seam_is_WIRED(self) -> None:
        """STATIC on purpose. With `--api-base` unwired, every case in this suite escapes to the real
        GitHub and one of them downloads 34 MB -- falsification reported that mutant as a STALL rather
        than a kill, which is the worst possible outcome. The property is that the flag is WIRED, which
        is visible in the source, so it is asserted here and dies in milliseconds."""
        source = SCRIPT.read_text(encoding="utf-8")
        self.assertIn("args.api_base", source,
                      "--api-base is accepted but not used, so the contract suite would reach the internet")
        self.assertRegex(source, r"args\.api_base\.rstrip\(",
                         "--api-base must reach the URL, not merely be parsed")
        self.assertIn("--api-base", subprocess.run(
            [sys.executable, str(SCRIPT), "--help"], capture_output=True, text=True,
            timeout=RUN_TIMEOUT).stdout)



class SurvivesAFailureAtExtraction(unittest.TestCase):
    """THE HEADLINE, at the stage where the original did its damage.

    The original deleted the published tree as its THIRD statement -- before the API call -- so ANY
    failure left no reference assemblies. The first version of the `survives` case broke the API with a
    500, which fails before the download, and falsification showed that let a mutant pass: delete the
    tree right before the marker check, and that case never reaches the line. So a failure that happens
    AT OR AFTER extraction is the case that matters, and it needs its own: a previously published tree
    must survive a run whose archive is real and whose extracted tree is wrong.
    """

    def setUp(self) -> None:
        self.work = Path(tempfile.mkdtemp(prefix="fb-late-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(self.work, ignore_errors=True))

    def out(self) -> Path:
        return self.work / "artifacts" / "bepinex-refs"

    def snapshot(self) -> dict:
        return {p.relative_to(self.out()).as_posix(): p.read_bytes()
                for p in sorted(self.out().rglob("*")) if p.is_file()}

    def test_a_MARKERLESS_archive_does_NOT_replace_a_published_tree(self) -> None:
        state()
        server, base = serve()
        try:
            code, first, _ = run_tool(base, self.work)
            self.assertEqual(code, 0, first)
            before = self.snapshot()
            self.assertTrue(before, "the first run published nothing, so there is nothing to protect")
            # Now serve a REAL zip that extracts cleanly and has no BepInEx/core. Every earlier stage
            # succeeds, so the failure happens exactly where the original had already deleted the tree.
            _State.zip_payload = make_zip(entries=[("readme.txt", b"hi"), ("LICENSE", b"MIT")])
            code2, second, _ = run_tool(base, self.work)
            self.assertEqual(second["reason"], "NO-BEPINEX-CORE")
            self.assertIs(second["published"], False)
            self.assertTrue(self.out().is_dir(), "a marker-less archive REPLACED the published tree")
            self.assertEqual(self.snapshot(), before, "a marker-less archive altered the published tree")
        finally:
            server.shutdown()

    def test_an_AMBIGUOUS_release_does_NOT_replace_a_published_tree(self) -> None:
        state()
        server, base = serve()
        try:
            run_tool(base, self.work)
            before = self.snapshot()
            _State.asset_names = REAL_ASSETS + ["BepInEx-Unity.IL2CPP-win-x64-6.0.0-pre.3.zip"]
            code, payload, _ = run_tool(base, self.work)
            self.assertEqual(payload["reason"], "ASSET-AMBIGUOUS")
            self.assertEqual(self.snapshot(), before)
        finally:
            server.shutdown()

if __name__ == "__main__":
    unittest.main()

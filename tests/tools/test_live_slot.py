"""Tests for `gk-core/scripts/live_slot.py` — the Python port of the retired `scripts/live-slot.ps1`.

Two jobs, in order of how much they are worth:

1. **The contract itself.** States, the stored-or-derived port rule, the five required install
   entries, the refusal vocabulary and the exit codes. The five required entries are pinned
   directly (the `.ps1` that once declared them is retired), and the cfg key `deploy-play.py`
   writes is read from that file so a change on either side turns a test red instead of silently
   forking the rule.

2. **The two defects, end to end.** The port fixes two defects the PowerShell original carried: a
   clone that inherited the owner's server URL instead of stamping the slot's own port, and a
   `--force` re-clone that could not delete an install carrying a trailing-space directory name.
   `DefectOneCloneTests` and `ForceRecloneTests` pin both against the port, and
   `PlainRmtreeTests` pins the host-independent half of the second (the original's `Remove-Item`
   remedy only fails on Windows PowerShell 5.1, not on PowerShell 7, so `shutil.rmtree` failing
   on BOTH is the assertion that does not depend on which PowerShell is installed).

Substrate: throwaway game trees in a temp directory, because the disk IS the thing under test (a
clone is a directory mirror and one defect is a directory name Windows will not delete). Cleanup
runs through the same remedy the tool uses and is ASSERTED, never swallowed
(`docs/contributing/testing-standard.md` R3). The real pool and the real game are never touched.
"""
from __future__ import annotations

import importlib.util
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time
import unittest
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TOOL = REPO / "scripts" / "live_slot.py"
DEPLOY = REPO / "scripts" / "deploy-play.py"

OWNER_URL = "http://127.0.0.1:5088"

# The polluted-name shape that broke the original: a dotnet build command line pasted into a
# directory name, trailing space and all. Windows path APIs normalise the trailing space away, so
# the name cannot be addressed, listed or deleted through them.
BAD_DIR_NAME = "Mods --no-incremental --nologo -v n "


def _load():
    spec = importlib.util.spec_from_file_location("live_slot_under_test", TOOL)
    assert spec and spec.loader
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


live_slot = _load()


def extended(path: Path) -> str:
    """The `\\\\?\\` form, built by string prefix and NEVER via `os.path.abspath`.

    Measured: `os.path.abspath` runs `ntpath.normpath`, which strips the trailing space off the
    last component, so a `\\?\\` path built that way silently creates the *trimmed* name and the
    reproduction quietly tests nothing. `str()` of an already-absolute `Path` is not normalised, so
    the prefix is applied directly.
    """
    text = str(path)
    assert os.path.isabs(text), text
    return text if text.startswith("\\\\?\\") else "\\\\?\\" + text


def mirror_empty_over(target: Path) -> int:
    """The one measured remedy: robocopy enumerates the unaddressable names itself."""
    robocopy = live_slot.find_robocopy()
    assert robocopy is not None, "robocopy is required by this test"
    with tempfile.TemporaryDirectory(prefix="test-empty-", dir=str(target.parent)) as empty:
        result = subprocess.run(
            [robocopy, empty, str(target), "/MIR", "/NFL", "/NDL", "/NJH", "/NJS", "/R:1", "/W:1"],
            capture_output=True, text=True, timeout=600, check=False)
    return result.returncode


class TempPool:
    """A throwaway pool with a fake source install that satisfies the verification contract."""

    def __init__(self, case: unittest.TestCase, owner_url: str = OWNER_URL) -> None:
        self.case = case
        self.root = Path(tempfile.mkdtemp(prefix="live-slot-test-"))
        self.root.mkdir(parents=True, exist_ok=True)
        case.addCleanup(self.destroy)
        self.pool = self.root / "pool"
        self.source = self.make_source(owner_url)

    def destroy(self) -> None:
        """Remove the tree even when it holds a name `shutil.rmtree` cannot address.

        A cleanup that cannot complete is a FAILURE (testing-standard.md R3), so the empty-mirror
        remedy runs and the assertion is made afterwards rather than swallowed in a `try`.
        """
        if not self.root.exists():
            return
        try:
            shutil.rmtree(self.root)
        except OSError:
            mirror_empty_over(self.root)
            shutil.rmtree(self.root)
        self.case.assertFalse(self.root.exists(),
                              f"the temp pool {self.root} survived cleanup -- a swallowed temp "
                              f"delete is the 65.5 GB incident this standard exists for")

    def make_source(self, server_url: str | None) -> Path:
        source = self.root / "source-install"
        for entry in live_slot.REQUIRED_ENTRIES:
            target = source / entry
            if entry in ("MelonLoader", "BepInEx", "Mods"):
                target.mkdir(parents=True, exist_ok=True)
                (target / "keep.txt").write_text("x", encoding="utf-8")
            else:
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_text("stub", encoding="utf-8")
        if server_url is not None:
            (source / "Mods" / "fusionrpg.cfg").write_text(
                "# FusionRpg MelonLoader host config (written by deploy-play.py)\n"
                f"ServerUrl={server_url}\n"
                "PersistCheats=false\n"
                "EnableUnsafeHitPatches=false\n", encoding="utf-8")
        return source

    def pollute(self, slot: int) -> Path:
        """Create the trailing-space directory inside a slot's install, the measured way."""
        install = self.pool / f"slot-{slot}"
        bad = install / BAD_DIR_NAME
        os.mkdir(extended(bad))
        with open(extended(bad / "marker.txt"), "w", encoding="utf-8") as handle:
            handle.write("polluted")
        return bad

    def registry(self) -> dict:
        return json.loads((self.pool / "slots.json").read_text(encoding="utf-8-sig"))

    def slot_cfg(self, slot: int) -> Path:
        return self.pool / f"slot-{slot}" / "Mods" / "fusionrpg.cfg"

    def cfg_server_url(self, slot: int) -> str | None:
        cfg = self.slot_cfg(slot)
        if not cfg.exists():
            return None
        found = live_slot.SERVER_URL_PATTERN.search(cfg.read_text(encoding="utf-8"))
        return found.group(1) if found else None

    def run(self, *args: str, timeout: float = 900.0):
        return subprocess.run([sys.executable, str(TOOL), *args], capture_output=True, text=True,
                              timeout=timeout, cwd=str(REPO), check=False)

    def live_slot(self, *args: str, timeout: float = 900.0):
        return self.run(*args, timeout=timeout)

    def clone(self, *extra: str, session: str = "test-session", timeout: float = 900.0):
        return self.live_slot("--clone", "--session", session, "--pool-root", str(self.pool),
                              "--source-install", str(self.source), *extra, timeout=timeout)

    def acquire(self, session: str = "test-session", *extra: str, timeout: float = 900.0):
        return self.live_slot("--acquire", "--session", session, "--pool-root", str(self.pool),
                              "--source-install", str(self.source), *extra, timeout=timeout)

    def status(self, *extra: str, timeout: float = 900.0):
        return self.live_slot("--status", "--pool-root", str(self.pool), *extra, timeout=timeout)

    def release(self, session: str = "test-session", timeout: float = 900.0):
        return self.live_slot("--release", "--session", session, "--pool-root", str(self.pool),
                              timeout=timeout)


def json_verdict(result: subprocess.CompletedProcess) -> dict:
    """The `--json` verdict, which is the last JSON object printed on stdout."""
    payload = result.stdout[result.stdout.index("{"):] if "{" in result.stdout else "{}"
    return json.loads(payload)


class RequiredEntriesContractTests(unittest.TestCase):
    """The verification contract is pinned directly; the `.ps1` that once declared it is retired."""

    def test_the_five_required_entries_are_the_documented_contract(self):
        self.assertEqual(
            live_slot.REQUIRED_ENTRIES,
            ("PlantsVsZombiesRH.exe", "MelonLoader", "BepInEx", "Mods", "GameAssembly.dll"),
            "the five entries are the contract: the MelonLoader host, the shipped content dirs and "
            "the executable. A clone missing any of them is broken, never ready")

    def test_the_cfg_key_this_tool_stamps_is_the_key_deploy_play_writes(self):
        deploy = DEPLOY.read_text(encoding="utf-8")
        written = re.findall(r"^\s*(f?\")?([A-Za-z][A-Za-z0-9_]*)=\{", deploy, re.MULTILINE)
        self.assertTrue(written, "deploy-play.py no longer writes a 'Key={url}' cfg line")
        # The line deploy-play.py WRITES and the pattern it VERIFIES with (:508) must be the same
        # key this tool stamps, or the stamp would edit a key the deploy never reads.
        verify = re.search(r're\.search\(r"\^([A-Za-z][A-Za-z0-9_]*)=', deploy)
        self.assertIsNotNone(verify, "deploy-play.py no longer verifies its cfg with ^Key=")
        self.assertEqual(verify.group(1), "ServerUrl")
        self.assertIn("ServerUrl", [name for _, name in written])

    def test_the_cfg_path_is_the_one_deploy_play_uses(self):
        deploy = DEPLOY.read_text(encoding="utf-8")
        self.assertIn('cfg_path: Path | None = plugin_dir / "fusionrpg.cfg"', deploy)
        self.assertEqual(live_slot.CFG_RELATIVE, Path("Mods") / "fusionrpg.cfg")

    def test_robocopy_success_range_is_the_documented_one(self):
        self.assertEqual(live_slot.ROBOCOPY_SUCCESS_MAX, 7)


class PortRuleTests(unittest.TestCase):
    def test_a_stored_port_wins_and_an_absent_one_is_derived(self):
        self.assertEqual(live_slot.resolve_port({"port": 5110}, 5100, 1), 5110)
        self.assertEqual(live_slot.resolve_port({}, 5100, 1), 5101)
        self.assertEqual(live_slot.resolve_port({"port": None}, 5100, 3), 5103)
        self.assertEqual(live_slot.resolve_port(None, 5100, 2), 5102)
        # The two halves are both load-bearing: an earlier reading of only the stored field
        # silently skipped two of three slots, so neither branch may be collapsed into the other.
        self.assertNotEqual(live_slot.resolve_port({}, 5100, 2), 5100)

    def test_the_owner_port_is_never_one_a_slot_derives_by_default(self):
        self.assertNotEqual(live_slot.OWNER_PORT, live_slot.resolve_port(None, 5100, 1))


class InstallContractTests(unittest.TestCase):
    def test_an_install_missing_any_required_entry_is_broken_not_ready(self):
        with tempfile.TemporaryDirectory() as temp:
            install = Path(temp)
            self.assertEqual(sorted(live_slot.test_install(install)),
                             sorted(live_slot.REQUIRED_ENTRIES))
            for entry in live_slot.REQUIRED_ENTRIES:
                (install / entry).mkdir(parents=True, exist_ok=True)
            self.assertEqual(live_slot.test_install(install), [])
            (install / "Mods").rmdir()
            self.assertEqual(live_slot.test_install(install), ["Mods"])


class ServerUrlStampTests(unittest.TestCase):
    """Defect 1, at the unit level. The end-to-end falsification is in DefectOneCloneTests."""

    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.install = Path(self.temp.name) / "install"
        (self.install / "Mods").mkdir(parents=True)
        self.log = live_slot.Logger()

    def write_cfg(self, body: str) -> Path:
        cfg = self.install / live_slot.CFG_RELATIVE
        cfg.write_text(body, encoding="utf-8")
        return cfg

    def test_a_clone_naming_the_owners_url_is_rewritten_to_the_slots_own_port(self):
        cfg = self.write_cfg(f"# header\nServerUrl={OWNER_URL}\nPersistCheats=false\n")
        result = live_slot.stamp_server_url(self.install, 2, 5102, self.log)
        self.assertEqual(result["outcome"], "stamped")
        self.assertEqual(result["previous"], OWNER_URL)
        text = cfg.read_text(encoding="utf-8")
        self.assertIn("ServerUrl=http://127.0.0.1:5102", text)
        self.assertNotIn(OWNER_URL, text)
        # only the one line changes -- the rest of the host config is not this tool's to touch
        self.assertIn("# header", text)
        self.assertIn("PersistCheats=false", text)
        self.assertEqual(len(text.splitlines()), 3)

    def test_a_cfg_with_no_server_url_key_is_refused_by_name_and_nothing_is_invented(self):
        original = "# header\nPersistCheats=false\n"
        cfg = self.write_cfg(original)
        with self.assertRaises(live_slot.Refusal) as caught:
            live_slot.stamp_server_url(self.install, 1, 5101, self.log)
        self.assertEqual(caught.exception.name, "CFG-NO-SERVERURL")
        self.assertEqual(cfg.read_text(encoding="utf-8"), original,
                         "a refusal must not have written a key it was told not to invent")
        self.assertIn("ServerUrl=", caught.exception.detail)

    def test_an_absent_cfg_warns_and_creates_nothing(self):
        # The BepInEx host has no cfg by design (deploy-play.py:277), so an absent one is not a
        # refusal: nothing was inherited, so there is no owner URL to replace.
        result = live_slot.stamp_server_url(self.install, 1, 5101, self.log)
        self.assertEqual(result["outcome"], "absent")
        self.assertIsNone(result["current"])
        self.assertFalse((self.install / live_slot.CFG_RELATIVE).exists())
        self.assertTrue(any("nothing was inherited" in line for line in self.log.lines))

    def test_stamping_is_idempotent(self):
        cfg = self.write_cfg(f"ServerUrl={OWNER_URL}\n")
        live_slot.stamp_server_url(self.install, 1, 5101, self.log)
        before = cfg.read_text(encoding="utf-8")
        second = live_slot.stamp_server_url(self.install, 1, 5101, self.log)
        self.assertEqual(second["outcome"], "already-correct")
        self.assertEqual(cfg.read_text(encoding="utf-8"), before)

    def test_a_stamp_that_names_the_owner_port_says_so_on_stdout(self):
        self.write_cfg(f"ServerUrl={OWNER_URL}\n")
        live_slot.stamp_server_url(self.install, 1, 5101, self.log)
        stamped = [line for line in self.log.lines if "stamped" in line]
        self.assertEqual(len(stamped), 1)
        self.assertIn(OWNER_URL, stamped[0])
        self.assertIn("http://127.0.0.1:5101", stamped[0])


class PoolRootTests(unittest.TestCase):
    def test_the_pool_root_is_trimmed_and_the_trim_is_announced(self):
        with tempfile.TemporaryDirectory() as temp:
            log = live_slot.Logger()
            resolved = live_slot.resolve_pool_root(f"{temp}\\pool  ", log)
            self.assertEqual(resolved, (Path(temp) / "pool").resolve())
            self.assertTrue(any("whitespace" in line for line in log.lines),
                            "a silent trim is how 'Cannot find path' reads like a missing pool")

    def test_a_trailing_separator_is_trimmed_and_announced_too(self):
        # live-slot.ps1:69 trims whitespace AND trailing separators in one step and announces
        # whenever the trimmed value differs, so the same shape is announced here.
        with tempfile.TemporaryDirectory() as temp:
            log = live_slot.Logger()
            resolved = live_slot.resolve_pool_root(f"{temp}\\pool\\", log)
            self.assertEqual(resolved, (Path(temp) / "pool").resolve())
            self.assertTrue(any("whitespace/separators" in line for line in log.lines))

    def test_no_pool_root_refuses_by_name(self):
        log = live_slot.Logger()
        for value in (None, "", "   "):
            with self.assertRaises(live_slot.Refusal) as caught:
                live_slot.resolve_pool_root(value, log)
            self.assertEqual(caught.exception.name, "POOL-ROOT-MISSING")

    def test_a_root_of_nothing_but_separators_refuses_rather_than_becoming_the_cwd(self):
        # `Path("")` is the current directory, so a "/" pool root would silently manage a
        # slots.json wherever the tool was started from.
        log = live_slot.Logger()
        for value in ("/", "\\", "  /  ", "\\\\"):
            with self.assertRaises(live_slot.Refusal) as caught:
                live_slot.resolve_pool_root(value, log)
            self.assertEqual(caught.exception.name, "POOL-ROOT-MISSING")


class RefusalContractTests(unittest.TestCase):
    def test_every_refusal_name_carries_its_meaning(self):
        for name, meaning in live_slot.REFUSALS.items():
            self.assertTrue(meaning.strip(), f"{name} has no meaning")
            self.assertGreater(len(meaning), 20, f"{name}'s meaning is too short to teach anything")

    def test_an_unnamed_refusal_cannot_be_raised(self):
        with self.assertRaises(KeyError):
            live_slot.Refusal("NOT-IN-THE-REGISTRY", "a refusal nobody can look up")

    def test_a_refusal_renders_its_stage_its_instance_and_its_remedy(self):
        refusal = live_slot.Refusal("ALL-SLOTS-HELD", "all 3 live slots are held (a, b, c)",
                                    stage="acquire")
        rendered = refusal.render()
        self.assertIn("[acquire]", rendered)
        self.assertIn("ALL-SLOTS-HELD", rendered)
        self.assertIn("(a, b, c)", rendered)
        # the remedy travels with the refusal: a reader who kept only the message still knows what
        # to do, which is what the PowerShell original's thrown message did.
        self.assertIn("wait for one to release", rendered)
        self.assertEqual(refusal.exit_code, live_slot.EXIT_REFUSED)

    def test_the_exit_codes_preserve_the_powershell_originals_four(self):
        # live-slot.ps1:244 exits 4 for a clone that finished and failed verification, and a
        # caller that polls for it must keep working across the port.
        self.assertEqual(live_slot.EXIT_CLONE_BROKEN, 4)
        self.assertEqual(live_slot.EXIT_REFUSED, 1)   # an unhandled throw in the original
        self.assertEqual(live_slot.EXIT_OK, 0)
        self.assertEqual(live_slot.EXIT_POOL_FAULT, 2)


class RegistryEncodingTests(unittest.TestCase):
    def test_the_registry_is_written_without_a_bom_so_the_plain_utf8_reader_still_works(self):
        # gk-fusion/scripts/prove-slot-connection.py:212 reads slots.json with a plain `utf-8`; a BOM would
        # raise JSONDecodeError there during the window where both implementations coexist.
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "slots.json"
            registry = {"maxSlots": 3, "slots": [], "staleBreaks": []}
            live_slot.write_registry(path, registry, live_slot.Logger())
            self.assertFalse(path.read_bytes().startswith(b"\xef\xbb\xbf"))
            self.assertEqual(json.loads(path.read_text(encoding="utf-8"))["maxSlots"], 3)

    def test_a_registry_a_powershell_51_host_left_with_a_bom_is_still_readable(self):
        # live-slot.ps1:95 uses Set-Content -Encoding utf8, which emits a BOM under Windows
        # PowerShell 5.1 and not under PowerShell 7. Reading utf-8-sig decodes both.
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "slots.json"
            path.write_text('{"maxSlots": 3, "slots": [{"slot": 1}], "staleBreaks": []}',
                            encoding="utf-8-sig")
            self.assertTrue(path.read_bytes().startswith(b"\xef\xbb\xbf"))
            self.assertEqual(live_slot.read_registry(path, 3)["slots"][0]["slot"], 1)

    def test_a_registry_that_is_not_json_refuses_by_name(self):
        with tempfile.TemporaryDirectory() as temp:
            path = Path(temp) / "slots.json"
            path.write_text("{not json", encoding="utf-8")
            with self.assertRaises(live_slot.Refusal) as caught:
                live_slot.read_registry(path, 3)
            self.assertEqual(caught.exception.name, "REGISTRY-UNREADABLE")


class PlainRmtreeTests(unittest.TestCase):
    """The HOST-INDEPENDENT falsifier for defect 2.

    The original's own `Remove-Item -Recurse -Force` fails only under Windows PowerShell 5.1
    (measured: `Win32Exception: The system cannot find the file specified`, exit 1); under
    PowerShell 7 the same call happens to succeed on a single trailing-space directory. Python's
    `shutil.rmtree` fails on BOTH, every time, which is why this is the assertion that pins the
    defect without depending on which PowerShell is installed.
    """

    def test_shutil_rmtree_alone_cannot_delete_a_trailing_space_directory(self):
        with tempfile.TemporaryDirectory() as temp:
            tree = Path(temp) / "victim"
            (tree / "Mods").mkdir(parents=True)
            os.mkdir(extended(tree / BAD_DIR_NAME))
            with self.assertRaises(OSError) as caught:
                shutil.rmtree(tree)
            self.assertEqual(caught.exception.winerror, 145,
                             f"expected WinError 145 (directory not empty), got {caught.exception}")
            # and the plain-path view really cannot see the name, which is the whole cause
            self.assertFalse((tree / BAD_DIR_NAME).exists())
            self.assertIn(BAD_DIR_NAME, os.listdir(extended(tree)))
            # cleanup through the one measured remedy, asserted
            self.assertLess(mirror_empty_over(tree), 8)
            shutil.rmtree(tree)
            self.assertFalse(tree.exists())


def assert_clone_names_its_own_port(runner, install_of_slot, cfg_url_of_slot, owner_url: str = OWNER_URL) -> None:
    """THE INVARIANT, as a bare assertion, so both implementations can be run through it.

    A slot's game must not dial the owner's server. A clone from a source whose cfg names `:5088`
    must therefore come out naming the slot's OWN port, and an acquire must not leave `:5088`
    behind. `runner` is "run the clone+acquire sequence" and is the only thing that differs
    between the pre-fix implementation and this one, so the falsification is a swap, not a
    re-statement of the same expectation in two styles.
    """
    clone = runner("clone", "1")
    if clone.returncode != 0:
        raise AssertionError(f"the clone failed: {clone.stdout[-400:]}{clone.stderr[-400:]}")
    url = cfg_url_of_slot(1)
    if url == owner_url:
        raise AssertionError(
            f"the clone INHERITED the owner's server URL ({url}); the Injector reads FUSIONRPG_SERVER_URL, "
            f"else ServerUrl= in this cfg, else :5088 with a warning, and deploy-play.py only rewrites "
            f"the cfg at stage 9 of 12 -- so any earlier failure leaves the game's game on the owner's server")
    if url != install_of_slot(1):
        raise AssertionError(f"slot 1's cfg names {url}, not its own port {install_of_slot(1)}")
    acquire = runner("acquire", "probe-1")
    if acquire.returncode != 0:
        raise AssertionError(f"the acquire failed: {acquire.stdout[-400:]}{acquire.stderr[-400:]}")
    if "YOUR SERVER PORT: 5101" not in acquire.stdout:
        raise AssertionError("the acquire did not report the slot's own port")
    after = cfg_url_of_slot(1)
    if after == owner_url:
        raise AssertionError("the acquire left the clone pointing at the owner's server")


def assert_force_reclone_survives_a_trailing_space_directory(runner, install_of_slot,
                                                             pollute) -> None:
    """THE INVARIANT for defect 2: a --force re-clone must be able to remove the install."""
    first = runner("clone", "1")
    if first.returncode != 0:
        raise AssertionError(f"the first clone failed: {first.stderr[-400:]}")
    pollute(1)
    again = runner("clone-force", "1")
    if again.returncode != 0:
        raise AssertionError(
            f"a --force re-clone could not delete an install carrying a trailing-space directory "
            f"name (exit {again.returncode}); the slot is now permanently broken: "
            f"{again.stdout[-400:]}{again.stderr[-400:]}")
    install = install_of_slot(1)
    if install.exists() and BAD_DIR_NAME in os.listdir(extended(install)):
        raise AssertionError("the undeletable directory name is still there after a --force re-clone")


class ForceRecloneTests(unittest.TestCase):
    """Defect 2, end to end, against the port."""

    def test_a_force_reclone_succeeds_against_a_tree_with_a_trailing_space_directory(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        pool.pollute(1)
        result = pool.clone("--slot", "1", "--force", session="recloner")
        self.assertEqual(result.returncode, 0,
                         f"a --force re-clone must survive a trailing-space directory name; "
                         f"stderr: {result.stderr[-600:]}")
        self.assertNotIn(BAD_DIR_NAME, os.listdir(extended(pool.pool / "slot-1")))
        entry = pool.registry()["slots"][0]
        self.assertEqual(entry["state"], "ready")
        self.assertEqual(entry["notes"], [])

    def test_the_force_reclone_reports_which_remedy_it_used(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        pool.pollute(1)
        result = pool.clone("--slot", "1", "--force", "--json", session="recloner")
        verdict = json_verdict(result)
        self.assertEqual(verdict["deleteRemedy"], "rmtree+robocopy-mirror")
        self.assertIn("mirroring an empty directory", result.stdout)

    def test_a_normal_tree_is_deleted_by_rmtree_alone_and_says_so(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        result = pool.clone("--slot", "1", "--force", "--json", session="recloner")
        self.assertEqual(json_verdict(result)["deleteRemedy"], "rmtree",
                         "the fast path must stay the fast path; the mirror is the fallback")

    def test_the_invariant_holds_for_this_implementation(self):
        pool = TempPool(self)

        def runner(verb, slot):
            if verb == "clone":
                return pool.clone("--slot", str(slot))
            if verb == "clone-force":
                return pool.clone("--slot", str(slot), "--force", session="recloner")
            return pool.acquire("probe-1", "--base-port", "5100")

        assert_force_reclone_survives_a_trailing_space_directory(
            runner, lambda n: pool.pool / f"slot-{n}", pool.pollute)


class DefectOneCloneTests(unittest.TestCase):
    """Defect 1, end to end, against the port."""

    def test_a_clone_from_a_source_naming_a_different_port_names_its_own_port(self):
        pool = TempPool(self, owner_url="http://127.0.0.1:5088")
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        self.assertEqual(pool.cfg_server_url(1), "http://127.0.0.1:5101")
        self.assertNotEqual(pool.cfg_server_url(1), OWNER_URL)

    def test_every_cloned_slot_gets_its_own_port_not_the_owners(self):
        pool = TempPool(self, owner_url=OWNER_URL)
        for slot in (1, 2, 3):
            self.assertEqual(pool.clone("--slot", str(slot)).returncode, 0)
        self.assertEqual([pool.cfg_server_url(n) for n in (1, 2, 3)],
                         [f"http://127.0.0.1:{5100 + n}" for n in (1, 2, 3)])

    def test_a_base_port_that_would_collide_is_still_honoured(self):
        pool = TempPool(self, owner_url=OWNER_URL)
        self.assertEqual(pool.clone("--slot", "2", "--base-port", "5200").returncode, 0)
        self.assertEqual(pool.cfg_server_url(2), "http://127.0.0.1:5202")

    def test_acquire_restamps_a_cfg_that_was_edited_back_to_the_owners_port(self):
        pool = TempPool(self, owner_url=OWNER_URL)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        pool.slot_cfg(1).write_text(f"ServerUrl={OWNER_URL}\n", encoding="utf-8")
        result = pool.acquire("probe-1")
        self.assertEqual(result.returncode, 0, result.stderr[-600:])
        self.assertEqual(pool.cfg_server_url(1), "http://127.0.0.1:5101")
        self.assertIn("5088", result.stdout, "the value it replaced must be reported")

    def test_a_cfg_with_no_server_url_key_refuses_the_clone_and_records_it_as_broken(self):
        pool = TempPool(self, owner_url=None)
        cfg = pool.source / "Mods" / "fusionrpg.cfg"
        cfg.write_text("# no key here\nPersistCheats=false\n", encoding="utf-8")
        result = pool.clone("--slot", "1")
        self.assertEqual(result.returncode, live_slot.EXIT_REFUSED, result.stderr[-600:])
        self.assertIn("CFG-NO-SERVERURL", result.stderr)
        # the diagnosis is persisted: a clone claims nothing, so recording it costs no slot
        entry = pool.registry()["slots"][0]
        self.assertEqual(entry["state"], "broken")
        self.assertIn("CFG-NO-SERVERURL", " ".join(entry["notes"]))

    def test_a_cfg_with_no_server_url_key_refuses_the_acquire_and_costs_no_claim(self):
        pool = TempPool(self, owner_url=None)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        cfg = pool.slot_cfg(1)
        cfg.write_text("# no key here\n", encoding="utf-8")
        result = pool.acquire("probe-1")
        self.assertEqual(result.returncode, live_slot.EXIT_REFUSED, result.stderr[-600:])
        self.assertIn("CFG-NO-SERVERURL", result.stderr)
        self.assertFalse(any(e.get("session") == "probe-1" for e in pool.registry()["slots"]),
                         "a refusal must never cost a slot (the rule prove-slot-connection.py:389 "
                         "was written for)")

    def test_the_invariant_holds_for_this_implementation(self):
        pool = TempPool(self, owner_url=OWNER_URL)

        def runner(verb, slot):
            if verb == "clone":
                return pool.clone("--slot", str(slot))
            return pool.acquire("probe-1", "--base-port", "5100")

        assert_clone_names_its_own_port(
            runner, lambda n: f"http://127.0.0.1:{5100 + n}", pool.cfg_server_url)


class CloneVerbTests(unittest.TestCase):
    def test_a_verified_clone_is_ready_and_carries_no_stored_port(self):
        pool = TempPool(self)
        result = pool.clone("--slot", "1", "--json")
        self.assertEqual(result.returncode, 0, result.stderr[-600:])
        entry = pool.registry()["slots"][0]
        self.assertEqual(entry["state"], "ready")
        # the port is DERIVED until a claim stores it -- the rule the original also follows
        self.assertNotIn("port", entry)
        self.assertIn("READY", result.stdout)
        self.assertIn("cloner=test-session", result.stdout)

    def test_a_clone_missing_an_entry_is_broken_and_exits_four(self):
        pool = TempPool(self)
        (pool.source / "MelonLoader" / "keep.txt").unlink()
        (pool.source / "MelonLoader").rmdir()
        result = pool.clone("--slot", "1")
        self.assertEqual(result.returncode, live_slot.EXIT_CLONE_BROKEN, result.stdout[-600:])
        self.assertIn("BROKEN -- missing: MelonLoader", result.stdout)
        entry = pool.registry()["slots"][0]
        self.assertEqual(entry["state"], "broken")
        self.assertEqual(entry["notes"], ["missing: MelonLoader"])

    def test_cloning_without_a_session_refuses(self):
        pool = TempPool(self)
        result = pool.live_slot("--clone", "--pool-root", str(pool.pool),
                                "--source-install", str(pool.source))
        self.assertEqual(result.returncode, live_slot.EXIT_REFUSED)
        self.assertIn("SESSION-REQUIRED", result.stderr)

    def test_a_missing_source_install_refuses_by_name(self):
        pool = TempPool(self)
        result = pool.live_slot("--clone", "--session", "s", "--pool-root", str(pool.pool),
                                "--source-install", str(pool.root / "not-there"))
        self.assertEqual(result.returncode, live_slot.EXIT_REFUSED)
        self.assertIn("SOURCE-INSTALL-NOT-FOUND", result.stderr)

    def test_cloning_with_no_source_install_refuses_by_name(self):
        pool = TempPool(self)
        result = pool.live_slot("--clone", "--session", "s", "--pool-root", str(pool.pool),
                                "--source-install", "")
        self.assertEqual(result.returncode, live_slot.EXIT_REFUSED)
        self.assertIn("SOURCE-INSTALL-MISSING", result.stderr)

    def test_cloning_over_an_occupied_slot_is_refused_and_names_the_holder(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        self.assertEqual(pool.acquire("holder-1", "--base-port", "5100").returncode, 0)
        result = pool.clone("--slot", "1")
        self.assertEqual(result.returncode, live_slot.EXIT_REFUSED)
        self.assertIn("SLOT-OCCUPIED", result.stderr)
        self.assertIn("holder-1", result.stderr)
        # ...unless forced, which is the documented escape
        self.assertEqual(pool.clone("--slot", "1", "--force", session="recloner").returncode, 0)

    def test_cloning_with_no_slot_and_no_free_number_refuses_by_name(self):
        pool = TempPool(self)
        for number in (1, 2, 3):
            self.assertEqual(pool.clone("--slot", str(number)).returncode, 0)
        result = pool.clone()
        self.assertEqual(result.returncode, live_slot.EXIT_REFUSED)
        self.assertIn("ALL-SLOTS-EXIST", result.stderr)

    def test_the_verb_picks_the_lowest_uncloned_slot(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone().returncode, 0)
        self.assertEqual(pool.clone().returncode, 0)
        self.assertEqual(sorted(e["slot"] for e in pool.registry()["slots"]), [1, 2])


class AcquireVerbTests(unittest.TestCase):
    def test_acquire_stores_the_derived_port_and_reports_it(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        result = pool.acquire("probe-1", "--json")
        self.assertEqual(result.returncode, 0, result.stderr[-600:])
        entry = next(e for e in pool.registry()["slots"] if e.get("session") == "probe-1")
        self.assertEqual(entry["state"], "occupied")
        self.assertEqual(entry["port"], 5101)
        self.assertIn("YOUR SERVER PORT: 5101", result.stdout)
        self.assertEqual(json_verdict(result)["serverUrl"]["current"], "http://127.0.0.1:5101")

    def test_acquire_clones_on_first_use(self):
        pool = TempPool(self)
        result = pool.acquire("probe-1", "--json")
        self.assertEqual(result.returncode, 0, result.stderr[-600:])
        self.assertEqual(json_verdict(result)["slot"], 1)
        self.assertEqual(pool.cfg_server_url(1), "http://127.0.0.1:5101")

    def test_acquire_is_a_no_op_when_the_session_already_holds_a_slot(self):
        pool = TempPool(self)
        self.assertEqual(pool.acquire("probe-1").returncode, 0)
        again = pool.acquire("probe-1", "--json")
        self.assertEqual(again.returncode, 0)
        self.assertTrue(json_verdict(again)["alreadyHeld"])
        self.assertIn("already holds slot 1", again.stdout)

    def test_acquire_refuses_when_every_slot_is_held_and_names_the_holders(self):
        pool = TempPool(self)
        for number, holder in ((1, "a"), (2, "b"), (3, "c")):
            self.assertEqual(pool.clone("--slot", str(number)).returncode, 0)
        for number, holder in ((1, "a"), (2, "b"), (3, "c")):
            result = pool.live_slot("--acquire", "--session", holder, "--pool-root", str(pool.pool),
                                    "--base-port", "5100")
            self.assertEqual(result.returncode, 0, result.stderr[-400:])
        refused = pool.acquire("d")
        self.assertEqual(refused.returncode, live_slot.EXIT_REFUSED)
        self.assertIn("ALL-SLOTS-HELD", refused.stderr)
        for holder in ("a", "b", "c"):
            self.assertIn(holder, refused.stderr)
        self.assertIn("wait for one to release", refused.stderr)

    def test_acquire_prefers_an_existing_ready_slot_over_cloning(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        self.assertEqual(pool.clone("--slot", "2").returncode, 0)
        result = pool.acquire("probe-1", "--json")
        self.assertEqual(json_verdict(result)["slot"], 1)
        self.assertIsNone(json_verdict(result)["cloneMethod"])

    def test_acquire_ignores_a_slot_flag_but_says_so(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        result = pool.acquire("probe-1", "--slot", "3", "--json")
        self.assertEqual(result.returncode, 0, result.stderr[-400:])
        # the pick is the original's: the lowest ready slot, not the one asked for. That rule is
        # load-bearing for gk-fusion/scripts/prove-slot-connection.py:452, so it is preserved -- but a
        # silently ignored flag is not, so the call is told.
        self.assertEqual(json_verdict(result)["slot"], 1)
        self.assertIn("ignores --slot 3", result.stdout)
        # a second session is told the same, and still gets its own pick rather than slot 3
        second = pool.acquire("probe-2", "--slot", "3", "--json")
        self.assertEqual(json_verdict(second)["slot"], 2)

    def test_a_broken_slot_on_acquire_is_refused_by_name(self):
        # The reachable path, which is also the only one the PowerShell original has: acquire
        # prefers a READY slot and otherwise takes the first uncloned number, so a broken entry is
        # only picked when that slot's install is on disk and fails verification right there.
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        shutil.rmtree(pool.pool / "slot-1" / "MelonLoader")
        (pool.pool / "slots.json").unlink()  # the pool has no entries: slot 1 is the first number
        result = pool.acquire("probe-1")
        self.assertEqual(result.returncode, live_slot.EXIT_REFUSED, result.stderr[-600:])
        self.assertIn("SLOT-BROKEN", result.stderr)
        self.assertIn("missing: MelonLoader", result.stderr)
        self.assertIn("--clone --slot 1 --force", result.stderr)
        self.assertFalse((pool.pool / "slots.lock").exists())

    def test_a_slot_resolving_to_the_owner_port_is_flagged(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "1", "--base-port", "5087").returncode, 0)
        result = pool.acquire("probe-1", "--base-port", "5087")
        self.assertEqual(result.returncode, 0, result.stderr[-400:])
        self.assertIn("OWNER's port", result.stdout)


class ReleaseAndReclaimTests(unittest.TestCase):
    def test_release_returns_the_slot_to_ready_and_keeps_the_install(self):
        pool = TempPool(self)
        self.assertEqual(pool.acquire("probe-1").returncode, 0)
        result = pool.release("probe-1")
        self.assertEqual(result.returncode, 0, result.stderr[-400:])
        entry = pool.registry()["slots"][0]
        self.assertEqual(entry["state"], "ready")
        self.assertIsNone(entry["session"])
        self.assertTrue((pool.pool / "slot-1").is_dir(), "release must not pay the clone again")
        self.assertIn("RELEASED 1 slot(s)", result.stdout)

    def test_releasing_nothing_says_so_and_exits_clean(self):
        pool = TempPool(self)
        result = pool.release("nobody")
        self.assertEqual(result.returncode, 0)
        self.assertIn("holds no slot", result.stdout)

    def test_reclaim_without_a_slot_refuses(self):
        pool = TempPool(self)
        result = pool.live_slot("--reclaim", "--session", "manager", "--pool-root", str(pool.pool))
        self.assertEqual(result.returncode, live_slot.EXIT_REFUSED)
        self.assertIn("SLOT-REQUIRED", result.stderr)

    def test_reclaiming_an_unknown_slot_refuses_by_name(self):
        pool = TempPool(self)
        result = pool.live_slot("--reclaim", "--slot", "2", "--session", "manager",
                                "--pool-root", str(pool.pool))
        self.assertEqual(result.returncode, live_slot.EXIT_REFUSED)
        self.assertIn("SLOT-UNKNOWN", result.stderr)

    def test_reclaim_frees_a_stale_claim_and_records_it_in_the_notes(self):
        pool = TempPool(self)
        self.assertEqual(pool.acquire("gone").returncode, 0)
        result = pool.live_slot("--reclaim", "--slot", "1", "--session", "manager",
                                "--pool-root", str(pool.pool))
        self.assertEqual(result.returncode, 0, result.stderr[-400:])
        entry = pool.registry()["slots"][0]
        self.assertEqual(entry["state"], "ready")
        self.assertIsNone(entry["session"])
        self.assertTrue(any("reclaimed from 'gone'" in note for note in entry["notes"]))
        self.assertIn("was held by 'gone'", result.stdout)


class StatusTests(unittest.TestCase):
    def test_status_reports_every_slot_including_the_free_ones(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        result = pool.status("--json")
        self.assertEqual(result.returncode, 0, result.stderr[-600:])
        verdict = json_verdict(result)
        self.assertEqual([row["state"] for row in verdict["slots"]], ["ready", "free", "free"])
        self.assertIn("ready-to-claim (cloned + verified): 1", result.stdout)
        self.assertIn("(nothing cloned yet)", result.stdout)

    def test_status_derives_the_port_for_a_slot_that_was_never_claimed(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "2").returncode, 0)
        verdict = json_verdict(pool.status("--json"))
        by_slot = {row["slot"]: row["port"] for row in verdict["slots"]}
        # both halves of the stored-or-derived rule, in one report
        self.assertEqual(by_slot, {1: 5101, 2: 5102, 3: 5103})

    def test_status_uses_the_stored_port_when_a_claim_recorded_one(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        self.assertEqual(pool.acquire("probe-1", "--base-port", "5300").returncode, 0)
        verdict = json_verdict(pool.status("--json"))
        self.assertEqual(verdict["slots"][0]["port"], 5301)

    def test_status_flags_a_stale_claim_held_past_the_threshold_with_no_process(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        self.assertEqual(pool.acquire("probe-1").returncode, 0)
        registry = pool.registry()
        registry["slots"][0]["acquiredAt"] = "2020-01-01T00:00:00+00:00"
        (pool.pool / "slots.json").write_text(json.dumps(registry), encoding="utf-8")
        result = pool.status("--stale-claim-minutes", "1")
        self.assertIn("STALE CLAIM", result.stdout)
        self.assertIn("--reclaim --slot 1", result.stdout)
        self.assertTrue(
            json_verdict(pool.status("--stale-claim-minutes", "1", "--json"))["slots"][0]["staleClaim"])

    def test_status_explains_a_broken_slot_with_the_remedy(self):
        pool = TempPool(self)
        (pool.source / "GameAssembly.dll").unlink()
        self.assertEqual(pool.clone("--slot", "1").returncode, live_slot.EXIT_CLONE_BROKEN)
        result = pool.status()
        self.assertIn("BROKEN: missing: GameAssembly.dll", result.stdout)
        self.assertIn("--clone --slot 1 --force", result.stdout)

    def test_status_needs_no_session_and_takes_no_lock(self):
        pool = TempPool(self)
        result = pool.status()
        self.assertEqual(result.returncode, 0)
        self.assertFalse((pool.pool / "slots.lock").exists())


class LockTests(unittest.TestCase):
    def test_the_lock_is_taken_for_the_write_and_released_afterwards(self):
        pool = TempPool(self)
        self.assertEqual(pool.clone("--slot", "1").returncode, 0)
        self.assertFalse((pool.pool / "slots.lock").exists(),
                         "the lock is held for the read-modify-write, never for a whole probe")

    def test_a_stale_lock_is_broken_and_the_break_is_recorded(self):
        pool = TempPool(self)
        pool.pool.mkdir(parents=True, exist_ok=True)
        lock = pool.pool / "slots.lock"
        lock.write_text("crashed-holder", encoding="utf-8")
        old = time.time() - 3600
        os.utime(lock, (old, old))
        result = pool.clone("--slot", "1")
        self.assertEqual(result.returncode, 0, result.stderr[-600:])
        self.assertIn("broke a stale lock", result.stdout)
        self.assertEqual(len(pool.registry()["staleBreaks"]), 1)
        self.assertEqual(pool.registry()["staleBreaks"][0]["by"], "test-session")
        self.assertFalse(lock.exists())

    def test_a_fresh_lock_makes_the_caller_wait_and_then_refuse_by_name(self):
        pool = TempPool(self)
        pool.pool.mkdir(parents=True, exist_ok=True)
        (pool.pool / "slots.lock").write_text("held", encoding="utf-8")
        result = pool.clone("--slot", "1", "--lock-timeout-seconds", "1")
        self.assertEqual(result.returncode, live_slot.EXIT_REFUSED)
        self.assertIn("LOCK-UNAVAILABLE", result.stderr)
        # the holder's lock is never stolen
        self.assertTrue((pool.pool / "slots.lock").exists())


class CliShapeTests(unittest.TestCase):
    def test_every_flag_answers_to_both_spellings(self):
        # The one live in-repo caller (gk-fusion/scripts/prove-slot-connection.py:456) passes the
        # PowerShell spelling, so re-pointing it is a one-token change: the program name.
        pool = TempPool(self)
        result = pool.run("-Status", "-PoolRoot", str(pool.pool))
        self.assertEqual(result.returncode, 0, result.stderr[-400:])
        result = pool.live_slot("-Clone", "-Session", "s", "-PoolRoot", str(pool.pool),
                                "-SourceInstall", str(pool.source), "-Slot", "1", "-Force")
        self.assertEqual(result.returncode, 0, result.stderr[-400:])
        self.assertEqual(pool.registry()["slots"][0]["state"], "ready")

    def test_no_verb_prints_usage_and_refuses(self):
        result = subprocess.run([sys.executable, str(TOOL)], capture_output=True, text=True,
                                timeout=120, cwd=str(REPO), check=False)
        self.assertEqual(result.returncode, live_slot.EXIT_REFUSED)
        self.assertIn("pick exactly one verb", result.stdout)

    def test_two_verbs_are_refused_rather_than_silently_resolved(self):
        pool = TempPool(self)
        result = pool.live_slot("--status", "--release", "--session", "s",
                                "--pool-root", str(pool.pool))
        self.assertEqual(result.returncode, live_slot.EXIT_REFUSED)
        self.assertIn("pick exactly one verb", result.stdout)

    def test_the_help_names_the_powershell_spelling_as_the_compatibility_alias(self):
        result = subprocess.run([sys.executable, str(TOOL), "--help"], capture_output=True,
                                text=True, timeout=120, cwd=str(REPO), check=False)
        self.assertEqual(result.returncode, 0)
        for flag in ("-Status", "-Clone", "-Acquire", "-Release", "-Reclaim", "-Session",
                     "-Slot", "-PoolRoot", "-SourceInstall", "-MaxSlots", "-BasePort",
                     "-LockTimeoutSeconds", "-StaleLockMinutes", "-StaleClaimMinutes", "-Force"):
            self.assertIn(flag, result.stdout, f"{flag} disappeared from the CLI")


if __name__ == "__main__":
    unittest.main()

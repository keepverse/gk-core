#!/usr/bin/env python3
"""Wrapper around: dotnet run --project gk-forge/tools/AtomImporter -c Release -- --check --validate --db "__DB__"

Runs the content validation gate (lint, power drift, atom validation).

Replaces `scripts/checks/gen-content-validate.ps1`. WHY THAT FORM WAS RETIRED is stated once, in
`gk-core/scripts/checks/common.py`, and the two facts that matter HERE are:

  * the command and the working directory are DECLARED, not inferred from line order. The .ps1's
    real command was "the line before the last `if ($LASTEXITCODE -ne 0) { throw `", a positional
    convention nothing enforced, so reordering a line silently made this wrapper unparseable - and
    the parity test reported a parity failure rather than a parse failure.
  * a missing toolchain and a failed check are different events with different exit codes. The .ps1
    threw for both, so a caller could not tell "the check found something" from "the check could not
    run", and they have different fixes.

Not a guard (`docs/architecture/ps1-ban-map.md` §3.4): this is an argument-free convenience wrapper
around the command a `ci.yml` step already runs, from the same directory. Its parity with CI is the
property worth protecting.
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import common  # noqa: E402

SUMMARY = 'the content validation gate (lint, power drift, atom validation)'
CHECK = ('dotnet', 'run', '--project', 'tools/AtomImporter', '-c', 'Release', '--', '--check', '--validate', '--db', '__DB__')
PREFLIGHT = ('dotnet',)
WORKING_DIRECTORY = '.'
FAIL_HINT = 'content validation gate failed - see the lint / power drift lines above'

SPEC = common.spec_from(sys.modules[__name__])


def _command_with_a_scratch_db() -> tuple:
    """Hand the command a scratch database directory that really exists.

    The .ps1 created a temp directory, passed `--db "$dbDir"` and removed it afterwards. CI passes
    `$env:RUNNER_TEMP/atom-validate-db`, which resolves to nothing on a developer machine, so the
    wrapper made its own. `common.execute` takes an optional command override for exactly this: it
    is the one wrapper of the fifteen that does more than run a command.
    """
    import atexit
    import shutil
    import tempfile

    directory = tempfile.mkdtemp(prefix="atom-validate-db-")
    atexit.register(shutil.rmtree, directory, True)
    return tuple(directory if token == SCRATCH_TOKEN else token for token in SPEC["check"])


SCRATCH_TOKEN = "__DB__"

if __name__ == "__main__":
    sys.exit(common.main(SPEC, command=_command_with_a_scratch_db()))


#!/usr/bin/env python3
"""Wrapper around: python -m seedsmith.adapters.creatures.fusion.reconcile --check

Runs the fusion-recipe seed reconciliation check.

Replaces `scripts/checks/gen-fusion-recipe.ps1`. WHY THAT FORM WAS RETIRED is stated once, in
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

SUMMARY = 'the fusion-recipe seed reconciliation check'
CHECK = ('python', '-m', 'seedsmith.adapters.creatures.fusion.reconcile', '--check')
PREFLIGHT = ('python',)
WORKING_DIRECTORY = 'tools/seedsmith'
FAIL_HINT = "fusion-recipe seed is stale — run 'python -m seedsmith.adapters.creatures.fusion.reconcile' (from tools/seedsmith) and commit the result"

SPEC = common.spec_from(sys.modules[__name__])

if __name__ == "__main__":
    sys.exit(common.main(SPEC))


#!/usr/bin/env python3
"""The web tree's own check: vitest, then the build — the two commands AGENTS.md names for it.

Replaces `scripts/checks/web-fusion-rpg-web.ps1`, the last `.ps1` among the seventeen
`gk-core/scripts/checks/*` wrappers. The other sixteen were ported to this shape earlier, so the retirement
reason for the SHARED runner is stated once in `gk-core/scripts/checks/common.py`; the two facts that matter
HERE are:

  * **IT IS A SEQUENCE, AND THE ORDER IS THE POINT.** The original ran `npm test` and then
    `npm run build` and said why: "the order that fails fastest: the vitest suite, then `npm run build`".
    The build is the slower half, and the type check it performs (`tsc --noEmit`) is the half a bare
    vitest run does NOT do, because vitest transpiles without type-checking. So `common.spec_from`
    gained a `CHECKS` sequence alongside `CHECK` — a wrapper must declare exactly ONE of the two,
    because a wrapper declaring both is ambiguous and resolving that silently would pick one at random.

  * **THE MISSING-`node_modules` REFUSAL WAS A THROW WITH NO EXIT CODE OF ITS OWN.** The original
    `throw`ed when `web/fusion-rpg-web/node_modules` was absent, which is a configuration problem with
    a different fix from "the check is red" — and `common.py`'s vocabulary already separates them. It is
    declared as `REQUIRED_PATHS` rather than re-implemented here.

Not a guard (`docs/architecture/ps1-ban-map.md` §3.4): it is a convenience wrapper around the commands a
`ci.yml` step already runs, from the same working directory. Its parity with CI is the property worth
protecting, because a wrapper that drifts from its CI step is a check that exists in two places and is
enforced in one.
"""
from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import common  # noqa: E402

SUMMARY = "the web vitest suite and build (tsc --noEmit + vite build)"

# Declaration order IS execution order, and it is the fail-fastest one. See the module docstring.
CHECKS = (
    ("npm", "test"),
    ("npm", "run", "build"),
)

WORKING_DIRECTORY = "web/fusion-rpg-web"

# `npm ci` has to have run for either command to mean anything, and the original refused with that
# exact instruction rather than letting npm print its own. Declared, so it is a named refusal.
REQUIRED_PATHS = ("web/fusion-rpg-web/node_modules",)

# The preflight is the tool, not the tree: `node_modules` is `REQUIRED_PATHS`, `npm` is this.
PREFLIGHT = ("npm",)

FAIL_HINT = ("the web suite or build is red; run `npm test` then `npm run build` in "
             "web/fusion-rpg-web to see the output")

SPEC = common.spec_from(sys.modules[__name__])

if __name__ == "__main__":
    sys.exit(common.main(SPEC))

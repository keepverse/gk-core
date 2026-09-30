"""WHERE A GUARD'S SUBJECT LIVES, GIVEN THE ROOT IT WAS HANDED.

One rule, shared, because three guards need it identically and a second copy of a rule is how two
of them come to disagree about the same file.

    A root that carries the subject itself is authoritative. Otherwise the owning repository is
    resolved.

Both halves are load-bearing and the second is not a fallback:

- A guard contract test plants a SELF-CONTAINED fixture - its own `data/seed/aptitudes/roster.json`,
  its own catalog - and points `--root` at it. That fixture IS the subject under test; a planted
  violation is the whole point. Reading the real content pack instead of the fixture means the
  guard examines something the test did not write, and every planted-violation test in the class
  fails on a subject it never planted. This is not a test being awkward; it is the test being the
  only thing that can prove a rule fires.

- After the workspace split a root frequently does NOT carry its subject. gk-core holds
  `data/tuning` and no `data/seed`; the roster and the derived-stats catalog are gk-data's, and
  `docs/architecture/power/inventory.json` is gk-workflow's. Resolving only the pack therefore
  breaks the real tree, which is the failure this rule's second half exists to prevent.

The order is root-then-owner, never owner-then-root, and that direction is what makes it safe
rather than convenient. Resolving the owner first would mean a root that happens to carry a stale
copy of the subject is silently ignored - the guard would report on a file the caller did not point
it at. Root-first can only ever find data the caller explicitly named.

A missing subject is a REFUSAL by name in both halves. It is never an empty scan and never a green
verdict: "I cannot see what I guard" and "what I guard is clean" are different sentences, and a
guard that cannot tell them apart reports the first as the second.
"""

from __future__ import annotations

from pathlib import Path

__all__ = ["subject_root"]


def subject_root(root: Path, relative: tuple[str, ...], accessor) -> Path:
    """The root that holds `relative` for this run: `root` if it has it, else the owner.

    `accessor` is one of the `keepverse_roots` accessors, passed in rather than imported so this
    module carries no opinion about WHICH repository owns which subject - that is the caller's
    knowledge, and hard-coding it here would be a second place for the topology to live.

    Raises whatever the accessor raises when the subject is in neither place. Callers are expected
    to convert that into their own named refusal: this module resolves, it does not adjudicate.
    """
    if root.joinpath(*relative).exists():
        return root
    return accessor(root)

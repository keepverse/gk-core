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
    """The root that holds `relative` for this run, and WHY that root.

    The distinction is between a FIXTURE and a REAL TREE, and it is made on a checkable property
    rather than a guess about intent: a real repository is a git working tree, and a planted fixture
    is a temporary directory that is not.

    - A FIXTURE carries its own subject, and that subject is what is under test. A guard contract test
      plants its own roster, catalog or inventory and points `--root` at it, because a planted
      violation is the only mechanism by which a rule is proven to fire. The fixture wins.
    - A REAL TREE does not get to override the owner. If the root is a git repository and the
      subject is missing from it, the subject is not there, and the owning repository holds it.

    Without the second half, precedence is a hole rather than a rule: a caller who plants a
    three-line `inventory.json` beside a real tree and passes that directory as `--root` silences a
    guard that would otherwise report a finding. Measured, that is what happened - a planted file
    turned the power guard from a G3 finding into `exit 0`. A guard that can be silenced by planting
    a file in the place it is supposed to be checking is not a guard, and "the root wins" is only
    safe while the root is somewhere other than the thing under test.

    Root-then-owner for a fixture, owner-then-root for a real tree, decided by `is_git_tree`. It is
    not a compromise between the two readings; each one is the correct answer for its own case.

    Raises whatever the accessor raises when the subject is in neither place. Callers convert that
    into their own named refusal: this module resolves, it does not adjudicate.
    """
    if not is_git_tree(root):
        # A FIXTURE IS THE WORLD. It owns the subject whether or not the subject is there.
        #
        # The `and root.joinpath(*relative).exists()` that used to sit here sent a fixture which
        # DELIBERATELY omitted the subject to the accessor, and a temporary directory has no
        # workspace above it - so the accessor raised and the caller reported WORKSPACE-ROOT-MISSING
        # when the honest verdict was INVENTORY-MISSING. A test that plants a fixture without an
        # inventory is testing exactly that refusal, and it was told the workspace could not be found
        # instead. Four tests in test_guard_power.py read it that way.
        #
        # Nothing is weakened by dropping the existence test. A real tree - the only case where
        # precedence could be abused to silence a guard - still goes to the accessor, so planting a
        # file beside a real repository still cannot override the owner. The anti-silencing property
        # was never about fixtures; a fixture is the thing under test, and it is supposed to be able
        # to say "this document is absent".
        return root
    return accessor(root)    # a REAL TREE resolves the owner, and never overrides it


def is_git_tree(root: Path) -> bool:
    """Is this a real repository working tree rather than a planted fixture?

    `.git` is a directory in a normal clone and a FILE in a worktree or a submodule, so both are
    accepted. A fixture is a temporary directory with neither.
    """
    git = root / ".git"
    return git.is_dir() or git.is_file()

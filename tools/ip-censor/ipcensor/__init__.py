"""ip-censor — IP detection and reporting over the tracked tree.

An independent tool, deliberately shaped like `gk-forge/tools/seedsmith` (its own package, its own
exact-pinned lockfile, its own `tests/`, `python -m ipcensor.report <verb>`) and deliberately
independent of it: the two share conventions, never code (spec-wiring.md §Tool shape).
"""

__version__ = "1.0.0"

"""Attach a suggested censor token to each finding.

Two rules are the whole module:

1. **The authored map wins.** A pair the owner authored in `replacements.v1.json` is used verbatim;
   a model never overrides it.
2. **A model proposal is a proposal.** Every generated suggestion carries `PROPOSAL_MARKER` and is
   never applied without a person's decision.

`propose=None` is the authored-only mode: no model, no network, fully deterministic. That is the mode
CI and the release gate use.
"""

from __future__ import annotations

from dataclasses import dataclass
from typing import Callable, Iterable, Literal

from ipcensor.registry import Registry
from ipcensor.scan import Finding

# Structural literal, not tunable: a contract string a human and a future execute plan both parse.
PROPOSAL_MARKER = "proposed — needs owner confirm"

Source = Literal["authored", "proposed", "none"]

# Closed vocabulary: a new member is a reviewed decision, and the marker string is a contract both a
# human and a future execute plan parse.
SOURCES: frozenset[str] = frozenset({"authored", "proposed", "none"})

SuggestFn = Callable[[Finding], str | None]


@dataclass(frozen=True, slots=True)
class Suggestion:
    """What to replace one mark with, and on whose authority."""

    mark: str
    replacement: str | None
    source: str
    note: str | None
    remediation: str

    def as_dict(self) -> dict[str, object]:
        return {
            "mark": self.mark,
            "replacement": self.replacement,
            "source": self.source,
            "note": self.note,
            "remediation": self.remediation,
        }


def suggest(
    findings: Iterable[Finding],
    registry: Registry,
    propose: SuggestFn | None = None,
) -> tuple[Suggestion, ...]:
    """One suggestion per finding: authored pair first, `propose` for the remainder.

    `remediation` is carried through unchanged — `suggest` never downgrades a `generator-owned`
    finding to a direct edit, because that is how an execute program comes to hand-edit generated data.
    """
    authored = {key.casefold(): value for key, value in registry.replacements.items()}
    proposals: dict[str, tuple[str | None, str | None]] = {}
    out: list[Suggestion] = []

    for finding in findings:
        if finding.bucket == "code-identifier" or finding.remediation == "code-change":
            # Renaming a code namespace is a code change, not a content edit; a proposed string there
            # invites a damaging blind replace.
            out.append(_none(finding, None))
            continue

        pair = _authored_for(authored, finding)
        if pair is not None:
            out.append(Suggestion(finding.mark, pair, "authored", None, finding.remediation))
            continue

        if propose is None:
            out.append(_none(finding, None))
            continue

        if finding.mark not in proposals:
            # Once per distinct mark, not once per hit: a mark that occurs 400 times is one proposal.
            try:
                proposals[finding.mark] = (propose(finding), None)
            except Exception as exc:  # noqa: BLE001 - a proposer failure is recorded, never fatal
                proposals[finding.mark] = (None, f"proposer failed: {exc!r}")
        replacement, error = proposals[finding.mark]
        if replacement:
            out.append(
                Suggestion(finding.mark, replacement, "proposed", PROPOSAL_MARKER, finding.remediation)
            )
        else:
            out.append(_none(finding, error or "proposer returned no proposal"))

    return tuple(out)


def _authored_for(authored: dict[str, str], finding: Finding) -> str | None:
    # The map is authored per spelling as well as per mark: IC-1b names both `pvz` and
    # `Plants vs. Zombies`, so the exact matched text is tried first.
    for key in (finding.matched.casefold(), finding.mark.casefold()):
        if key in authored:
            return authored[key]
    return None


def _none(finding: Finding, note: str | None) -> Suggestion:
    return Suggestion(finding.mark, None, "none", note, finding.remediation)

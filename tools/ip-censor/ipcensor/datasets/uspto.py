"""The USPTO adapter — the first register behind the candidate shape.

USPTO's trademark bulk product is public and published for bulk download, so an import is repeatable
without scraping (spec-curate.md §The dataset). The raw export is never committed; the dataset id and
its version are recorded in every candidate's `source`.

**The element map below is pinned but UNVERIFIED against a real export.** This program has not
downloaded one (T21 owns the first real round), so the adapter declares the shape it was written
against and refuses anything else rather than guessing per file. T21 must confirm these names against
the download and correct this map if they differ — a one-line change here, not a redesign, and the
refusal below is what makes the difference visible.
"""

from __future__ import annotations

import xml.etree.ElementTree as ElementTree
from dataclasses import dataclass
from pathlib import Path

DATASET_ID = "uspto"

# The pinned format. Adopting a future export shape means a new constant and a new branch, never a
# loosened check.
FORMAT_ID = "uspto-case-files-xml/v1"
ROOT_ELEMENT = "trademark-case-files"
RECORD_ELEMENT = "case-file"

# See the module docstring: unverified element names, declared once, in one place.
ELEMENT_MAP: dict[str, str] = {
    "serial": "serial-number",
    "mark": "mark-identification",
    "status": "status-code",
    "classes": "international-class",
    "goods": "goods-services-description",
}


class UnknownExportFormat(RuntimeError):
    """The export is not the format this adapter pins. The message names what was found instead."""


@dataclass(frozen=True, slots=True)
class DatasetRecord:
    """One mark from the export, reduced to what the authored filter reads."""

    record_id: str
    mark: str
    status: str
    classes: tuple[str, ...]
    goods: str


def read_export(path: Path | str) -> tuple[DatasetRecord, ...]:
    """Read the pinned USPTO export into records.

    A root element other than `ROOT_ELEMENT` is refused, naming the root it saw, so a changed download
    is a loud rejection rather than an empty candidate file.
    """
    file = Path(path)
    try:
        tree = ElementTree.parse(file)
    except ElementTree.ParseError as exc:
        raise UnknownExportFormat(f"{file}: not valid XML ({exc})") from exc
    except OSError as exc:
        raise UnknownExportFormat(f"{file}: cannot be read ({exc})") from exc

    root = tree.getroot()
    if root.tag != ROOT_ELEMENT:
        raise UnknownExportFormat(
            f"{file}: root element is {root.tag!r}; this adapter pins {FORMAT_ID} "
            f"(root {ROOT_ELEMENT!r}, records {RECORD_ELEMENT!r})"
        )

    return tuple(_record_from(element) for element in root.findall(RECORD_ELEMENT))


def _record_from(element: ElementTree.Element) -> DatasetRecord:
    def text(name: str) -> str:
        found = element.find(f".//{ELEMENT_MAP[name]}")
        return (found.text or "").strip() if found is not None else ""

    classes = tuple(
        sorted(
            {
                (found.text or "").strip()
                for found in element.findall(f".//{ELEMENT_MAP['classes']}")
                if (found.text or "").strip()
            }
        )
    )
    return DatasetRecord(
        record_id=text("serial"),
        mark=text("mark"),
        status=text("status"),
        classes=classes,
        goods=text("goods"),
    )

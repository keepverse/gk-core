#!/usr/bin/env python3
"""
Shared C# source scanner used by the SQL/DAL boundary guards.

Why this module exists
----------------------
Five PowerShell guards each carry their own comment stripper -
`guard-clock-seam.ps1`, `guard-debug-scope.ps1`, `guard-sim-fabrication.ps1`,
`guard-single-writer.py` and `guard-test-substrate.ps1` - and a sixth guard,
`guard-open-identity.ps1`, does the same job inline with a line-prefix test. They have already
drifted: some blank comment lines and keep the array index, some delete them, and the
line-prefix form flags a pattern named in a trailing `//` comment or in a `/* */` body whose
lines do not begin with a `*`. Two guards disagreeing about what "code" means is how a boundary
quietly stops being a boundary. The Python port of `guard-vocabulary-mirror.py` carried a
seventh copy, which was correct; this module is that copy given a name so the next port imports
it instead of re-deriving it.

VERIFIED, not recalled: the five-file list above is what `git grep -E 'function (Strip|Remove)-
Comments|Strip-Comments' -- scripts/**` returns. An earlier draft of this docstring named
`guard-secondary-no-unity.ps1` as one of the three copies; it has no stripper at all and scans
raw text, so the claim was wrong. The file list here is a reading, and a reader who needs it
fresh should re-run that grep rather than trust it.

KNOWN DEBT (deliberate, and it shrinks as guards are ported)
------------------------------------------------------------
`guard-vocabulary-mirror.py` still holds its own private copy, and the remaining PowerShell copies
are untouched. Rather than edit guards that currently work, each port imports from here; the old
copies fold in when their own guard is next touched.

THERE ARE FIVE POLICIES, NOT ONE - and the number is a reading, not a preference
-------------------------------------------------------------------------------
The copies did not drift by accident; they answer different questions, and collapsing them would
change what a guard reports. Each was verified character-for-character against the PowerShell
original before it was added (18-20 probe cases each, including the awkward ones).

  strip_comments                             comments removed, a block becomes ONE space,
                                               literals KEPT verbatim
  strip_comments_and_literals                comments removed, a block becomes ONE space,
                                               literal CONTENTS blanked
  strip_whole_line_comments                  only lines whose first non-space characters are
                                               // * /* are blanked; a trailing comment is scanned
  strip_comments_preserving_layout           every comment character becomes a SPACE, so the text
                                               keeps its length and its LINE COUNT; literals kept
  strip_comments_and_literals_preserving_layout  both at once: literals blanked AND length and
                                               line count kept; understands @"" verbatim strings,
                                               and an unterminated literal stops at the newline

Whether a pattern can match text inside a string is one axis; whether LINE NUMBERS survive is the
other, and the two are independent. `Log("p.theHealth = 0")` separates the string axis. A file with
a large block comment near the top separates the line-number axis, because the collapsing policies
report lines that do not exist in the source and send an operator to the wrong place. A guard that
reports a line wants a layout-preserving policy; a guard whose patterns must not match inside a
literal wants a blanking one; `guard-battle-responsibility` asks for both at once, which is why the fifth
exists rather than the second being stretched.

If a port needs a policy that is not here, ADD one with its own name and its own differential.
Do not widen an existing one: a scanner that quietly gets stronger turns a passing guard red for
reasons nobody can point at, and a scanner that quietly gets weaker stops catching the thing it
was written for.

`strip_comments` PRESERVES string and char literals, and that is deliberate rather than incidental:

  * For a SQL/DAL boundary, a `CREATE TABLE` inside a string literal is a REAL finding - it is raw
    SQL handed to a driver, and comments are the only place a keyword is genuinely inert.
  * So the rule is "scan code and strings, skip comments", not "scan code only". A scanner that
    also ate string literals would let a raw-SQL route through by writing it as a string, which is
    the exact shape the guard exists to catch.

Usage:
    from cscan import (strip_comments, strip_comments_and_literals,
                       strip_comments_preserving_layout, strip_whole_line_comments)
"""
from __future__ import annotations


def strip_comments(text: str) -> str:
    """Remove `//` line comments and `/* ... */` block comments; keep string/char literals intact.

    One pass, left to right, with no regular expression, because a regex cannot tell a `//` inside
    a string literal from a comment - and a guard that gets that wrong is either noisy (it flags
    `"http://x"`) or blind (it strips a real statement). A block comment is replaced by a single
    space so two tokens either side of it cannot be joined into a false match.

    Handles: escaped quotes (`\\"`), a `//` inside a string, a `/*` inside a string, and an
    unterminated block comment (consumed to end of input rather than looping).
    """
    out: list[str] = []
    i, n = 0, len(text)
    while i < n:
        char = text[i]
        # `//` line comment
        if char == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        # `/* ... */` block comment -> one space, so neighbours cannot fuse into a false match
        if char == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                i += 1
            i = min(n, i + 2)
            out.append(" ")
            continue
        # string / char literal: copied through verbatim, escapes included
        if char in ('"', "'"):
            quote = char
            out.append(char)
            i += 1
            while i < n:
                if text[i] == "\\" and i + 1 < n:
                    out.append(text[i:i + 2])
                    i += 2
                    continue
                out.append(text[i])
                closed = text[i] == quote
                i += 1
                if closed:
                    break
            continue
        out.append(char)
        i += 1
    return "".join(out)


def strip_comments_and_literals(text: str) -> str:
    """Remove comments AND blank the CONTENTS of every string/char literal, keeping the quotes.

    This is a THIRD policy, distinct from both `strip_comments` and `strip_whole_line_comments`,
    and it exists because several PowerShell guards shared a `Strip-Comments` that behaved this
    way. It is not `strip_comments` with a flag:

        `strip_comments`            keeps a literal verbatim, so a pattern CAN match text inside one
        `strip_comments_and_literals`  replaces the contents with nothing, so it cannot

    The difference is load-bearing for a guard that matches source text. A C# file whose log message
    reads `"p.theHealth = 0 after the apply"` matches `\\b[pz]\\.(\\w+)\\s*=(?!=)` if the literal is
    kept, and matches nothing if the literal is blanked. Under the blanking policy that file is
    clean; under the other it is a false positive that hard-fails a deploy.

    Two further details are transcribed from the PowerShell original rather than invented, because
    a rewrite that "fixed" them would change what the text looks like to a pattern:

    * a doubled quote is an escaped quote, so `"a""b"` is ONE literal, not two. `strip_comments`
      closes on the first `"` and would then read `b"` as code.
    * a backslash escapes the next character only inside a DOUBLE-quoted literal. Inside `'...'` a
      backslash is a literal backslash, so `'a\\'` ends at the second quote. `strip_comments`
      treats `\\` as an escape in both, which is why it is not a substitute here.

    A line comment consumes to end of line and the newline is KEPT, so line numbers survive; a
    block comment becomes a single space so two tokens either side cannot fuse into a false match.
    """
    out: list[str] = []
    i, n = 0, len(text)
    while i < n:
        char = text[i]
        if char == "/" and i + 1 < n and text[i + 1] == "/":
            while i < n and text[i] != "\n":
                i += 1
            continue
        if char == "/" and i + 1 < n and text[i + 1] == "*":
            i += 2
            while i + 1 < n and not (text[i] == "*" and text[i + 1] == "/"):
                i += 1
            i = min(n, i + 2)
            out.append(" ")
            continue
        if char in ('"', "'"):
            quote = char
            i += 1
            while i < n:
                # A backslash escapes the next character only inside a double-quoted literal.
                if text[i] == "\\" and quote == '"' and i + 1 < n:
                    i += 2
                    continue
                if text[i] == quote:
                    # A doubled quote is an escaped quote, so this literal continues.
                    if i + 1 < n and text[i + 1] == quote:
                        i += 2
                        continue
                    i += 1
                    break
                i += 1
            # The quotes survive; the contents do not. That is the whole policy.
            out.append('""')
            continue
        out.append(char)
        i += 1
    return "".join(out)


def strip_comments_preserving_layout(text: str) -> str:
    """Replace every comment with SPACES, keeping every character offset and every newline.

    This is a FOURTH policy, and it is the one that makes a reported line number true. Compare:

        `strip_comments`   a block comment collapses to ONE space, so every line after it shifts
        `strip_comments_and_literals`  same, and literals are blanked
        THIS               each comment character becomes a space, so the text keeps its length
                           and its line count exactly

    A guard that reports `file.cs:412` has to mean line 412 of the file the operator will open.
    Under the other two policies a file with a large block comment near the top reports line
    numbers that do not exist in the source, which sends an operator to the wrong place - worse
    than reporting no line at all.

    Literals are kept VERBATIM here, so a pattern can match text inside a string. That is this
    policy's other half and it is the difference from `strip_comments_and_literals`; a guard
    choosing this one has decided it does not mind a log message reading like code.
    """
    chars = list(text)
    i, n = 0, len(chars)
    while i < n:
        char = chars[i]
        if char == "/" and i + 1 < n and chars[i + 1] == "/":
            while i < n and chars[i] != "\n":
                chars[i] = " "
                i += 1
            continue
        if char == "/" and i + 1 < n and chars[i + 1] == "*":
            chars[i] = chars[i + 1] = " "
            i += 2
            while i + 1 < n and not (chars[i] == "*" and chars[i + 1] == "/"):
                if chars[i] != "\n":
                    chars[i] = " "
                i += 1
            if i + 1 < n:
                chars[i] = chars[i + 1] = " "
                i += 2
            continue
        if char in ('"', "'"):
            quote = char
            i += 1
            while i < n:
                if chars[i] == "\\" and i + 1 < n:
                    i += 2
                    continue
                if chars[i] == quote:
                    i += 1
                    break
                i += 1
            continue
        i += 1
    return "".join(chars)


def strip_comments_and_literals_preserving_layout(text: str) -> str:
    """Blank every comment AND every string/char literal, keeping every offset and every newline.

    The FIFTH policy, and the one that combines the two others:

        `strip_comments_and_literals`      blanks literals, COLLAPSES a block to one space
        `strip_comments_preserving_layout`  keeps literals, KEEPS length and line count
        THIS                               blanks literals AND keeps length and line count

    It exists because the shape of the evidence a guard needs is two independent choices, and the
    registry-driven battle-responsibility guard asks for both at once: a mechanism must not be
    satisfied by text inside a string literal or a comment (so literals must be blanked), and a
    registered pattern is written against the source's own spacing (so layout must hold).

    THE SECOND HALF IS FIDELITY, NOT A CURRENT REQUIREMENT, and that distinction is worth keeping
    straight. Measured against `gk-core/scripts/battle-responsibility.v1.json`: all 6 patterns use `\\s*` -
    zero-or-more - which matches whether a comment between two tokens became one space or five, so
    NO pattern in the register today distinguishes this policy from `strip_comments_and_literals`.
    A fixture with `\\s{3,}` across a block comment does distinguish them, which is the point: the
    two policies are not interchangeable in general, so a future pattern that needs the real spacing
    will not find that the guard quietly changed meaning underneath it. Substituting the collapsing
    policy here would be a latent behaviour change, and the original's own choice is the cheaper
    thing to honour.

    Two details no other policy here has, both transcribed rather than invented:

    * `@"..."` VERBATIM strings are recognised. A raw C# string's contents are code-shaped text, and
      treating the `@"` as two ordinary characters lets the scanner walk into the middle of it.

      Its `""` ESCAPE is transcribed but is DEFENSIVE, not behavioural, and this is worth knowing
      before anyone tries to test it: the scan leaves the verbatim string at the same place either
      way, and every character after it is blanked regardless, so the branch cannot be observed
      through the stripped output. Measured - six candidate inputs, including ones with a `//`, a
      `/* */` and a following real literal, and NONE changes the output. It is kept because the
      original has it and a scanner that mis-tracks its own position is a latent bug, not because a
      test can pin it. A test claiming to cover it would be asserting nothing, which is worse than
      having no test.
    * AN UNTERMINATED LITERAL STOPS AT THE NEWLINE instead of consuming to end of input. The
      original says so in a comment - "an unterminated literal must not eat the rest of the file" -
      because a stray apostrophe in a comment fragment would otherwise blank every line after it and
      turn the guard silently blind. This is the OPPOSITE choice from
      `strip_comments_preserving_layout`, whose own original consumes to end of input; both are
      transcribed, and each is right for the guard that carries it.

    Line comments, blocks and literals all become spaces, so a reported character offset is the
    file's own.
    """
    out: list[str] = []
    i, n = 0, len(text)
    while i < n:
        char = text[i]
        nxt = text[i + 1] if i + 1 < n else ""

        if char == "/" and nxt == "/":
            while i < n and text[i] != "\n":
                out.append(" ")
                i += 1
            continue

        if char == "/" and nxt == "*":
            out.append("  ")
            i += 2
            while i < n:
                if text[i] == "*" and i + 1 < n and text[i + 1] == "/":
                    out.append("  ")
                    i += 2
                    break
                out.append(text[i] if text[i] in "\n\r" else " ")
                i += 1
            continue

        # A verbatim/raw string: `@"`, with `""` as the escape, and a newline does not end it.
        if char == "@" and nxt == '"':
            out.append("  ")
            i += 2
            while i < n:
                if text[i] == '"':
                    if i + 1 < n and text[i + 1] == '"':
                        out.append("  ")
                        i += 2
                        continue
                    out.append(" ")
                    i += 1
                    break
                out.append(text[i] if text[i] in "\n\r" else " ")
                i += 1
            continue

        if char in ('"', "'"):
            quote = char
            out.append(" ")
            i += 1
            while i < n:
                if text[i] == "\\" and i + 1 < n:
                    out.append("  ")
                    i += 2
                    continue
                if text[i] == quote:
                    out.append(" ")
                    i += 1
                    break
                if text[i] == "\n":
                    # An unterminated literal must not eat the rest of the file.
                    out.append("\n")
                    i += 1
                    break
                out.append(" ")
                i += 1
            continue

        out.append(char)
        i += 1
    return "".join(out)


def line_of(text: str, index: int) -> int:
    """1-based line number of `index` within `text`. Used to report WHERE a finding is, so a
    boundary report names a line an operator can jump to rather than only a file."""
    return text.count("\n", 0, index) + 1


def strip_whole_line_comments(text: str) -> str:
    """Blank lines whose FIRST non-space characters are `//`, `*` or `/*`. Keep everything else.

    This is WEAKER than `strip_comments` on purpose, and the weakness is a stated policy rather
    than an oversight, so it lives here under its own name instead of being quietly upgraded:

        Whole-line comments are not code, and matching them is a false positive that hard-fails a
        deploy. Found 2026-09-04: a comment in ModifierOp.cs that DOCUMENTS the boundary
        ("EntityStatWriter.cs writes the entity that has it") failed the guard, which then failed
        the deploy outright. Documenting the rule must never look like breaking it.

        This is deliberately NOT full comment/string awareness - a trailing comment on a line of
        real code is still scanned, so the rule is narrowed for documentation and not weakened
        for code.

    That last sentence is the whole reason this is a second function. Upgrading a guard to
    `strip_comments` is a CONTRACT change - it stops scanning trailing comments - and it must be a
    deliberate decision by the guard's owner, not a tidy-up.

    The residual gap is real but NARROW, and it was MEASURED rather than assumed - a first draft of
    this docstring claimed every multi-line block body survives, and the guard-funnel-delta
    differential disproved it. `*` is caught by the same test as everything else, so a
    CONVENTIONAL block comment (`/*` then ` * ...` per line) is stripped in full. What survives is
    a block comment whose body lines do NOT begin with `*`:

        /*\\nTakeDamage is forbidden\\n*/         <- body survives, still scanned
        /*\\n * TakeDamage is forbidden\\n */     <- body stripped

    `guard-open-identity` had the false positive and it was fixed there; `guard-funnel-delta`'s
    own comment says the weaker form is intended, so it is preserved and named here rather than
    quietly upgraded.

    Comment lines are BLANKED, not removed, so `cscan.line_of` still reports the line number in
    the ORIGINAL file. Blanking is matching-equivalent to removal for every pattern that does not
    spell a literal newline: a blank line contributes only whitespace, and `\\s` already matched
    the newlines that removal would have joined. A pattern containing a literal `\\n` would see
    the difference, and no guard uses one - but a fixture that proves the equivalence for the
    patterns actually in play is the honest way to keep that claim, rather than asserting it here.
    """
    kept: list[str] = []
    for line in text.replace("\r\n", "\n").replace("\r", "\n").split("\n"):
        stripped = line.lstrip()
        if stripped.startswith("//") or stripped.startswith("*") or stripped.startswith("/*"):
            kept.append("")
            continue
        kept.append(line)
    return "\n".join(kept)

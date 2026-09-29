"""Reclaim a DE-REGISTERED worktree directory whose every file is already landed, or refuse.

`git worktree remove` de-registers first and deletes second, so a delete that fails on a Windows long
path leaves a directory holding tens of thousands of files that git can never reclaim and never lists
again. Measured 2026-09-27 on `opencode-resume-28b-...`: `git worktree remove --force` de-registered it
(32 -> 31 worktrees) and then failed with `Filename too long`, leaving **14,392 files** on disk.

`--reclaim-only` does not help: it removes *provably empty* directories and correctly refuses this one,
because a directory with content in it might be holding unlanded work. So the reclamation needs a
division of labour, and this is it:

  * the **manager** adjudicates by content and passes the paths that are NOT at integration;
  * this **tool** proves the rest. Every other file's content must already exist somewhere in
    integration's HISTORY (not merely equal today's `HEAD` - see `reachable_blobs`), and any file
    whose content is nowhere in that history aborts the whole reclaim.

Git-ignored paths are excluded from the proof, and the count is reported rather than swallowed: an
ignored path is by construction not tracked content, so it cannot be unlanded *work* - but a count the
caller cannot see would be a blind spot, which is the thing this tool exists to close.

That ordering is the safety property. A file the manager forgot to adjudicate cannot be deleted,
because the proof fails and the tool refuses. A file the manager named is deleted only because the
manager looked at it. Neither the tool nor the caller can delete unadjudicated content alone.

Removal uses the long-path-safe route - `robocopy /MIR` from an empty directory, then `rmdir` - because
`git worktree remove` and `shutil.rmtree` both fail on the paths that create this condition.
"""
import argparse
import collections
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile

TIMEOUT = 900
INTEGRATION_DEFAULT = 'features/mega-merge'
WALK_CAP = 200_000

REFUSALS = {
    'STILL-REGISTERED': 'git still lists this path as a worktree, so it is not a leftover',
    'HAS-GITDIR': 'the directory still has a .git entry, so it is a live worktree, not a leftover',
    'NOT-FOUND': 'no such directory',
    'HELD': 'a live process holds a handle on the directory',
    'PATH-TOO-LONG': 'the path exceeds the legacy 260-character limit even after the extended-length pass',
    'UNLANDED-FILE': 'at least one file has content nowhere in integration history',
    'WALK-CAPPED': 'the file walk hit its cap, so "every file is landed" was never established',
    'OBJECTS-UNREADABLE': "could not enumerate the objects reachable from integration, so nothing "
                          "could be proven",
    'HASH-FAILED': 'git hash-object failed, so no file could be proven either way',
    'HASH-COUNT-MISMATCH': 'git returned fewer hashes than files given, so the proof is incomplete',
    'LONG-PATH-NOT-IGNORED': 'a path too long for git is NOT git-ignored, so it may be real unlanded '
                             'work and must be adjudicated by hand',
    'IGNORE-QUERY-FAILED': 'could not ask git which paths are ignored, so the exclusion is unproven',
    'PREFIX-IS-TRACKED': 'an --adjudicated-prefix covers paths tracked at integration, so it is not '
                         'build output and cannot be cleared as a subtree',
}


class Refusal(RuntimeError):
    def __init__(self, name, detail):
        super().__init__(f'{name}: {detail}')
        self.name = name
        self.detail = detail


def git(*args, cwd=None, timeout=TIMEOUT, input=None):
    """Run git, capturing output. `input` is encoded to BYTES deliberately.

    With `text=True`, Python performs newline translation in BOTH directions on Windows: the
    `'\n'.join(paths)` written to git's stdin arrives as `\r\n`, so every path carries a trailing CR.
    git echoes the path it was given, CR included, and the returned value never matches the clean path
    from `os.walk` - which is exactly what happened here, and the ignore exclusion silently matched
    nothing while appearing to work. Sending bytes removes the translation at the source; the outputs
    are decoded here so callers still get `str`.
    """
    payload = input.encode('utf-8', 'surrogateescape') if isinstance(input, str) else input
    r = subprocess.run(['git', *args], capture_output=True, cwd=cwd, timeout=timeout, input=payload)
    return (r.returncode, r.stdout.decode('utf-8', 'replace'), r.stderr.decode('utf-8', 'replace'))


def _same(a, b):
    r"""Do these two paths name the same thing, 8.3 alias and separator included?

    Two Windows-specific ways to say "same path" and get it wrong, both hit while writing this:

    * **separators.** git prints POSIX separators on Windows. `retire_worktrees.py` had this bug once
      already - comparing a git-printed path against a `Path` string made every registered worktree look
      unregistered, which is the exact failure a leftover scan must not have.
    * **the 8.3 short-name alias.** `TEMP` on this machine is `C:\Users\NENESC~1\...` while git
      prints the long `C:\Users\NENESCARLET\...`. `os.path.abspath` does **not** canonicalise the
      alias, so a correct comparison said "not registered" for a live worktree.

    `os.path.samefile` compares by stat, so it gets both right. It needs both paths to exist, so the
    string comparison stays as the fallback and as the only option for a path that is already gone.
    """
    try:
        if os.path.exists(a) and os.path.exists(b):
            return os.path.samefile(a, b)
    except OSError:
        pass
    norm = lambda x: os.path.normcase(os.path.abspath(str(x).replace('/', os.sep)))  # noqa: E731
    return norm(a) == norm(b)


def _is_registered(root, path):
    _, out, _ = git('worktree', 'list', '--porcelain', cwd=root)
    for line in out.splitlines():
        if not line.startswith('worktree '):
            continue
        if _same(line[9:], path):
            return True
    return False


def reachable_blobs(root):
    """Every blob SHA reachable from integration, as ONE set.

    The criterion is **reachability, not equality with HEAD**. A de-registered worktree is a checkout
    of an *ancestor* commit, so its files legitimately differ from today's `HEAD` - integration has
    moved on since. Comparing against `HEAD` therefore refuses the very case this tool exists for: a
    test fixture built at `HEAD~1` came back `UNLANDED-FILE` for `a.txt` and `deep/b.txt`, which is the
    tool rejecting a perfectly landed older revision.

    The question that actually matters is narrower and answerable: *does this exact content already
    exist anywhere in integration's history?* If it does, the file is landed and nothing is lost by
    deleting it. If it does not, the content is unique to something integration does not contain, and
    the tool must stop. One `rev-list --objects` answers it for the whole tree.
    """
    code, out, _ = git('rev-list', '--objects', INTEGRATION_DEFAULT, cwd=root,
                        timeout=TIMEOUT * 4)
    if code != 0:
        raise Refusal('OBJECTS-UNREADABLE',
                      'git rev-list --objects over integration did not succeed')
    return {line.split(' ', 1)[0] for line in out.splitlines() if line.strip()}


def _blob_shas(root, paths):
    """git's own blob SHA for each path, in one process, via `hash-object --stdin-paths`.

    An earlier version computed the hash by hand - `sha1("blob <size>\0" + bytes)` - and was wrong on
    Windows: a file written with Python's `write_text` holds CRLF on disk (`newline=None` translates
    `\n` to `os.linesep`) while git stores LF, so the hand-rolled SHA never matched anything and every
    file read as unlanded. Two ways to say "no" at once.

    Asking git is both correct and faster: `hash-object` applies the same filters git would (attributes,
    `core.autocrlf`), and `--stdin-paths` does the whole tree in one process instead of 14,392.
    """
    if not paths:
        return []
    payload = '\n'.join(str(x) for x in paths)
    code, out, err = git('hash-object', '--stdin-paths', cwd=root, timeout=TIMEOUT * 4, input=payload)
    if code != 0:
        raise Refusal('HASH-FAILED',
                      f'git hash-object exited {code} on {len(paths)} path(s): {err.strip()[:200]}')
    return out.split()


def _git_ignored(root, rels):
    """Which of these WORKTREE-relative paths git ignores.

    The paths are made worktree-relative BEFORE they reach git, and that is the whole point.
    `.gitignore:91` is `.claude/worktrees/`, so handing git a `.claude/worktrees/<name>/...` path asks
    "is this under an ignored directory" - which is yes for every file in the worktree, tracked or not.
    That would exclude the entire proof and report a clean reclaim over 2.28 GB of unadjudicated content.
    Stripping the prefix asks the question that was meant: does `obj/...` match `[Oo]bj/` (yes) while
    `src/...` does not (no).
    """
    if not rels:
        return set(), 0
    # `git()` sends bytes, so no CR is introduced here any more. Plain output is therefore safe, and
    # `-z` is deliberately NOT used: `check-ignore -z --stdin` returns empty on this git build even
    # though it is documented, which would silently disable the whole exclusion. A path git decides to
    # quote still arrives quoted, so the quote-strip is kept as a belt-and-braces measure.
    sent = [r.strip() for r in rels]
    code, out, err = git('check-ignore', '--stdin', cwd=root, timeout=TIMEOUT * 4,
                         input='\n'.join(sent))
    if code not in (0, 1):
        raise Refusal('IGNORE-QUERY-FAILED',
                      f'git check-ignore exited {code}: {err.strip()[:200]}')
    ignored = set()
    for line in out.splitlines():
        value = line.strip().strip('"').strip()
        if value:
            ignored.add(value)
    # git stays SILENT about a path it cannot open rather than reporting it as not-ignored, so every
    # long build-output path comes back missing from the set and lands in the caller as
    # LONG-PATH-NOT-IGNORED. Measured on the real orphan: 63 such paths, all under
    # `bin/Release/net8.0/` and `obj/Release/net8.0/`, none of which git would classify any other way.
    #
    # The answer is to ask about the ANCESTOR instead. A directory-level ignore rule (`bin/`) ignores
    # everything beneath it, so if any parent directory is ignored this file is ignored by exactly the
    # rule git applies - and a directory prefix is short enough to evaluate. This derives the answer
    # from git's own rules rather than hardcoding `bin/` and `obj/`, which is the difference between a
    # rule and a guess.
    missing = [r for r in rels if r not in ignored]
    if missing:
        prefixes = set()
        for rel in missing:
            parts = rel.split('/')
            for i in range(len(parts) - 1, 0, -1):
                prefixes.add('/'.join(parts[:i]) + '/')
        if prefixes:
            hit, _ = _git_ignored_raw(root, sorted(prefixes))
            ignored |= {rel for rel in missing
                        if any(rel == p or rel.startswith(p) for p in hit)}
    return ignored, len(rels)


def _git_ignored_raw(root, rels):
    """The un-normalised query, so `_git_ignored` can recurse without normalising twice."""
    sent = [r.strip() for r in rels]
    code, out, err = git('check-ignore', '--stdin', cwd=root, timeout=TIMEOUT * 4,
                         input='\n'.join(sent))
    if code not in (0, 1):
        raise Refusal('IGNORE-QUERY-FAILED',
                      f'git check-ignore exited {code}: {err.strip()[:200]}')
    return {line.strip().strip('"').strip() for line in out.splitlines()
            if line.strip().strip('"').strip()}, len(rels)


def _pattern_of(rel):
    """The first two path segments - enough to see `obj/Release` at a glance, not so deep it is noise."""
    parts = rel.split('/')
    return '/'.join(parts[:2]) if len(parts) > 1 else parts[0]


def audit_unlanded(root, directory, adjudicated, blobs=None):
    """Every file under `directory` whose content is nowhere in integration, minus the adjudicated set."""
    base = pathlib.Path(directory)
    skip = {str(base / p).replace('\\', '/') for p in adjudicated}
    if blobs is None:
        blobs = reachable_blobs(root)
    paths, walked = [], 0
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d != '.git']
        for name in filenames:
            full = pathlib.Path(dirpath) / name
            if str(full).replace('\\', '/') in skip:
                continue
            walked += 1
            if walked > WALK_CAP:
                raise Refusal('WALK-CAPPED',
                              f'stopped after {WALK_CAP} files; "everything is landed" is unproven')
            paths.append((full, str(full.relative_to(base)).replace('\\', '/')))

    # Partition: ignored paths cannot be unlanded *tracked* content, so they leave the proof - but the
    # count and the patterns they fall under are reported, so a caller can see what was not examined.
    ignored, _ = _git_ignored(root, [rel for _, rel in paths])
    kept = [(f, rel) for f, rel in paths if rel not in ignored]
    skipped_patterns = collections.Counter(_pattern_of(rel) for rel in ignored)

    # A path too long for git that is NOT ignored may be real work; refuse rather than drop it.
    too_long = sorted(rel for _, rel in kept if len(str(_root_of(directory, rel))) > 255)
    if too_long:
        raise Refusal('LONG-PATH-NOT-IGNORED',
                      f'{len(too_long)} non-ignored path(s) exceed what git can open, so they cannot be '
                      f'proven landed and must be adjudicated by hand. First: {too_long[:3]}')

    shas = _blob_shas(root, [str(f) for f, _ in kept])
    if len(shas) != len(kept):
        raise Refusal('HASH-COUNT-MISMATCH',
                      f'git returned {len(shas)} hashes for {len(kept)} files; the proof is incomplete')
    unlanded = [rel for (_, rel), sha in zip(kept, shas) if sha not in blobs]
    return unlanded, walked, {'totalFiles': walked, 'hashed': len(kept),
                              'ignoredSkipped': len(ignored),
                              'ignoredPatterns': dict(skipped_patterns.most_common(8))}


def _root_of(directory, rel):
    return pathlib.Path(directory) / rel


def _remove_long_path(directory, timeout):
    """Empty then delete, the only route that works past 260 characters."""
    empty = tempfile.mkdtemp(prefix='reclaim-empty-')
    try:
        if os.name == 'nt':
            subprocess.run(['robocopy', empty, str(directory), '/MIR', '/NFL', '/NDL',
                            '/NJH', '/NJS', '/NP', '/R:1', '/W:1'],
                           capture_output=True, text=True, errors='replace', timeout=timeout)
        else:
            for entry in pathlib.Path(directory).iterdir():
                if entry.is_dir() and not entry.is_symlink():
                    shutil.rmtree(entry, ignore_errors=True)
                else:
                    try:
                        entry.unlink()
                    except OSError:
                        pass
        for attempt in (lambda: os.rmdir(directory),
                        lambda: os.rmdir('\\\\?\\' + os.path.abspath(directory))):
            try:
                attempt()
                return 'REMOVED'
            except FileNotFoundError:
                return 'GONE'
            except OSError as exc:
                last = exc
        err = getattr(last, 'winerror', None)
        if err in (32, 33) or last.errno in (13, 16):
            raise Refusal('HELD', 'a live process holds a handle on the directory')
        if err == 206 or last.errno == 36:
            raise Refusal('PATH-TOO-LONG', 'still too long after the extended-length pass')
        raise Refusal('HELD', str(last))
    finally:
        shutil.rmtree(empty, ignore_errors=True)


def directory_of(root, name):
    return pathlib.Path(root) / '.claude' / 'worktrees' / name


def _under(base, prefix):
    """Every walked path under `prefix`, expressed relative to `base`."""
    start = str(base / prefix.rstrip('/')).replace('\\', '/')
    out = []
    for dirpath, dirnames, filenames in os.walk(base):
        dirnames[:] = [d for d in dirnames if d != '.git']
        for name in filenames:
            full = str(pathlib.Path(dirpath) / name).replace('\\', '/')
            if full == start or full.startswith(start + '/'):
                out.append(os.path.relpath(full, str(base)).replace('\\', '/'))
    return out


def assert_prefix_is_untracked(root, prefixes):
    r"""Refuse a prefix that would swallow content integration actually has.

    This is the entire safety argument for adjudicating a subtree rather than a file, so it is enforced
    instead of documented. A prefix naming tracked content is not a build-output tree, it is a real
    worktree's output, and clearing it on the strength of the word "build" would be precisely the
    failure this tool exists to prevent. Build output is untracked by definition, so the gate passes
    exactly the case the flag exists for.
    """
    for prefix in prefixes:
        clean = prefix.strip().rstrip('/')
        if not clean:
            continue
        code, out, _ = git('ls-tree', '-r', '--name-only', INTEGRATION_DEFAULT, '--', clean, cwd=root)
        if code != 0:
            raise Refusal('PREFIX-IS-TRACKED',
                          f'git ls-tree over "{clean}" exited {code}; the prefix could not be proven '
                          f'untracked, so it is refused rather than assumed to be build output')
        tracked = [ln for ln in out.splitlines() if ln.strip()]
        if tracked:
            raise Refusal('PREFIX-IS-TRACKED',
                          f'"{clean}" holds {len(tracked)} path(s) tracked at {INTEGRATION_DEFAULT} '
                          f'(first: {tracked[0]}), so it is not build output and must be adjudicated '
                          f'file by file')
    return True


def reclaim_worktree_dir(root, name, adjudicated, apply, timeout=TIMEOUT, prefixes=()):
    assert_prefix_is_untracked(root, prefixes)
    directory = pathlib.Path(root) / '.claude' / 'worktrees' / name
    named = list(adjudicated)
    if prefixes:
        adjudicated = named + [p for pfx in prefixes for p in _under(directory, pfx)]
    if not directory.is_dir():
        raise Refusal('NOT-FOUND', str(directory))
    if _is_registered(root, directory):
        raise Refusal('STILL-REGISTERED', str(directory))
    if (directory / '.git').exists():
        raise Refusal('HAS-GITDIR', str(directory))

    unlanded, walked, partition = audit_unlanded(root, directory, adjudicated)
    # Record what the CALLER named, not what that expanded to. Echoing the expansion produced a
    # 10,000-line `--json` result on the real orphan, which makes the tool's own record unreadable -
    # and a record nobody can read is not evidence. The prefix count carries the size instead.
    plan = {'name': name, 'path': str(directory).replace('\\', '/'), 'filesWalked': walked,
            'adjudicated': sorted(named),
            'adjudicatedViaPrefix': len(adjudicated) - len(named),
            'unlandedCount': len(unlanded),
            'unlanded': unlanded[:40], 'partition': partition,
            'adjudicatedPrefixes': sorted(p for p in prefixes if p)}
    if unlanded:
        raise Refusal('UNLANDED-FILE',
                      f'{len(unlanded)} file(s) whose content is nowhere in integration\'s history; adjudicate them and pass them '
                      f'as --adjudicated, or stop. First: {unlanded[:5]}')
    if not apply:
        plan['outcome'] = 'WOULD-REMOVE'
        return plan
    plan['outcome'] = _remove_long_path(directory, timeout)
    return plan


def main(argv=None):
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument('name', help='directory name under .claude/worktrees')
    ap.add_argument('--adjudicated', action='append', default=[], metavar='PATHS',
                    help='repo-relative paths judged unlanded and cleared to delete. Repeatable, and '
                         'each occurrence may itself be a comma-separated list.')
    ap.add_argument('--adjudicated-prefix', action='append', default=[], metavar='SUBTREE',
                    help='adjudicate a whole subtree as cleared, e.g. a bin/ or obj/ build-output tree, '
                         'so a manager is not asked to enumerate machine-generated filenames. Refused '
                         'by name if anything under it is tracked at integration.')
    ap.add_argument('--apply', action='store_true', help='actually remove it')
    ap.add_argument('--json', action='store_true')
    ap.add_argument('--timeout', type=float, default=TIMEOUT)
    ap.add_argument('--root', type=pathlib.Path, default=pathlib.Path('.'))
    args = ap.parse_args(argv)
    if args.timeout <= 0:
        print('REFUSED: NEGATIVE-TIMEOUT', file=sys.stderr)
        return 2
    # `action='append'` rather than a store: argparse keeps the LAST value of a repeated store flag, so
    # a caller who writes `--adjudicated a --adjudicated b` would have every name but `b` silently
    # dropped. The proof then refuses on a file the caller *did* adjudicate, which sends them chasing a
    # phantom - measured here, where four `--adjudicated` flags left exactly the other three unlanded.
    # Nothing is at risk (a dropped name makes the tool refuse, not delete), but the trap is read as
    # "integration holds work" when it holds nothing, so both spellings are accepted and merged.
    adjudicated = list(dict.fromkeys(
        p.strip() for chunk in args.adjudicated for p in chunk.split(',') if p.strip()))
    try:
        plan = reclaim_worktree_dir(args.root.resolve(), args.name, adjudicated, args.apply,
                                    args.timeout, args.adjudicated_prefix)
    except Refusal as r:
        if args.json:
            print(json.dumps({'outcome': 'REFUSED', 'name': r.name, 'detail': r.detail,
                              'meaning': REFUSALS[r.name]}, indent=2))
        else:
            print(f'RECLAIM REFUSED: {r.name}: {r.detail}', file=sys.stderr)
            print(f'  meaning: {REFUSALS[r.name]}', file=sys.stderr)
        return 2
    print(json.dumps(plan, indent=2) if args.json
          else f"{plan['outcome']} {plan['name']}  ({plan['partition']['hashed']} files proven landed, "
               f"{plan['partition']['ignoredSkipped']} git-ignored skipped"
               + (f" under {', '.join(plan['partition']['ignoredPatterns'])}"
                  if plan['partition']['ignoredPatterns'] else '')
               + f", {len(plan['adjudicated'])} adjudicated)")
    return 0


if __name__ == '__main__':
    sys.exit(main())

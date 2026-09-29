#!/usr/bin/env python3
"""
Anchor ledger — minimal JSONL run-state for a long-run program so any agent can resume mid-run
without restarting from the beginning.

One ledger per program: tasks/<program>-ledger.jsonl. One line per event, append-only; the last
line of each kind wins (no locking needed for a single writer per session — the session-boundary
record owns the program while it runs).

Kinds (exactly these six — anything else goes in tasks/evidence-fragments/<task-id>.md):
  anchor   — source-of-truth pointers (plan/todo/specs paths, session id, standards rows read)
  task     — one task's lifecycle: started | done (+evidence path) | blocked (+reason)
  note     — one decision or correction future sessions must not rediscover (<= 280 chars)
  gate     — one deterministic gate result: session-boundary-check | verify-change | guard:<name>
  queue    — set or replace the ordered task queue (latest line wins; lets a lane adopt an
             already-anchored ledger whose anchor carries no queue)
  complete — program closed: every requirement mapped to impl + evidence (or why not)

Usage (repo root):
    python gk-core/scripts/anchor-ledger.py tasks/solid-remediation-ledger.jsonl anchor --plan tasks/solid-remediation-plan.md --todo tasks/solid-remediation-todo.md --session solid-remediation-20260917 --queue "T6 T7"
    python gk-core/scripts/anchor-ledger.py <ledger> queue --ids "ST1.1 ST1.2 ST1.3"
    python gk-core/scripts/anchor-ledger.py <ledger> task --id T6 --state started
    python gk-core/scripts/anchor-ledger.py <ledger> task --id T6 --state done --evidence tasks/evidence-fragments/T6.md
    python gk-core/scripts/anchor-ledger.py <ledger> task --id T7 --state blocked --reason "waits on T6 re-bless"
    python gk-core/scripts/anchor-ledger.py <ledger> note --text "BattleStatComposer is deleted; never cite its ADR as precedent"
    python gk-core/scripts/anchor-ledger.py <ledger> gate --name session-boundary-check --result pass
    python gk-core/scripts/anchor-ledger.py <ledger> complete
    python gk-core/scripts/anchor-ledger.py <ledger> resume   # print resume brief to stdout, exit 0
    python gk-core/scripts/anchor-ledger.py <ledger> check    # exit 0 clean, 1 on schema/sequence drift

Exit codes: 0 = ok, 1 = drift found (check) or gate recorded fail, 2 = usage error.
Only the stdlib is used (argparse/hashlib/json/os/sys + datetime).

Integrity: every line written by this script carries a `mark` (sha256 over a public salt +
timestamp + kind + body). The mark catches ACCIDENTAL hand-writing — a line added or edited
outside the script will not carry a valid mark. It is NOT forgery protection: the salt is
public in this file, so anyone can mint a valid mark on purpose. Leniency rule: unmarked
lines that precede the first marked line in a ledger are legacy (trusted with a warning, so
old ledgers still resume); any unmarked or tampered line at or after the first marked line
is drift and fails `check`.
"""
import argparse
import hashlib
import json
import os
import sys
from datetime import datetime, timezone

KINDS = ("anchor", "task", "note", "gate", "queue", "complete")
TASK_STATES = ("started", "done", "blocked")
GATE_RESULTS = ("pass", "fail")
NOTE_LIMIT = 280
MARK_SALT = "anchor-ledger-v1"
REQUIRED = {
    "anchor": ("plan", "todo", "session"),
    "task": ("id", "state"),
    "note": ("text",),
    "gate": ("name", "result"),
    "queue": ("ids",),
    "complete": (),
}


def utcnow():
    return datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%SZ")


def integrity_mark(ts, kind, body):
    digest = hashlib.sha256(
        ("%s|%s|%s|%s" % (MARK_SALT, ts, kind, body)).encode("utf-8")
    ).hexdigest()[:16]
    return "mk1-" + digest


def first_marked_index(events):
    for i, (_, ev) in enumerate(events):
        if mark_ok(ev):
            return i
    return None


def is_legacy(events, index):
    if mark_ok(events[index][1]):
        return False
    first = first_marked_index(events)
    return first is None or index < first


def legacy_count(events):
    return sum(1 for i in range(len(events)) if is_legacy(events, i))


def ev_body(ev):
    rest = {k: v for k, v in ev.items() if k not in ("ts", "kind", "mark")}
    return json.dumps(rest, sort_keys=True, separators=(",", ":"))


def mark_ok(ev):
    return isinstance(ev, dict) and ev.get("mark") == integrity_mark(
        ev.get("ts", ""), ev.get("kind", ""), ev_body(ev)
    )


def read_events(path):
    events = []
    if not os.path.exists(path):
        return events, None
    with open(path, encoding="utf-8") as fh:
        for lineno, line in enumerate(fh, 1):
            line = line.strip()
            if not line:
                continue
            try:
                ev = json.loads(line)
            except json.JSONDecodeError as exc:
                return None, "line %d: not JSON (%s)" % (lineno, exc)
            events.append((lineno, ev))
    return events, None


def validate(events):
    seen_anchor = False
    open_tasks = set()
    failures = []
    for index, (lineno, ev) in enumerate(events):
        if not isinstance(ev, dict):
            failures.append("line %d: event is not an object" % lineno)
            continue
        kind = ev.get("kind")
        if kind not in KINDS:
            failures.append("line %d: unknown kind %r" % (lineno, kind))
            continue
        for field in REQUIRED[kind]:
            if field not in ev or ev.get(field) in ("", None):
                failures.append("line %d: %s misses %r" % (lineno, kind, field))
        if kind == "anchor":
            seen_anchor = True
        elif not seen_anchor:
            failures.append("line %d: first event must be anchor" % lineno)
        if kind == "queue" and not isinstance(ev.get("ids"), list):
            failures.append("line %d: queue ids must be a list" % lineno)
        if kind == "task":
            tid, state = ev.get("id"), ev.get("state")
            if state not in TASK_STATES:
                failures.append("line %d: bad task state %r" % (lineno, state))
            elif state == "started":
                if tid in open_tasks:
                    failures.append("line %d: task %s started twice" % (lineno, tid))
                open_tasks.add(tid)
            elif state in ("done", "blocked"):
                if tid not in open_tasks:
                    failures.append("line %d: task %s %s without start" % (lineno, tid, state))
                else:
                    open_tasks.discard(tid)
            if state == "blocked" and not ev.get("reason"):
                failures.append("line %d: blocked task %s needs a reason" % (lineno, tid))
        if kind == "note" and len(str(ev.get("text", ""))) > NOTE_LIMIT:
            failures.append("line %d: note exceeds %d chars" % (lineno, NOTE_LIMIT))
        if kind == "gate" and ev.get("result") not in GATE_RESULTS:
            failures.append("line %d: bad gate result %r" % (lineno, ev.get("result")))
        if not is_legacy(events, index) and not mark_ok(ev):
            failures.append(
                "line %d: missing or bad integrity mark "
                "(write through scripts/anchor-ledger.py; hand-written lines are rejected)" % lineno
            )
    return failures


def cmd_append(args):
    ev = {"ts": utcnow(), "kind": args.kind}
    if args.kind == "anchor":
        ev.update(plan=args.plan, todo=args.todo, session=args.session)
        if args.specs:
            ev["specs"] = args.specs
        if args.standards:
            ev["standards"] = args.standards
        if args.queue:
            ev["queue"] = [t for t in args.queue.replace(",", " ").split() if t]
    elif args.kind == "task":
        ev.update(id=args.id, state=args.state)
        if args.evidence:
            ev["evidence"] = args.evidence
        if args.reason:
            ev["reason"] = args.reason
    elif args.kind == "note":
        ev["text"] = args.text
    elif args.kind == "gate":
        ev.update(name=args.name, result=args.result)
    elif args.kind == "queue":
        ids = [t for t in args.ids.replace(",", " ").split() if t]
        if not ids:
            print("queue needs at least one task id", file=sys.stderr)
            return 2
        ev["ids"] = ids
    elif args.kind == "complete":
        if args.note:
            ev["note"] = args.note
    ev["mark"] = integrity_mark(ev["ts"], ev["kind"], ev_body(ev))
    events, err = read_events(args.ledger)
    if err:
        print("ledger unreadable: %s" % err, file=sys.stderr)
        return 2
    if args.kind == "anchor" and any(ev2.get("kind") == "anchor" for _, ev2 in events):
        print("ledger already anchored; append task/note/gate instead", file=sys.stderr)
        return 2
    trial = events + [(len(events) + 1, ev)]
    failures = validate(trial)
    if failures:
        print("refused — would leave drift:", file=sys.stderr)
        for failure in failures:
            print("  %s" % failure, file=sys.stderr)
        return 1
    if os.path.exists(args.ledger) and os.path.getsize(args.ledger) > 0:
        with open(args.ledger, "rb") as fh:
            fh.seek(-1, os.SEEK_END)
            if fh.read(1) != b"\n":
                with open(args.ledger, "a", encoding="utf-8", newline="") as nl:
                    nl.write("\n")
    with open(args.ledger, "a", encoding="utf-8") as fh:
        fh.write(json.dumps(ev, separators=(",", ":")) + "\n")
    if args.kind == "gate" and args.result == "fail":
        return 1
    return 0


def summarize(events):
    anchors = [ev for _, ev in events if ev.get("kind") == "anchor"]
    anchor = anchors[-1] if anchors else {}
    queues = [ev for _, ev in events if ev.get("kind") == "queue"]
    queue = list(queues[-1].get("ids", [])) if queues else list(anchor.get("queue", []))
    last_task = {}
    for _, ev in events:
        if ev.get("kind") == "task":
            last_task[ev["id"]] = ev
    done = sorted(tid for tid, ev in last_task.items() if ev["state"] == "done")
    blocked = {tid: ev.get("reason", "") for tid, ev in last_task.items() if ev["state"] == "blocked"}
    active = sorted(tid for tid, ev in last_task.items() if ev["state"] == "started")
    gates = [(ev.get("name"), ev.get("result")) for _, ev in events if ev.get("kind") == "gate"]
    notes = [ev.get("text") for _, ev in events if ev.get("kind") == "note"]
    completed = any(ev.get("kind") == "complete" for _, ev in events)
    return anchor, queue, done, blocked, active, gates, notes, completed


def next_from_queue(queue, done, blocked, active):
    finished = set(done) | set(blocked)
    for tid in queue:
        if tid in active:
            return tid, "active"
        if tid not in finished:
            return tid, "queued"
    return None, ""


def cmd_resume(args):
    events, err = read_events(args.ledger)
    if err:
        print("ledger unreadable: %s" % err, file=sys.stderr)
        return 2
    failures = validate(events)
    if failures:
        print("DRIFT (%d):" % len(failures))
        for failure in failures:
            print("  %s" % failure)
        return 1
    anchor, queue, done, blocked, active, gates, notes, completed = summarize(events)
    legacy = legacy_count(events)
    if legacy:
        print("warning : %d legacy line(s) precede the first integrity mark; trusted leniently" % legacy)
    print("program : %s" % anchor.get("session", "?"))
    print("scope   : %s + %s" % (anchor.get("plan", "?"), anchor.get("todo", "?")))
    print("ledger  : %s" % args.ledger)
    if anchor.get("specs"):
        print("specs   : %s" % ", ".join(anchor["specs"]))
    if anchor.get("standards"):
        print("read    : %s" % anchor["standards"])
    if queue:
        print("queue   : %s" % ", ".join(queue))
    print("done    : %s" % (", ".join(done) if done else "none"))
    if blocked:
        for tid, reason in sorted(blocked.items()):
            print("blocked : %s (%s)" % (tid, reason))
    print("active  : %s" % (", ".join(active) if active else "none"))
    fails = [name for name, result in gates if result == "fail"]
    print("gates   : %d recorded%s" % (len(gates), "; FAILING: %s" % ", ".join(fails) if fails else ", all pass"))
    for note in notes[-5:]:
        print("note    : %s" % note)
    print("status  : %s" % ("COMPLETE" if completed else "IN PROGRESS"))
    if not completed:
        nxt, how = next_from_queue(queue, done, blocked, active)
        if nxt:
            if how == "active":
                print("next    : continue %s (already started)" % nxt)
            else:
                print("next    : start %s" % nxt)
        elif active:
            print("next    : continue the active task (%s)" % ", ".join(active))
        elif queue and set(queue) <= set(done):
            print("next    : queue exhausted — record complete or extend the queue")
        else:
            print("next    : pick the next task in todo order")
    return 0


def cmd_check(args):
    events, err = read_events(args.ledger)
    if err:
        print("LEDGER DRIFT: %s" % err)
        return 1
    failures = validate(events)
    if failures:
        print("LEDGER DRIFT (%d):" % len(failures))
        for failure in failures:
            print("  %s" % failure)
        return 1
    legacy = legacy_count(events)
    if legacy:
        print("LEDGER OK (%d events, %d legacy pre-mark lines trusted leniently)" % (len(events), legacy))
    else:
        print("LEDGER OK (%d events)" % len(events))
    return 0


def main():
    ap = argparse.ArgumentParser(description="Minimal JSONL run-state ledger for a long-run program.")
    ap.add_argument("ledger", help="ledger file, e.g. tasks/<program>-ledger.jsonl")
    sub = ap.add_subparsers(dest="cmd", required=True)
    p = sub.add_parser("anchor", help="first line: source-of-truth pointers")
    p.add_argument("--plan", required=True)
    p.add_argument("--todo", required=True)
    p.add_argument("--session", required=True)
    p.add_argument("--specs", nargs="*", default=[])
    p.add_argument("--standards", default="")
    p.add_argument("--queue", default="",
                   help="ordered task ids, space- or comma-separated (resume names the first unblocked one)")
    p = sub.add_parser("task", help="task lifecycle event")
    p.add_argument("--id", required=True)
    p.add_argument("--state", required=True, choices=TASK_STATES)
    p.add_argument("--evidence", default="")
    p.add_argument("--reason", default="")
    p = sub.add_parser("note", help="one decision/correction (<= %d chars)" % NOTE_LIMIT)
    p.add_argument("--text", required=True)
    p = sub.add_parser("gate", help="one deterministic gate result")
    p.add_argument("--name", required=True)
    p.add_argument("--result", required=True, choices=GATE_RESULTS)
    p = sub.add_parser("queue", help="set or replace the ordered task queue (latest wins)")
    p.add_argument("--ids", required=True,
                   help="ordered task ids, space- or comma-separated")
    p = sub.add_parser("complete", help="close the program")
    p.add_argument("--note", default="")
    sub.add_parser("resume", help="print the resume brief")
    sub.add_parser("check", help="validate schema + sequence, exit 1 on drift")
    args = ap.parse_args()
    args.kind = args.cmd if args.cmd in KINDS else None
    if args.cmd == "resume":
        return cmd_resume(args)
    if args.cmd == "check":
        return cmd_check(args)
    return cmd_append(args)


if __name__ == "__main__":
    sys.exit(main())

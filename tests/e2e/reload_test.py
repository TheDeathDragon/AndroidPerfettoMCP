#!/usr/bin/env python3
"""End-to-end test of supervisor hot reload.

Usage: reload_test.py <publish-dir> <trace> [--keep]
Installs into a temp prefix (scripts/install.ps1 or install.sh), drives the supervisor over stdio,
re-installs to trigger a hot swap, kills the worker to test crash recovery, and checks cleanup.
"""
import json
import os
import queue
import shutil
import subprocess
import sys
import tempfile
import threading
import time

SCRIPTS = os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..', 'scripts')
WIN = os.name == 'nt'


def install(src, prefix):
    if WIN:
        cmd = ['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
               os.path.join(SCRIPTS, 'install.ps1'), src, '-Prefix', prefix]
    else:
        cmd = ['sh', os.path.join(SCRIPTS, 'install.sh'), src, '--prefix', prefix, '--bin-dir', os.path.join(prefix, 'bin')]
    out = subprocess.run(cmd, stdout=subprocess.PIPE, stderr=subprocess.PIPE, universal_newlines=True)
    if out.returncode != 0:
        raise SystemExit('install failed:\n' + out.stdout + out.stderr)
    with open(os.path.join(prefix, 'current.txt')) as f:
        return f.read().strip()


class Client:
    def __init__(self, exe):
        self.p = subprocess.Popen([exe, '--supervise'], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, universal_newlines=True, encoding='utf-8', errors='replace')
        self.q = queue.Queue()
        self.err = []
        self.notes = []
        self.next_id = 1
        threading.Thread(target=self._read, daemon=True).start()
        threading.Thread(target=lambda: [self.err.append(l.rstrip()) for l in self.p.stderr], daemon=True).start()

    def _read(self):
        for line in self.p.stdout:
            self.q.put(json.loads(line))

    def send(self, obj):
        self.p.stdin.write(json.dumps(obj) + '\n')
        self.p.stdin.flush()

    def request(self, method, params=None, timeout=120):
        rid = self.next_id
        self.next_id += 1
        self.send({'jsonrpc': '2.0', 'id': rid, 'method': method, 'params': params or {}})
        deadline = time.time() + timeout
        while time.time() < deadline:
            try:
                msg = self.q.get(timeout=max(0.1, deadline - time.time()))
            except queue.Empty:
                break
            if msg.get('id') == rid:
                return msg
            if 'method' in msg:
                self.notes.append(msg['method'])
        raise SystemExit(f'timeout waiting for {method}\n' + '\n'.join(self.err[-30:]))

    def call(self, name, args):
        r = self.request('tools/call', {'name': name, 'arguments': args})
        if 'error' in r:
            return None, r['error']['message']
        res = r['result']
        return res, ''.join(c.get('text', '') for c in res['content'])

    def wait_note(self, method, timeout=20):
        deadline = time.time() + timeout
        while time.time() < deadline:
            if method in self.notes:
                return True
            try:
                msg = self.q.get(timeout=0.5)
                if 'method' in msg:
                    self.notes.append(msg['method'])
            except queue.Empty:
                pass
        return False


def write_current(prefix, version):
    tmp = os.path.join(prefix, 'current.txt.tmp')
    with open(tmp, 'w') as f:
        f.write(version)
    os.replace(tmp, os.path.join(prefix, 'current.txt'))


def read_log(prefix):
    try:
        with open(os.path.join(prefix, 'state', 'supervisor.log'), encoding='utf-8', errors='replace') as f:
            return f.read()
    except OSError:
        return ''


def worker_pids(prefix):
    if WIN:
        out = subprocess.run(['powershell', '-NoProfile', '-Command',
                              "Get-CimInstance Win32_Process -Filter \"Name='perfetto-mcp.exe'\" | "
                              "ForEach-Object { \"$($_.ProcessId)|$($_.ExecutablePath)\" }"],
                             stdout=subprocess.PIPE, stderr=subprocess.PIPE, universal_newlines=True).stdout
        rows = [l.split('|', 1) for l in out.splitlines() if '|' in l]
    else:
        rows = []
        for pid in os.listdir('/proc'):
            if pid.isdigit():
                try:
                    rows.append((pid, os.readlink(f'/proc/{pid}/exe')))
                except OSError:
                    pass
    versions = os.path.normcase(os.path.join(prefix, 'versions'))
    # trace_processor_shell also lives under versions/<id>/tools; count workers only.
    return [int(p) for p, path in rows if os.path.normcase(path).startswith(versions)
            and os.path.basename(path).lower().startswith('perfetto-mcp')]


def check(cond, msg):
    print(('PASS ' if cond else 'FAIL ') + msg)
    if not cond:
        check.failed = True


check.failed = False


def main():
    src, trace = os.path.abspath(sys.argv[1]), os.path.abspath(sys.argv[2])
    prefix = tempfile.mkdtemp(prefix='pm-reload-')
    try:
        v1 = install(src, prefix)
        exe = os.path.join(prefix, 'perfetto-mcp.exe' if WIN else 'perfetto-mcp')
        c = Client(exe)
        init = c.request('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {}, 'clientInfo': {'name': 'reload-test', 'version': '0'}})
        check(init['result']['capabilities']['tools']['listChanged'] is True, 'initialize advertises tools.listChanged')
        c.send({'jsonrpc': '2.0', 'method': 'notifications/initialized'})
        res, text = c.call('load_trace', {'path': trace, 'overview': False})
        check(res is not None and not res.get('isError'), f'load_trace on {v1}')
        pids_v1 = worker_pids(prefix)
        check(len(pids_v1) == 1, f'one worker running ({pids_v1})')

        time.sleep(1.1)  # version ids carry a seconds timestamp
        v2 = install(src, prefix)
        check(v2 != v1, f'reinstall produced new version {v2}')
        check(c.wait_note('notifications/tools/list_changed'), 'client received tools/list_changed after update')
        time.sleep(1.0)
        pids_v2 = worker_pids(prefix)
        check(len(pids_v2) == 1 and pids_v2 != pids_v1, f'worker replaced ({pids_v1} -> {pids_v2})')
        res, text = c.call('list_traces', {})
        check('"t1"' in text, 'trace handle t1 restored after update')
        res, text = c.call('execute_sql', {'sql': 'SELECT COUNT(*) AS n FROM slice', 'trace_id': 't1'})
        check(res is not None and not res.get('isError'), 'query on restored t1 reloads lazily: ' + text[:80])

        # A version whose worker dies at startup: the old worker keeps serving, nothing leaks, no retry storm.
        broken = '0.0.0-broken'
        bdir = os.path.join(prefix, 'versions', broken)
        os.makedirs(bdir)
        bexe = os.path.join(bdir, 'perfetto-mcp.exe' if WIN else 'perfetto-mcp')
        if WIN:
            shutil.copy(os.path.join(os.environ.get('SystemRoot', r'C:\Windows'), 'System32', 'whoami.exe'), bexe)
        else:
            with open(bexe, 'w') as f:
                f.write('#!/bin/sh\nexit 3\n')
            os.chmod(bexe, 0o755)
        c.notes.clear()
        write_current(prefix, broken)
        time.sleep(6)
        log = read_log(prefix)
        check(log.count(f'starting worker {broken} failed') == 1, 'broken version fails once (backoff), logged to state/supervisor.log')
        check(worker_pids(prefix) == pids_v2, f'broken version leaves only the old worker ({worker_pids(prefix)})')
        res, text = c.call('execute_sql', {'sql': 'SELECT 2 AS ok', 'trace_id': 't1'})
        check(res is not None and not res.get('isError'), 'old worker still serves after a failed update')
        check('notifications/tools/list_changed' not in c.notes, 'no list_changed for a failed update')
        write_current(prefix, v2)

        c.notes.clear()
        for pid in pids_v2:
            if WIN:
                subprocess.run(['taskkill', '/F', '/PID', str(pid)], stdout=subprocess.PIPE, stderr=subprocess.PIPE)
            else:
                os.kill(pid, 9)
        check(c.wait_note('notifications/tools/list_changed'), 'worker crash -> restarted and client notified')
        res, text = c.call('execute_sql', {'sql': 'SELECT 1 AS ok', 'trace_id': 't1'})
        check(res is not None and not res.get('isError'), 'call after crash works: ' + text[:60])

        c.p.stdin.close()
        c.p.wait(30)
        time.sleep(2)
        check(c.p.returncode == 0, 'supervisor exits cleanly on stdin EOF')
        check(worker_pids(prefix) == [], 'no worker left behind')
        print('\n'.join('  | ' + l for l in c.err if 'supervisor' in l))
    finally:
        if '--keep' not in sys.argv:
            shutil.rmtree(prefix, ignore_errors=True)
    sys.exit(1 if check.failed else 0)


if __name__ == '__main__':
    main()

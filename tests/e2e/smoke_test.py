#!/usr/bin/env python3
"""End-to-end smoke test: drives perfetto-mcp over stdio JSON-RPC.

Usage: smoke_test.py <server-exe> <trace> [<trace2>] [--calls calls.json] [--show N]
Prints each tool call with latency and the first N chars of the response.
"""
import json
import subprocess
import sys
import threading
import time


class Client:
    def __init__(self, exe):
        self.p = subprocess.Popen([exe], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                  stderr=subprocess.PIPE, universal_newlines=True, encoding='utf-8', errors='replace', bufsize=1)
        self.next_id = 1
        self.stderr_lines = []
        threading.Thread(target=self._drain, daemon=True).start()

    def _drain(self):
        for line in self.p.stderr:
            self.stderr_lines.append(line.rstrip())

    def request(self, method, params=None, progress=False):
        rid = self.next_id
        self.next_id += 1
        msg = {'jsonrpc': '2.0', 'id': rid, 'method': method, 'params': params or {}}
        if progress:
            msg['params'].setdefault('_meta', {})['progressToken'] = f'p{rid}'
        self.p.stdin.write(json.dumps(msg, ensure_ascii=False) + '\n')
        self.p.stdin.flush()
        notes = 0
        while True:
            line = self.p.stdout.readline()
            if not line:
                raise RuntimeError('server closed stdout\n' + '\n'.join(self.stderr_lines[-30:]))
            obj = json.loads(line)
            if obj.get('method') == 'notifications/progress':
                notes += 1
                continue
            if obj.get('id') == rid:
                return obj, notes

    def call(self, name, args, show, progress=False):
        t = time.time()
        resp, notes = self.request('tools/call', {'name': name, 'arguments': args}, progress)
        dt = time.time() - t
        res = resp.get('result', {})
        text = ''.join(c.get('text', '') for c in res.get('content', []))
        flag = 'ERR' if res.get('isError') or 'error' in resp else 'ok '
        extra = f' progress_notes={notes}' if progress else ''
        print(f'[{flag}] {name} {json.dumps(args, ensure_ascii=False)} {dt:.2f}s{extra}')
        print('      ' + (text or json.dumps(resp, ensure_ascii=False))[:show].replace('\n', '\n      '))
        return res, text

    def close(self):
        self.p.stdin.close()
        try:
            self.p.wait(15)
        except subprocess.TimeoutExpired:
            self.p.kill()


def main():
    args = [a for a in sys.argv[1:]]
    show = 600
    if '--show' in args:
        i = args.index('--show')
        show = int(args[i + 1])
        del args[i:i + 2]
    calls = None
    if '--calls' in args:
        i = args.index('--calls')
        with open(args[i + 1], encoding='utf-8') as f:
            calls = json.load(f)
        del args[i:i + 2]
    exe, traces = args[0], args[1:]

    c = Client(exe)
    init, _ = c.request('initialize', {'protocolVersion': '2025-06-18', 'capabilities': {},
                                       'clientInfo': {'name': 'smoke', 'version': '0'}})
    print('initialize ->', init['result']['serverInfo'], init['result']['protocolVersion'])
    tools, _ = c.request('tools/list')
    print('tools:', [t['name'] for t in tools['result']['tools']])

    for tr in traces:
        c.call('load_trace', {'path': tr}, show * 3, progress=True)

    if calls is None:
        calls = [
            ['list_processes', {'limit': 5}],
            ['android_startups', {}],
            ['cpu_top_threads', {'limit': 5}],
            ['android_jank_frames', {'limit': 5}],
            ['android_binder_txns', {'limit': 5}],
            ['slice_hotspots', {'limit': 5}],
            ['list_stdlib_modules', {'filter': 'startup'}],
            ['list_tables', {'pattern': '*frame*'}],
            ['list_table_structure', {'table_name': 'android_startups'}],
            ['execute_sql', {'sql': 'SELECT COUNT(*) AS n FROM slice'}],
            ['execute_sql', {'sql': 'SELECT * FROM android_startups'}],
            ['execute_sql', {'sql': 'SELECT bogus_col FROM thread'}],
            ['execute_sql', {'sql': 'SELECT 1', 'bogus_param': 1}],
            ['list_traces', {}],
        ]
    for name, a in calls:
        c.call(name, a, show)
    c.close()
    errs = [l for l in c.stderr_lines if ' ERROR ' in l or ' WARN ' in l]
    print('\nserver warnings/errors:', *(errs[-20:] or ['none']), sep='\n  ')


if __name__ == '__main__':
    main()

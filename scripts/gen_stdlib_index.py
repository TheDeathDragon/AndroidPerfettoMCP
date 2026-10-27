#!/usr/bin/env python3
# Usage: gen_stdlib_index.py <perfetto>/src/trace_processor/perfetto_sql/stdlib > stdlib_index.tsv
# Emits "<module>\t<public objects comma-separated>" for every PerfettoSQL stdlib module.
import os
import re
import sys

root = sys.argv[1]
pat = re.compile(r'^\s*CREATE\s+PERFETTO\s+(TABLE|VIEW|FUNCTION|MACRO)\s+([A-Za-z0-9_]+)', re.I | re.M)
out = []
for d, _, files in os.walk(root):
    for f in files:
        if not f.endswith('.sql'):
            continue
        p = os.path.join(d, f)
        mod = os.path.relpath(p, root)[:-4].replace(os.sep, '.')
        if mod.startswith('_') or '._' in mod:
            continue
        with open(p, encoding='utf-8') as fh:
            names = {n for _, n in pat.findall(fh.read()) if not n.startswith('_')}
        out.append((mod, sorted(names)))
for mod, names in sorted(out):
    sys.stdout.write(mod + '\t' + ','.join(names) + '\n')

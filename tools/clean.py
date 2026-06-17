#!/usr/bin/env python3
"""Delete the contents of a directory (used instead of `rm -rf` so the shell
doesn't prompt for confirmation). Usage: python tools/clean.py <dir>"""
import os, shutil, sys

def clean(d):
    if not os.path.isdir(d):
        print(f"(nothing to clean: {d})"); return
    for name in os.listdir(d):
        p = os.path.join(d, name)
        try:
            if os.path.isdir(p) and not os.path.islink(p):
                shutil.rmtree(p)
            else:
                os.remove(p)
        except Exception as e:
            print(f"skip {p}: {e}")
    print(f"cleaned {d}")

if __name__ == "__main__":
    for d in sys.argv[1:]:
        clean(d)

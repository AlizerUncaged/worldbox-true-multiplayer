"""Stamp the deployed mod.json with the build's date version.

Called from WorldboxMP.Client.csproj (DeployToMods and ObfuscateAndPublish).
Kept as a file on purpose: an inline python -c goes through cmd.exe, which
eats characters like ^ inside regexes and silently breaks the replacement.

Usage: python stamp_version.py <path-to-mod.json> <version>
"""
import io
import re
import sys

path, version = sys.argv[1], sys.argv[2]
s = io.open(path, encoding="utf-8").read()
out, n = re.subn(r'"version"\s*:\s*"[^"]*"', '"version": "%s"' % version, s)
if n != 1:
    sys.exit("stamp_version: expected exactly 1 version field in %s, found %d" % (path, n))
io.open(path, "w", encoding="utf-8").write(out)
print("stamp_version: %s -> %s" % (path, version))

"""Verify frozen source after builds; generated dependencies/output are not source inputs."""
import hashlib
import json
from pathlib import Path
import sys
import fnmatch

root = Path(sys.argv[1]).resolve() if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent
record = json.loads((root / 'CANDIDATE.json').read_text(encoding='utf-8'))
bad = [row['path'] for row in record['files'] if not (root / row['path']).is_file()
       or hashlib.sha256((root / row['path']).read_bytes()).hexdigest() != row['sha256']]
expected = {row['path'] for row in record['files']} | {'CANDIDATE.json'}
generated = {'bin', 'obj', 'target', 'node_modules', '.git', '.vs', 'AppPackages'}
extra = [p.relative_to(root).as_posix() for p in root.rglob('*') if p.is_file()
         and not any(part in generated for part in p.relative_to(root).parts)
         and not fnmatch.fnmatch(p.relative_to(root).as_posix(), 'SidecarApp/dist/*')
         and p.relative_to(root).as_posix() != 'SidecarRuntime/node.exe'
         and p.relative_to(root).as_posix() not in expected]
fingerprint = hashlib.sha256(json.dumps(record['files'], separators=(',', ':')).encode()).hexdigest()
if bad or extra or fingerprint != record['sourceSha256']:
    raise SystemExit('Frozen source changed: ' + ', '.join(bad + extra))
runtime = root / 'SidecarRuntime/node.exe'
if runtime.exists():
    pinned = json.loads((root / 'release/dependencies.json').read_text(encoding='utf-8'))['node']['sha256']
    if hashlib.sha256(runtime.read_bytes()).hexdigest() != pinned:
        raise SystemExit('Bundled runtime differs from the dependency lock.')
print('Verified source fingerprint: ' + fingerprint)

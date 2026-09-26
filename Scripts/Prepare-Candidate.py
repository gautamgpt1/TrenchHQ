"""Freeze reviewed source without touching the development tree or its Git index."""
import argparse
import fnmatch
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import xml.etree.ElementTree as ET

parser = argparse.ArgumentParser()
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
output = args.output.resolve()
if output == root or output.is_relative_to(root) or root.is_relative_to(output):
    raise SystemExit('Candidate must be a separate new directory outside the development checkout.')
if output.exists():
    raise SystemExit('Candidate destination already exists; choose a new directory.')
policy = json.loads((root / 'release/source-policy.json').read_text(encoding='utf-8'))
files = subprocess.check_output(['rg', '--files', '--hidden', '--no-ignore', '-g', '!.git/**', '-g', '!.vs/**'],
                                cwd=root, text=True, encoding='utf-8').splitlines()
selected, excluded = [], []
for relative in sorted(p.replace('\\', '/') for p in files):
    # Root-only globs must not accidentally include files in ignored directory trees.
    included = any(fnmatch.fnmatchcase(relative, pat) and ('/' in pat or '/' not in relative)
                   for pat in policy['include'])
    denied = any(fnmatch.fnmatchcase(relative, pat) for pat in policy['exclude'])
    (selected if included and not denied else excluded).append(relative)
output.mkdir(parents=True)
manifest = []
for relative in selected:
    source = root / relative
    if source.is_symlink() or not source.resolve().is_relative_to(root):
        raise SystemExit('Candidate contains a link outside its source tree: ' + relative)
    destination = output / relative
    destination.parent.mkdir(parents=True, exist_ok=True)
    shutil.copy2(source, destination)
    manifest.append({'path': relative, 'sha256': hashlib.sha256(destination.read_bytes()).hexdigest()})
fingerprint = hashlib.sha256(json.dumps(manifest, separators=(',', ':')).encode()).hexdigest()
package = ET.parse(output / 'Package.appxmanifest').getroot()
identity = next(e.attrib for e in package if e.tag.endswith('}Identity'))
record = {'schema': 1, 'sourceSha256': fingerprint, 'identity': identity, 'files': manifest,
          'head': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
          'dirtyDiffSha256': hashlib.sha256(subprocess.check_output(['git', 'diff', '--binary'], cwd=root)).hexdigest(),
          'indexDiffSha256': hashlib.sha256(subprocess.check_output(['git', 'diff', '--cached', '--binary'], cwd=root)).hexdigest()}
(output / 'CANDIDATE.json').write_text(json.dumps(record, indent=2), encoding='utf-8')
# Keep this development inventory outside the public source snapshot.
(output.parent / (output.name + '-publication-map.json')).write_text(json.dumps(
    {'included': selected, 'omittedAndPreserved': excluded, 'policy': policy['policy']}, indent=2), encoding='utf-8')
print(json.dumps({'sourceSha256': fingerprint, 'files': len(manifest), 'version': identity['Version']}))

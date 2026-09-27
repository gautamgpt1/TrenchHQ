"""Inventory restored locked dependencies and retain their actual license/notice files."""
import hashlib
import json
from pathlib import Path
import re
import shutil
import subprocess
import xml.etree.ElementTree as ET

root = Path(__file__).resolve().parent.parent
rows = []

def record(ecosystem, name, version, license_name, folder, build_only=False):
    copied = []
    # Retain license files at package root and conventional license directories.
    candidates = list(folder.glob('*'))
    for directory in ('licenses', 'license', 'LICENSES'):
        if (folder / directory).is_dir():
            candidates.extend((folder / directory).rglob('*'))
    for source in sorted(set(candidates)):
        if not source.is_file() or not re.search(r'license|licence|notice|copying|copyright', source.name, re.I):
            continue
        if source.suffix.lower() in ('.dll', '.exe', '.pdb'):
            continue
        relative = Path('ThirdParty/Licenses/Dependencies') / ecosystem / (name.replace('/', '_') + '-' + version) / source.relative_to(folder)
        destination = root / relative
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(source, destination)
        copied.append({'path': relative.as_posix(), 'sha256': hashlib.sha256(source.read_bytes()).hexdigest()})
    rows.append({'ecosystem': ecosystem, 'name': name, 'version': version,
                 'license': license_name, 'buildOnly': build_only, 'notices': copied})

npm = json.loads((root / 'src/MarketSidecar/package-lock.json').read_text(encoding='utf-8'))
for relative, package in npm['packages'].items():
    if relative and (root / 'src/MarketSidecar' / relative).is_dir():
        name = relative.split('node_modules/')[-1]
        record('npm', name, package['version'], package.get('license', 'REVIEW REQUIRED'),
               root / 'src/MarketSidecar' / relative, package.get('dev', False))

cargo = json.loads(subprocess.check_output(['cargo', '+1.88.0', 'metadata', '--locked', '--format-version', '1'],
                                         cwd=root / 'src/OnChainEngine', text=True, encoding='utf-8'))
for package in cargo['packages']:
    if package['source']:
        record('cargo', package['name'], package['version'], package['license'] or 'REVIEW REQUIRED',
               Path(package['manifest_path']).parent)

seen = set()
for assets in [root / 'src/TrenchHQ.App/obj/project.assets.json', root / 'src/TrenchHQ.Infrastructure/obj/project.assets.json', root / 'src/TrenchHQ.OnChain.Yellowstone/obj/project.assets.json']:
    data = json.loads(assets.read_text(encoding='utf-8'))
    cache = Path(next(iter(data['packageFolders'])))
    for key, package in data['libraries'].items():
        if package['type'] != 'package' or key in seen:
            continue
        seen.add(key)
        name, version = key.rsplit('/', 1)
        folder = cache / package['path']
        specification = ET.parse(next(folder.glob('*.nuspec'))).getroot()
        license_element = next((e for e in specification.iter() if e.tag.endswith('}license') or e.tag == 'license'), None)
        license_url = next((e.text for e in specification.iter() if e.tag.endswith('}licenseUrl') or e.tag == 'licenseUrl'), None)
        license_name = license_element.text if license_element is not None else license_url or 'REVIEW REQUIRED'
        record('nuget', name, version, license_name, folder,
               name in ('Grpc.Tools', 'Microsoft.Windows.SDK.BuildTools'))
# Packages that declare upstream licenses but do not contain their text at package root.
supplemental = {
    'Google.Protobuf': 'ThirdParty/Licenses/GoogleProtobuf-BSD-3-Clause.txt',
    'Grpc.Core.Api': 'ThirdParty/Yellowstone/LICENSE_APACHE2',
    'Grpc.Net.Client': 'ThirdParty/Yellowstone/LICENSE_APACHE2',
    'Grpc.Net.Common': 'ThirdParty/Yellowstone/LICENSE_APACHE2',
    'Grpc.Tools': 'ThirdParty/Yellowstone/LICENSE_APACHE2',
    'Microsoft.Graphics.Win2D': 'ThirdParty/Licenses/Win2D-LICENSE.txt',
    '@esbuild/win32-x64': 'src/MarketSidecar/node_modules/esbuild/LICENSE.md'
}
for row in rows:
    relative = supplemental.get(row['name'])
    if not row['notices'] and relative and (root / relative).is_file():
        destination = Path('ThirdParty/Licenses/Dependencies') / row['ecosystem'] / (row['name'].replace('/', '_') + '-' + row['version']) / 'LICENSE.txt'
        (root / destination).parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(root / relative, root / destination)
        row['notices'].append({'path': destination.as_posix(), 'sha256': hashlib.sha256((root / relative).read_bytes()).hexdigest()})
rows.sort(key=lambda row: (row['ecosystem'], row['name'], row['version']))
(root / 'ThirdParty/DEPENDENCIES.json').write_text(json.dumps(rows, indent=2), encoding='utf-8')
print(json.dumps({'packages': len(rows), 'missingLicenseMetadata': [r['name'] for r in rows if r['license'] == 'REVIEW REQUIRED'],
                  'withoutNoticeFiles': [r['name'] for r in rows if not r['notices']]}))

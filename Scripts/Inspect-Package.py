"""Inspect a built MSIX without installing or running it."""
import hashlib
import json
from pathlib import Path
import struct
import sys
from urllib.parse import unquote
import xml.etree.ElementTree as ET
import zipfile

package = Path(sys.argv[1]).resolve()
output = Path(sys.argv[2]).resolve()
root = Path(__file__).resolve().parent.parent
inputs = json.loads((root / 'release/dependencies.json').read_text(encoding='utf-8'))
source_manifest = ET.parse(root / 'Package.appxmanifest').getroot()
expected_identity = next(e.attrib for e in source_manifest if e.tag.endswith('}Identity'))
with zipfile.ZipFile(package) as archive:
    # MSIX stores scoped npm notice directories such as @noble as URI-escaped ZIP names.
    names = {unquote(name): name for name in archive.namelist()}
    def read(name):
        return archive.read(names[name])
    def exactly_one(predicate):
        matches = [name for name in names if predicate(name)]
        if len(matches) != 1:
            raise SystemExit('Expected exactly one payload component: ' + str(matches))
        return matches[0]
    native = [exactly_one(lambda n: n == 'OnChainEngine/trenchhq-onchain-engine.exe'),
              exactly_one(lambda n: n.startswith('TrenchHQ.WindowPin.') and n.endswith('.dll')),
              exactly_one(lambda n: n == 'SidecarRuntime/node.exe')]
    machines = {}
    for name in native:
        data = read(name)
        pe_offset = struct.unpack_from('<I', data, 0x3c)[0]
        machine = struct.unpack_from('<H', data, pe_offset + 4)[0]
        if data[:2] != b'MZ' or data[pe_offset:pe_offset + 4] != b'PE\0\0' or machine != 0x8664:
            raise SystemExit('Native payload is not x64 PE: ' + name)
        machines[name] = {'machine': 'x64', 'sha256': hashlib.sha256(data).hexdigest()}
    for required in ('LICENSE', 'THIRD_PARTY_NOTICES.md', 'Privacy.txt', 'ThirdParty/DEPENDENCIES.json',
                     'ThirdParty/Licenses/Node-LICENSE.txt', 'SidecarApp/dist/sidecar.bundle.cjs'):
        if required not in names:
            raise SystemExit('Required payload missing: ' + required)
    forbidden = [n for n in names if any(part in n.lower() for part in
                 ('onchain-secrets', 'social-secrets', 'websiteprofiles', 'websitebrowser', '.pfx', '.p12',
                  'assets/protocols/pump.svg', 'assets/protocols/meteora.svg'))]
    if forbidden:
        raise SystemExit('Unexpected private or unapproved payload: ' + str(forbidden))
    manifest = ET.fromstring(read('AppxManifest.xml'))
    identity = next(e.attrib for e in manifest if e.tag.endswith('}Identity'))
    if any(identity.get(key) != expected_identity.get(key) for key in ('Name', 'Publisher', 'Version')):
        raise SystemExit('Package identity differs from the candidate manifest.')
    if identity.get('ProcessorArchitecture') != 'x64':
        raise SystemExit('Package architecture is not x64.')
    inventory = read('ThirdParty/DEPENDENCIES.json')
    if inventory != (root / 'ThirdParty/DEPENDENCIES.json').read_bytes():
        raise SystemExit('Packaged dependency inventory differs from the candidate.')
    notice_count = 0
    for dependency in json.loads(inventory):
        for notice in dependency['notices']:
            if hashlib.sha256(read(notice['path'])).hexdigest() != notice['sha256']:
                raise SystemExit('Packaged notice hash mismatch: ' + notice['path'])
            notice_count += 1
    for path, expected in (('SidecarRuntime/node.exe', inputs['node']['sha256']),
                           ('ThirdParty/Licenses/Node-LICENSE.txt', inputs['node']['licenseSha256'])):
        if hashlib.sha256(read(path)).hexdigest() != expected:
            raise SystemExit('Pinned Node payload hash mismatch: ' + path)
    report = {'artifact': package.name, 'size': package.stat().st_size,
              'sha256': hashlib.sha256(package.read_bytes()).hexdigest(), 'identity': identity,
              'native': machines, 'licenseFiles': len([n for n in names if n.startswith('ThirdParty/Licenses/')]),
              'verifiedDependencyNotices': notice_count,
              'signedContainer': 'AppxSignature.p7x' in names,
              'files': [{'path': n, 'sha256': hashlib.sha256(read(n)).hexdigest()} for n in names]}
output.write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps({k: v for k, v in report.items() if k != 'files'}, indent=2))

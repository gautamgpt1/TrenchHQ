"""Inspect a built MSIX without installing or running it."""
import hashlib
import ctypes
import json
import os
from pathlib import Path
import struct
import subprocess
import sys
import tempfile
from urllib.parse import unquote
import xml.etree.ElementTree as ET
import zipfile

package = Path(sys.argv[1]).resolve()
output = Path(sys.argv[2]).resolve()
root = Path(__file__).resolve().parent.parent
inputs = json.loads((root / 'release/dependencies.json').read_text(encoding='utf-8'))
source_manifest = ET.parse(root / 'src/TrenchHQ.App/Package.appxmanifest').getroot()
expected_identity = next(e.attrib for e in source_manifest if e.tag.endswith('}Identity'))

# Recreate the Windows resources from the committed master, independently of
# the build output, so missing/stale generated assets cannot pass inspection.
with tempfile.TemporaryDirectory(prefix='TrenchHQ-brand-check-') as temporary:
    subprocess.run(['powershell.exe', '-NoProfile', '-File',
                    str(root / 'Scripts/Build-BrandAssets.ps1'), '-OutputPath', temporary],
                   check=True, capture_output=True)
    generated_brand_assets = {'Assets/' + p.name: p.read_bytes()
                              for p in Path(temporary).iterdir() if p.is_file()}


def binary_metadata(data, suffix):
    """Read Windows version resources without loading executable code."""
    if os.name != 'nt':
        raise SystemExit('Package binary metadata verification requires Windows.')
    api = ctypes.WinDLL('version', use_last_error=True)
    api.GetFileVersionInfoSizeW.argtypes = [ctypes.c_wchar_p, ctypes.c_void_p]
    api.GetFileVersionInfoW.argtypes = [ctypes.c_wchar_p, ctypes.c_uint, ctypes.c_uint, ctypes.c_void_p]
    api.VerQueryValueW.argtypes = [ctypes.c_void_p, ctypes.c_wchar_p,
                                 ctypes.POINTER(ctypes.c_void_p), ctypes.POINTER(ctypes.c_uint)]
    with tempfile.TemporaryDirectory(prefix='TrenchHQ-metadata-') as temporary:
        path = Path(temporary) / ('payload' + suffix)
        path.write_bytes(data)
        size = api.GetFileVersionInfoSizeW(str(path), None)
        buffer = ctypes.create_string_buffer(size)
        if not size or not api.GetFileVersionInfoW(str(path), 0, size, buffer):
            raise SystemExit('Owned binary is missing Windows version information.')
        pointer, length = ctypes.c_void_p(), ctypes.c_uint()
        if not api.VerQueryValueW(buffer, '\\VarFileInfo\\Translation', ctypes.byref(pointer), ctypes.byref(length)) or length.value < 4:
            raise SystemExit('Owned binary is missing its version translation.')
        language, codepage = struct.unpack('<HH', ctypes.string_at(pointer, 4))
        values = {}
        for key in ('ProductName', 'FileVersion', 'ProductVersion'):
            query = f'\\StringFileInfo\\{language:04x}{codepage:04x}\\{key}'
            if not api.VerQueryValueW(buffer, query, ctypes.byref(pointer), ctypes.byref(length)):
                raise SystemExit('Owned binary is missing ' + key)
            values[key] = ctypes.wstring_at(pointer)
        return values


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
    owned = ['TrenchHQ.exe', 'TrenchHQ.dll', 'TrenchHQ.OnChain.Yellowstone.dll', 'TrenchHQ.Core.dll', 'TrenchHQ.Infrastructure.dll', *native[:2]]
    unexpected_code = [n for n in names if
                       (n.startswith('TrenchHQ') and n.endswith(('.dll', '.exe')) and n not in owned)
                       or Path(n).name.lower().startswith(('xunit.', 'testhost.', 'microsoft.testing.', 'microsoft.testplatform.'))]
    if unexpected_code:
        raise SystemExit('Unexpected test/tool code in application payload: ' + str(unexpected_code))
    versions = {}
    for name in owned:
        metadata = binary_metadata(read(name), Path(name).suffix)
        if metadata != {'ProductName': 'TrenchHQ', 'FileVersion': expected_identity['Version'],
                        'ProductVersion': expected_identity['Version']}:
            raise SystemExit('Owned binary product/version mismatch: ' + name + ': ' + str(metadata))
        versions[name] = metadata
    for required in ('LICENSE', 'THIRD_PARTY_NOTICES.md', 'Privacy.txt', 'ThirdParty/DEPENDENCIES.json',
                     'ThirdParty/Licenses/Node-LICENSE.txt', 'SidecarApp/dist/sidecar.bundle.cjs'):
        if required not in names:
            raise SystemExit('Required payload missing: ' + required)
    source_assets = {'LICENSE': root / 'LICENSE',
                     'THIRD_PARTY_NOTICES.md': root / 'THIRD_PARTY_NOTICES.md',
                     'Privacy.txt': root / 'docs/public/privacy.txt'}
    source_assets.update({'Assets/' + p.name: p for p in (root / 'src/TrenchHQ.App/Assets').iterdir()
                          if p.is_file() and p.suffix in ('.png', '.svg', '.ico')})
    for name, source in source_assets.items():
        if name not in names or read(name) != source.read_bytes():
            raise SystemExit('Packaged policy, notice or brand asset differs from source: ' + name)
    for name, expected in generated_brand_assets.items():
        if name not in names or read(name) != expected:
            raise SystemExit('Packaged generated brand asset differs from the master: ' + name)
    unexpected_brand = [n for n in names if Path(n).parent.as_posix() == 'Assets'
                        and Path(n).suffix in ('.png', '.svg', '.ico')
                        and n not in source_assets and n not in generated_brand_assets]
    if unexpected_brand:
        raise SystemExit('Unexpected redundant brand assets: ' + str(unexpected_brand))
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
              'native': machines, 'ownedBinaryVersions': versions,
              'licenseFiles': len([n for n in names if n.startswith('ThirdParty/Licenses/')]),
              'verifiedDependencyNotices': notice_count,
              'verifiedPolicyAndBrandFiles': len(source_assets) + len(generated_brand_assets),
              'signedContainer': 'AppxSignature.p7x' in names,
              'files': [{'path': n, 'sha256': hashlib.sha256(read(n)).hexdigest()} for n in names]}
output.write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps({k: v for k, v in report.items() if k != 'files'}, indent=2))

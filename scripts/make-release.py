"""Assemble a new local release from the checked runtime inventory; no private data."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import re
import shutil

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser()
parser.add_argument('--name', required=True)
args = parser.parse_args()
if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]{0,79}', args.name):
    raise ValueError('Invalid release directory name')
destination = ROOT / 'release' / args.name
if destination.exists():
    raise FileExistsError('Never overwrite an existing release: ' + str(destination))
for ancestor in [ROOT / 'release', destination]:
    if ancestor.is_symlink() or ancestor.is_junction():
        raise ValueError('Linked release path')
manifest_path = ROOT / 'docs/runtime-distribution.json'
inventory = json.loads(manifest_path.read_text(encoding='utf-8'))
if inventory['missingRequiredPaths'] or not inventory['desktopPublishComplete']:
    raise ValueError('Incomplete runtime inventory')
destination.mkdir(parents=True)

def sha(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()

def copy(row):
    relative = Path(row['path'])
    if relative.is_absolute() or relative.drive or '..' in relative.parts:
        raise ValueError('Unsafe inventory path')
    source = ROOT / relative
    if source.is_symlink() or source.is_junction() or any(p.is_junction() or p.is_symlink() for p in source.parents if p.is_relative_to(ROOT)):
        raise ValueError('Linked input')
    target = destination / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, target)
    if target.stat().st_size != row['bytes'] or sha(target) != row['sha256']:
        raise ValueError('Inventory changed or copy mismatch: ' + row['path'])
    return {'path': row['path'], 'bytes': row['bytes'], 'sha256': row['sha256'], 'category': row['category']}

rows = []
with ThreadPoolExecutor(max_workers=4) as pool:
    for row in pool.map(copy, inventory['files']):
        rows.append(row)
        if len(rows) % 5000 == 0:
            print('Verified copied files: ' + str(len(rows)), flush=True)

extras = {
    'docs/runtime-distribution.json': manifest_path,
    'docs/runtime-sbom.cdx.json': ROOT / 'docs/runtime-sbom.cdx.json',
    'README.md': ROOT / 'docs/release-guide.md',
}
for relative, source in extras.items():
    target = destination / relative
    target.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(source, target)
    rows.append({'path': relative, 'bytes': target.stat().st_size, 'sha256': sha(target), 'category': 'release-documentation'})

# Source snapshot identity is a content hash of the final corresponding source
# entries, not a Git revision (this workspace has no Git metadata).
source_rows = [r for r in rows if r['category'] in {'corresponding-source', 'configuration-template', 'runtime-helper', 'amadeus-source-closure', 'embedding-worker'} or r['path'].endswith(('.py', '.mjs')) and '/node_modules/' not in r['path']]
source_identity = hashlib.sha256(json.dumps(sorted(source_rows, key=lambda r:r['path']), sort_keys=True).encode()).hexdigest()
release = {
    'schemaVersion': 1, 'version': args.name, 'createdUtc': datetime.now(timezone.utc).isoformat(),
    'sourceIdentitySha256': source_identity, 'runtimeInventorySha256': sha(manifest_path),
    'entryPoint': 'Start-ATRI.cmd', 'executable': 'app/atri_agent.exe',
    'dataRoot': 'data (relative to this release; initialized on first use)',
    'privateConfigurationCopied': False, 'userMemoryCopied': False,
    'prerequisites': inventory['externalPrerequisites'],
    'publicRedistributionRightsVerified': False,
    'files': sorted(rows, key=lambda r:r['path']),
    'totalFilesWithoutThisManifest': len(rows), 'totalBytesWithoutThisManifest': sum(r['bytes'] for r in rows),
}
(destination / 'release-manifest.json').write_text(json.dumps(release, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps({'release': str(destination), 'files': len(rows)+1, 'bytes': sum(p.stat().st_size for p in destination.rglob('*') if p.is_file()), 'sourceIdentity': source_identity}, ensure_ascii=False), flush=True)

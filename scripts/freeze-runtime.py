"""Read only, allowlisted local runtime inventory and CycloneDX dependency SBOM.

Does not copy/delete resources, import installed distributions, read private user
state or scan the original training runtime. Re-run after publishing final app/.
"""
from __future__ import annotations
import argparse
from collections import Counter
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import re
import sys
from urllib.parse import quote
import uuid

ROOT=Path(__file__).resolve().parents[1]
AMAEDEUS_MODULES=['agent_host/__init__.py','agent_host/browser_request_contract.py','agent_host/pi_rpc.py','agent_host/provider_contract.py','agent_host/provider_outcome.py',
    'agent_host/provider_types.py','agent_host/work_ledger_store.py','agent_host/work_ledger_types.py','vn_player/script_index.py',
    'vn_player/text.py','server/__init__.py','server/auip_contract.py','server/local_git.py','server/unified_diff.py','server/work_artifact_registry.py']
EXCLUDED_PARTS={'__pycache__','tests','test','node_modules/.cache','.cache','bin','obj','core-probe','visual-probe'}
EXCLUDED_SUFFIXES={'.pyc','.pyo','.whl','.7z','.tar','.tgz','.gz','.rar','.pdb'}
PRIVATE_CATEGORIES={'voice-model','voice-snapshot','voice-reference','game-asset','maintenance-asset','game-script','game-audio-source','game-audio-pcm'}
GAME_AUDIO_IDS={'title','chat','chat-sunshine','chat-twilight','chat-calm','hover','select','back','settings','display-settings','sound-settings','shortcuts','memory','resume','reset-settings','exit-confirm','factory-reset-confirm'}


def selected_game_audio_clips(root=ROOT):
    """Only the explicitly audited clips, never the whole original systemVoice tree."""
    manifest=root/'assets/derived/game-audio.json'
    if not manifest.is_file():return []
    data=json.loads(manifest.read_text(encoding='utf8'))
    if data.get('schemaVersion')!=1 or set(data.get('clips',{}))!=GAME_AUDIO_IDS:
        raise ValueError('Game audio selection changed; audit the explicit clip allowlist before freezing')
    clips=[];selected_paths=set()
    for identifier,clip in sorted(data['clips'].items()):
        for key,prefix,suffixes in [('source','assets/systemVoice/',{'.ogg','.opus'}),('cache','data/cache/game-audio/',{'.wav'})]:
            value=clip.get(key)
            if (not isinstance(value,str) or not value.startswith(prefix) or '\\' in value or
                any(part in {'.','..',''} for part in value.split('/')) or Path(value).suffix.lower() not in suffixes):
                raise ValueError('Game audio input outside selected resource scope: '+identifier+'/'+key)
            if value in selected_paths:raise ValueError('Duplicate selected game audio resource: '+value)
            selected_paths.add(value)
            expected=clip.get(key+'Sha256')
            if not isinstance(expected,str) or not re.fullmatch('[0-9a-fA-F]{64}',expected):
                raise ValueError('Missing selected game audio hash: '+identifier+'/'+key)
        kind=clip.get('kind')
        if (kind not in {'bgm','effect','prompt'} or clip.get('sampleRate')!=44100 or clip.get('bits')!=16 or
            clip.get('channels')!=(2 if kind=='bgm' else 1)):
            raise ValueError('Unexpected deployed game audio PCM format: '+identifier)
        clips.append({'id':identifier,'kind':kind,**{key:clip[key] for key in ['source','sourceSha256','cache','cacheSha256']}})
    return clips


def relative(path):return path.relative_to(ROOT).as_posix()
def digest(path):
    before=path.stat()
    with path.open('rb') as file:value=hashlib.file_digest(file,'sha256').hexdigest()
    after=path.stat()
    if (before.st_size,before.st_mtime_ns)!=(after.st_size,after.st_mtime_ns):raise RuntimeError('File changed during freeze: '+relative(path))
    return value
def linked(path):return path.is_symlink() or (hasattr(path,'is_junction') and path.is_junction())
def children(base):
    for folder,dirs,files in os.walk(base,followlinks=False):
        dirs[:]=sorted(name for name in dirs if name not in EXCLUDED_PARTS and not linked(Path(folder)/name))
        for name in sorted(files):
            path=Path(folder)/name
            if linked(path) or path.suffix.lower() in EXCLUDED_SUFFIXES or name in {'.env','auth.json','credentials.json'} or name.endswith('.dpapi'):continue
            if name.startswith('verify') and path.suffix=='.py':continue
            yield path
def notice_files(paths):
    values=[]
    for path in sorted(set(paths)):
        if path.is_file() and not linked(path):values.append({'path':relative(path),'sha256':digest(path)})
    return values
def is_notice(path):return bool(re.match(r'^(?:license|licence|copying|notice|copyright)(?:[._-]|$)',path.name,re.I))


def main():
    parser=argparse.ArgumentParser();parser.add_argument('--desktop-output',default='app')
    parser.add_argument('--release-version',default='local-development');args=parser.parse_args()
    if not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._-]{0,79}',args.release_version):raise ValueError('Invalid release version')
    output=ROOT/args.desktop_output
    if not output.resolve().is_relative_to(ROOT):raise ValueError('Desktop output must remain inside project')
    timestamp=datetime.now(timezone.utc).isoformat()
    files={};missing=[];expected_hashes={};safe_directories={ROOT};hash_workers=ThreadPoolExecutor(max_workers=4)
    def safe_parent(path):
        parent=path.parent
        if parent in safe_directories:return
        if not parent.is_relative_to(ROOT):raise ValueError('Runtime path outside project')
        chain=[]
        while parent not in safe_directories:
            if linked(parent):raise ValueError('Linked runtime directory')
            chain.append(parent);parent=parent.parent
        safe_directories.update(chain)
    def add(path,category):
        path=Path(path)
        if not path.is_file():missing.append(relative(path));return
        safe_parent(path)
        if not path.is_relative_to(ROOT) or linked(path):raise ValueError('Linked/outside runtime input')
        stamp=path.stat()
        def hash_frozen():
            current=path.stat()
            if (current.st_size,current.st_mtime_ns)!=(stamp.st_size,stamp.st_mtime_ns):raise RuntimeError('File changed before hash: '+relative(path))
            return digest(path)
        files[relative(path)]={'path':relative(path),'bytes':stamp.st_size,'_hashFuture':hash_workers.submit(hash_frozen),'category':category,
            'distributionScope':'local-user-provided-resource; redistribution rights not granted by code license' if category in PRIVATE_CATEGORIES else 'runtime/source under retained component license'}
        if len(files)%5000==0:print('Queued '+str(len(files))+' allowlisted hashes',flush=True)
    def tree(base,category):
        if not base.is_dir():missing.append(relative(base));return
        print('Hashing '+relative(base)+' ['+category+']',flush=True)
        for path in children(base):add(path,category)
    if output.is_dir():
        # The deploy output is already flat and must not be confused with test bin/obj.
        for path in output.rglob('*'):
            if path.is_file() and path.suffix!='.pdb':add(path,'desktop-publish')
    else:missing.append(relative(output))
    for name,category in [('runtime/voice','voice-runtime'),('runtime/asr','asr-runtime'),('runtime/memory','embedding-worker'),('runtime/capabilities','capability-runtime')]:tree(ROOT/name,category)
    # Release.ps1 requires these offline checks before freeze/assembly. Chat
    # Work uses the selected bridge/source closure, stdlib and registered
    # persona only; no verify_pi.py, tests tree or external model is needed.
    # Keep the general verification-script exclusion with explicit gate entries.
    for name in ['verify_reset.py','verify_reset_relocation.py','verify_chat_work.py']:
        add(ROOT/'runtime/capabilities'/name,'release-verification')
    for name in AMAEDEUS_MODULES:add(ROOT/'agent-runtime'/name,'amadeus-source-closure')
    for name in ['LICENSE','THIRD_PARTY_NOTICES.md']:add(ROOT/name,'license')
    for name in ['Directory.Build.props','NuGet.Config','Start-ATRI.cmd']:add(ROOT/name,'corresponding-source')
    add(ROOT/'settings.template.json','configuration-template')
    for name in ['release-guide.md','chat-work.md']:
        add(ROOT/'docs'/name,'corresponding-source')
    for name in ['LICENSE','THIRD_PARTY_NOTICES.md']:add(ROOT/'agent-runtime'/name,'license')
    tree(ROOT/'agent-runtime/LICENSES','license');tree(ROOT/'licenses','license')
    add(ROOT/'scripts/psb.py','runtime-helper')
    # Corresponding first-party source, separate from published binaries.
    tree(ROOT/'src','corresponding-source')
    for name in ['Build.ps1','Release.ps1','make-release.py','Install-LocalRelease.py','Check-Environment.ps1','Environment.ps1','Start-ATRI.ps1','setup-capabilities.ps1','setup-asr.ps1','setup-memory.ps1','freeze-runtime.py','prepare-game-audio.py','GARbro-LICENSE.txt']:
        add(ROOT/'scripts'/name,'corresponding-source')
    # The UI discovers every installed model, including invalid entries that must
    # remain visible for diagnostics. Do not silently ship only the default.
    tree(ROOT/'models/voice','voice-model')
    if (ROOT/'training/atri/model-snapshots').is_dir():tree(ROOT/'training/atri/model-snapshots','voice-snapshot')
    reference_root=ROOT/'training/atri/ATR_reference'
    reference_files=sorted(path for path in reference_root.glob('*/*') if path.suffix.lower() in {'.wav','.txt'})
    if not reference_root.is_dir() or not reference_files:
        missing.append('training/atri/ATR_reference (nonempty WAV/TXT pairs required)')
    for path in reference_files:
        if not path.with_suffix('.txt' if path.suffix.lower()=='.wav' else '.wav').is_file():
            missing.append(relative(path)+' (missing reference pair)')
    if not any(re.sub(r'^\d+_', '', p.parent.name)=='neutral' and p.suffix=='.wav' for p in reference_files):
        missing.append('training/atri/ATR_reference (neutral reference required)')
    for path in reference_files:
        if path.suffix.lower() in {'.wav','.txt'}:add(path,'voice-reference')
    tree(ROOT/'data/models/multilingual-e5-small','embedding-model')
    for required_asr in ['SenseVoiceSmall/model.int8.onnx','SenseVoiceSmall/tokens.txt','silero-vad.onnx',
                         'WhisperTurbo/model.safetensors','WhisperTurbo/config.json','WhisperTurbo/generation_config.json',
                         'WhisperTurbo/preprocessor_config.json','WhisperTurbo/tokenizer.json','WhisperTurbo/tokenizer_config.json',
                         'WhisperTurbo/vocab.json','WhisperTurbo/merges.txt','WhisperTurbo/added_tokens.json',
                         'WhisperTurbo/special_tokens_map.json','WhisperTurbo/normalizer.json',
                         'WhisperTurbo/README.md','WhisperTurbo/LICENSE']:
        if not (ROOT/'data/models/asr'/required_asr).is_file():missing.append('data/models/asr/'+required_asr)
    for path in children(ROOT/'data/models/asr'):
        if 'test_wavs' not in path.parts:add(path,'asr-model')
    # The managed browser is a deployed dependency despite residing under cache/.
    browsers=list((ROOT/'data/cache/browser').glob('chromium-*/chrome.exe'))+list((ROOT/'data/cache/browser').glob('chromium-*/chrome-win64/chrome.exe'))
    if browsers:
        selected=max(browsers,key=lambda p:int(p.parent.parent.name.split('-')[-1]) if p.parent.name=='chrome-win64' else int(p.parent.name.split('-')[-1]))
        tree(selected.parent,'managed-browser')
    else:missing.append('data/cache/browser/chromium-*/chrome.exe')
    for name in ['persona.json','knowledge.json','sprites.json','mouth-sprites.json']:add(ROOT/'assets/derived'/name,'game-asset')
    add(ROOT/'assets/derived/game-audio.json','game-asset')
    game_audio=selected_game_audio_clips()
    for clip in game_audio:
        for field,category in [('source','game-audio-source'),('cache','game-audio-pcm')]:
            add(ROOT/clip[field],category);expected_hashes[clip[field]]=clip[field+'Sha256'].lower()
    tree(ROOT/'assets/derived/sprites','game-asset')
    for name in ['b101.ks.scn','b102.ks.scn','b103.ks.scn']:add(ROOT/'assets/ATRIplay'/name,'game-script')
    art=ROOT/'assets/ATRI-UI-BG/转换后PNG'
    add(art/'UI_系统图像/data/image/title_base2.png','game-asset')
    for name in ['949','1124','1144','1126','1146','1129','1149']:add(art/f'UI_菜单图层/data/uipsd/title/{name}.png','game-asset')
    add(art/'UI_菜单图层/data/uipsd/config/13397.png','game-asset')
    for name in ['414','437','441','445','438','442','446']:add(art/f'UI_菜单图层/data/uipsd/dialog/{name}.png','game-asset')
    # Existing WPF maintenance tests copy the original Chinese dialog variant.
    # Keep those fixtures, without claiming they are current runtime UI images.
    for name in ['455','478','479','482','483','486','487']:add(art/f'UI_菜单图层/data/uipsd/dialog_cn/{name}.png','maintenance-asset')
    for name in ['bg002','bg003','bg005','bg006','bg007','bg009','bg010']:add(art/f'背景_场景/bgimage/{name}.png','game-asset')
    for index,row in enumerate(files.values(),1):
        row['sha256']=row.pop('_hashFuture').result()
        if row['path'] in expected_hashes and row['sha256']!=expected_hashes[row['path']]:
            raise RuntimeError('Audited game audio hash changed: '+row['path'])
        if index%5000==0:print('Hashed '+str(index)+'/'+str(len(files))+' files',flush=True)
    hash_workers.shutdown(wait=True)
    components=[];python_rows=[];node_rows=[];uninstalled_optional=[]
    for site_name in ['runtime/voice/python/Lib/site-packages','runtime/asr/site-packages']:
        site=ROOT/site_name
        for dist in sorted(importlib.metadata.distributions(path=[str(site)]),key=lambda d:d.metadata.get('Name','')):
            name=dist.metadata.get('Name','unknown');version=dist.version;metadata=Path(dist._path)/'METADATA'
            notices=[]
            for file in dist.files or []:
                path=Path(dist.locate_file(file))
                if is_notice(path) and path.is_file() and path.resolve().is_relative_to(site):notices.append(path)
            declared=dist.metadata.get('License-Expression') or dist.metadata.get('License') or '; '.join(x for x in dist.metadata.get_all('Classifier',[]) if x.startswith('License ::')) or 'not declared'
            row={'name':name,'version':version,'runtimePath':site_name,'metadata':relative(metadata),'metadataSha256':digest(metadata),
                'declaredLicense':declared[:2000],'licenseExpression':dist.metadata.get('License-Expression'),'notices':notice_files(notices),'requiresDist':dist.metadata.get_all('Requires-Dist',[])}
            python_rows.append(row)
            normalized=re.sub('[-_.]+','-',name).lower();ref=f'python:{site_name}:{normalized}@{version}'
            components.append({'type':'library','bom-ref':ref,'name':name,'version':version,'purl':f'pkg:pypi/{quote(normalized)}@{quote(version,safe="")}',
                'hashes':[{'alg':'SHA-256','content':row['metadataSha256']}],
                'properties':[{'name':'atri:runtimePath','value':site_name},{'name':'atri:declaredLicense','value':declared[:2000]},
                              {'name':'atri:retainedNoticePaths','value':json.dumps(row['notices'],ensure_ascii=False)}]})
    node=ROOT/'runtime/capabilities/node';lock=json.loads((node/'package-lock.json').read_text(encoding='utf8'))
    for location,locked in sorted(lock['packages'].items()):
        if not location:continue
        package=node/location/'package.json'
        if not package.is_file():
            if locked.get('optional'):
                uninstalled_optional.append({'path':location,'version':locked.get('version'),'os':locked.get('os',[]),'cpu':locked.get('cpu',[])})
            else:missing.append(relative(package))
            continue
        data=json.loads(package.read_text(encoding='utf8'));name=data.get('name',location);version=data.get('version','unknown')
        notices=notice_files(p for p in package.parent.iterdir() if p.is_file() and is_notice(p))
        declared=data.get('license',locked.get('license','not declared'))
        row={'name':name,'version':version,'path':relative(package),'declaredLicense':declared,'notices':notices,'lockIntegrity':locked.get('integrity'),'lockResolved':locked.get('resolved')}
        node_rows.append(row)
        ref='node:'+location+'@'+version
        components.append({'type':'library','bom-ref':ref,'name':name,'version':version,'purl':f'pkg:npm/{quote(name,safe="/")}@{quote(version,safe="")}',
            'hashes':[{'alg':'SHA-256','content':digest(package)}],'properties':[{'name':'atri:runtimePath','value':relative(package.parent)},
            {'name':'atri:declaredLicense','value':str(declared)},{'name':'atri:retainedNoticePaths','value':json.dumps(notices,ensure_ascii=False)},
            {'name':'atri:npmIntegrity','value':locked.get('integrity','not declared')}]})
    excluded=['GPT-SoVITS-v2pro-20261006/**','*.7z and source/download archives','training/** except training/atri/ATR_reference/*/*.wav|txt and preserved training/atri/model-snapshots/**',
        'tests/** and runtime/**/core-probe|visual-probe|verify*.py except capabilities/verify_reset.py, capabilities/verify_reset_relocation.py and capabilities/verify_chat_work.py','data/config/** and data/memory/** and data/capabilities/**',
        'data/temp/**, data/logs/**, data/verification/**, download/npm/HuggingFace caches','unused original art/scripts; only explicit UI assets and b101/b102/b103 scenes are selected',
        'assets/systemVoice/** except explicitly audited game-audio.json sources; data/cache/game-audio/** except their required PCM WAVs',
        'Amadeus private config/.env/keys/auth, original Chat/TTS/desktop; only enumerated independent source modules are selected',
        'Python/Node package tests, __pycache__, wheel/source archives and debug .pdb']
    counts=Counter(x['category'] for x in files.values())
    manifest={'schemaVersion':1,'frozenAtUtc':timestamp,'projectRoot':'D:/ATRI_Agent (relative file paths portable)',
        'selection':'Explicit runtime allowlist plus corresponding source; reads only, never deletes/copies originals',
        'desktopPublishComplete':output.is_dir() and any(p.name=='atri_agent.exe' for p in output.glob('*')),
        'notAnInstaller':True,'redistributionRightsReviewRequired':True,'missingRequiredPaths':sorted(set(missing)),
        'excludedPatterns':excluded,'categoryFileCounts':dict(sorted(counts.items())),'totalFiles':len(files),'totalBytes':sum(x['bytes'] for x in files.values()),
        'files':sorted(files.values(),key=lambda x:x['path']),
        'gameAudioSelection':{'manifest':'assets/derived/game-audio.json','clips':game_audio,
            'sourceFiles':sum(x['category']=='game-audio-source' for x in files.values()),
            'pcmFiles':sum(x['category']=='game-audio-pcm' for x in files.values()),
            'pcmBytes':sum(x['bytes'] for x in files.values() if x['category']=='game-audio-pcm'),
            'cacheIsRequiredRuntimeResource':True,'rebuildOnlyPrerequisite':'ffmpeg; already deployed PCM playback does not require it'},
        'pythonDistributions':python_rows,'nodePackages':node_rows,'notInstalledOptionalNodeLockEntries':uninstalled_optional,
        'externalPrerequisites':['Windows 10/11 x64','.NET 9 Windows Desktop runtime (framework-dependent publish)','NVIDIA CUDA-compatible driver for measured TTS CUDA path; CPU fallback not performance-qualified','Git executable for Git operations; Codex CLI/login only for optional Codex provider']}
    docs=ROOT/'docs';docs.mkdir(exist_ok=True)
    (docs/'runtime-distribution.json').write_text(json.dumps(manifest,ensure_ascii=False,indent=2),encoding='utf8')
    sbom={'bomFormat':'CycloneDX','specVersion':'1.6','serialNumber':'urn:uuid:'+str(uuid.uuid4()),'version':1,
        'metadata':{'timestamp':timestamp,'component':{'type':'application','bom-ref':'atri-agent','name':'ATRI_Agent','version':args.release_version},
            'properties':[{'name':'atri:scope','value':'Installed Python distributions from specified product runtime paths and packages from isolated Node lock; model/art/audio file hashes in runtime-distribution.json'},
                          {'name':'atri:graphLimit','value':'Deployed component inventory, not a claim of exact dynamic import/transitive execution graph'}]},
        'components':components,'dependencies':[{'ref':'atri-agent','dependsOn':[c['bom-ref'] for c in components]}]}
    (docs/'runtime-sbom.cdx.json').write_text(json.dumps(sbom,ensure_ascii=False,indent=2),encoding='utf8')
    print(json.dumps({'files':len(files),'bytes':manifest['totalBytes'],'pythonDistributions':len(python_rows),'nodePackages':len(node_rows),
        'desktopPublishComplete':manifest['desktopPublishComplete'],'missingRequiredPaths':manifest['missingRequiredPaths'],'outputs':['docs/runtime-distribution.json','docs/runtime-sbom.cdx.json']},ensure_ascii=False))


if __name__=='__main__':main()

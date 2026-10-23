"""Build a player-only installer from the project's active, already patched runtime.
Python 3 + Windows .NET Framework compiler; no NuGet or Inno Setup dependency.
Private signing key must be OUTSIDE the game/source/output directories.
"""
from pathlib import Path
import argparse
import hashlib
import json
import os
import re
import shutil
import subprocess
import tempfile
import zipfile

HERE = Path(__file__).resolve().parent
PRODUCT = 'SwarmAssaultII'
REPO = 'ADELIEBEAR/SwarmAssault2'

def digest(path):
    with path.open('rb') as f:
        h = hashlib.sha256()
        for block in iter(lambda: f.read(1024*1024), b''): h.update(block)
        return h.hexdigest()

def write_json(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')

def run(args):
    subprocess.run([str(a) for a in args], check=True)

def build(project, output, version, private_key):
    if not re.fullmatch(r'\d{4}\.\d{1,2}\.\d{1,2}\.\d{1,6}', version): raise ValueError('Version: YYYY.M.D.N')
    project, output, private_key = project.resolve(), output.resolve(), private_key.resolve()
    for forbidden in [project, output, HERE]:
        if private_key.is_relative_to(forbidden): raise ValueError('Keep signing-private.xml outside project, source and output')
    output.mkdir(parents=True, exist_ok=True)
    private_key.parent.mkdir(parents=True, exist_ok=True)
    active = json.loads((project/'active-version.json').read_text(encoding='utf-8-sig'))
    runtime = (project/active['engine']).resolve()
    if not runtime.is_relative_to(project): raise ValueError('Active engine outside project')
    csc = Path(os.environ['WINDIR'])/'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
    refs = ['System.Windows.Forms', 'System.Drawing', 'System.Core', 'System.Net.Http', 'System.Web.Extensions', 'System.IO.Compression', 'System.IO.Compression.FileSystem']
    with tempfile.TemporaryDirectory(prefix='sii-build-', dir=output) as temporary:
        work = Path(temporary)
        exe = work/'SwarmAssaultII.exe'
        icon = runtime/'sa.ico'
        if not icon.exists(): icon = project/'sa.ico'
        command = [csc, '/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/out:'+str(exe), '/win32manifest:'+str(HERE/'app.manifest'), '/win32icon:'+str(icon)] + ['/reference:'+r+'.dll' for r in refs] + [HERE/'Launcher.cs']
        run(command)
        signer = work/'Signer.exe'
        run([csc, '/nologo', '/platform:x64', '/out:'+str(signer), '/reference:'+str(exe), '/reference:System.Web.Extensions.dll', HERE/'Signer.cs'])
        public = private_key.with_name('signing-public.xml')
        if not private_key.exists(): run([signer, 'generate', private_key, public])
        if not public.exists(): raise ValueError('Missing paired signing-public.xml')
        config = {'Schema':1,'Product':PRODUCT,'Repository':REPO,'PublicKeyXml':public.read_text(),'DataRoot':None}
        config_path = work/'distribution.json'; write_json(config_path,config)
        catalog = []
        content_root = runtime/'Support/Content/sa'
        for p in sorted(content_root.rglob('*')):
            if p.is_file(): catalog.append({'Path':p.relative_to(content_root).as_posix(),'Length':p.stat().st_size,'Sha256':digest(p)})
        if len(catalog) != 8: raise ValueError('Unexpected asset seed: inspect before release')
        catalog_path = work/'content-catalog.json'; write_json(catalog_path,catalog)
        # Explicit runtime-only roots. No root legacy copy, Profile, source, test tools or debug symbols.
        files = []
        for p in sorted(runtime.rglob('*')):
            if not p.is_file(): continue
            rel = p.relative_to(runtime)
            if rel.parts[0] not in ('mods','glsl','Support') and len(rel.parts)>1: continue
            if rel.parts[0] == 'Support' and not p.is_relative_to(content_root): continue
            if p.suffix.lower() in ('.pdb','.cs','.csproj','.sln','.py','.ps1','.cmd','.bat') or p.name == 'AGENTS.md': continue
            if p.name.startswith(('OpenRA.Utility.', 'OpenRA.Server.')) or p.name == 'createdump.exe': continue
            if any('sii-lab' in part.lower() or part.lower().startswith('sii-test-') for part in rel.parts): raise ValueError('Test fixture in runtime: '+str(rel))
            files.append((p, 'versions/'+version+'/'+rel.as_posix()))
        files += [(exe,'SwarmAssaultII.exe'),(icon,'SwarmAssaultII.ico'),(config_path,'distribution.json'),(catalog_path,'versions/'+version+'/content-catalog.json')]
        for p in sorted((project/'출처').glob('*')):
            if p.is_file(): files.append((p,'licenses/asset-provenance/'+p.name))
        for name in ['COPYING','AUTHORS','NanumGothic-OFL.txt']:
            files.append((runtime/name, 'licenses/'+name))
        rights = work/'ASSET-NOTICE.txt'
        rights.write_text('Game engine: see COPYING (GPL-3.0). Distribution shell: MIT.\nOriginal Swarm Assault assets and external insect graphics retain their own rights; they are not covered by the distribution shell MIT license.\nSome recorded external models use CC BY-NC-SA or CC BY-NC-ND. Distributors remain responsible for the applicable asset permissions and corresponding engine source obligations.\nModel provenance records are included; not every recorded model is necessarily used in this build.\n',encoding='utf-8')
        files.append((rights,'licenses/ASSET-NOTICE.txt'))
        files.append((HERE/'LICENSE','licenses/Distribution-MIT.txt'))
        guide = work/'플레이-안내.txt'
        guide.write_text('스웜 어썰트 II\nWindows 10/11 64비트용. 설치파일 하나를 실행하면 됩니다.\n실행: 바탕화면의 스웜 어썰트 II. 첫 실행 시 .NET Framework 4.8이 필요합니다(일반적인 Windows 10/11에 포함).\n새 정식 버전이 GitHub에 게시되면 게임 시작 시 자동으로 확인·설치합니다. 오프라인이면 현재 버전으로 실행합니다.\n저장 데이터: %LOCALAPPDATA%\\SwarmAssaultII\\Profile\n기존 개발판의 진행을 옮기려면 양쪽 게임을 종료한 뒤 기존 Profile 내용을 위 폴더로 복사하세요.\n원작 및 외부 그래픽의 권리는 licenses 폴더를 참고하세요.\n',encoding='utf-8')
        files.append((guide,'플레이-안내.txt'))
        paths = [rel.lower() for _,rel in files]
        if len(paths) != len(set(paths)): raise ValueError('Duplicate package path')
        manifest = {'Schema':1,'Product':PRODUCT,'Version':version,'Files':[{'Path':rel,'Length':p.stat().st_size,'Sha256':digest(p)} for p,rel in files]}
        payload = work/'payload.zip'
        with zipfile.ZipFile(payload,'w',zipfile.ZIP_DEFLATED,compresslevel=6) as z:
            for p,rel in files: z.write(p,rel)
            z.writestr('package.json',json.dumps(manifest,ensure_ascii=False,separators=(',',':')))
        filename = f'SwarmAssaultII-Setup-v{version}.exe'
        setup = output/filename
        # Keep package data in declared PE resources instead of an opaque appended overlay.
        payload_hash = work/'payload.sha256'
        payload_hash.write_text(digest(payload), encoding='ascii')
        setup_command = [('/out:'+str(setup)) if str(arg).startswith('/out:') else arg for arg in command]
        run(setup_command + ['/resource:'+str(payload)+',SwarmAssaultII.Package.zip,public',
                             '/resource:'+str(payload_hash)+',SwarmAssaultII.Package.sha256,public'])
        update = {'Schema':1,'Product':PRODUCT,'Repository':REPO,'Version':version,'FileName':filename,'Length':setup.stat().st_size,'Sha256':digest(setup)}
        unsigned = work/'update-payload.json'; write_json(unsigned,update)
        run([signer,'sign',private_key,unsigned,output/'swarm-update.json'])
        # Verify the paired key immediately rather than shipping an unusable update channel.
        shutil.copy2(exe,output/'launcher-build.exe')
        write_json(output/'package-inventory.json',manifest)
        (output/'SHA256SUMS.txt').write_text(digest(setup)+'  '+filename+'\n'+digest(output/'swarm-update.json')+'  swarm-update.json\n',encoding='ascii')
        (output/'RELEASE-NOTES.md').write_text(f'스웜 어썰트 II {version}\n\n- 플레이어 전용 설치파일: 개발 소스·시험 도구·개인 저장 데이터 제외\n- Windows 10/11 64비트, 관리자 권한 없이 설치\n- 게임 시작 시 GitHub 정식 버전 자동 업데이트\n- 서명·SHA-256 검증, 업데이트 설치 실패 시 기존 설치로 직접 실행, 저장 데이터 보존\n\n원작·외부 그래픽과 엔진에는 각각의 라이선스가 적용됩니다. 설치 폴더의 licenses를 참고하세요.\n',encoding='utf-8')
        print(json.dumps({'setup':str(setup),'bytes':setup.stat().st_size,'files':len(files),'runtime':active['engine'],'sha256':digest(setup)}))

if __name__ == '__main__':
    parser=argparse.ArgumentParser()
    parser.add_argument('--project',type=Path,required=True)
    parser.add_argument('--output',type=Path,required=True)
    parser.add_argument('--version',required=True)
    parser.add_argument('--private-key',type=Path,required=True)
    args=parser.parse_args()
    build(args.project,args.output,args.version,args.private_key)

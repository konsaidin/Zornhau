#!/usr/bin/env python3
"""Export resolved recipes/weapons or smoke-test builds in isolated headless worlds."""
import argparse
from datetime import datetime
import json
import os
from pathlib import Path
import shutil
import subprocess
import time
import zipfile

ROOT = Path(__file__).resolve().parents[1]


def run(args):
    game = Path(args.game).resolve()
    version = json.loads((ROOT / 'modinfo.json').read_text())['version']
    package = ROOT / 'dist' / f'warbandcombat-{version}.zip'
    if not package.exists(): raise FileNotFoundError(f'Build the mod first: {package}')
    if args.training_smoke:
        subprocess.run([args.dotnet, 'build', str(ROOT / 'tools/TrainingSmoke/TrainingSmoke.csproj'),
                        '-c', 'Release', '--nologo', f'-p:VSPath={game}'], check=True)
        training = ROOT / '.artifacts/trainingsmoke.zip'
        with zipfile.ZipFile(training, 'w', zipfile.ZIP_DEFLATED) as archive:
            archive.writestr('modinfo.json', json.dumps(dict(type='code', modid='trainingsmoke',
                name='Developer training integration check', version='1.0.0', side='server',
                authors=['Warband Combat developers'], dependencies=dict(warbandcombat=version))))
            archive.write(ROOT / 'tools/TrainingSmoke/bin/Release/net10.0/TrainingSmoke.dll', 'TrainingSmoke.dll')
    if not args.smoke:
        subprocess.run([args.dotnet, 'build', str(ROOT / 'tools/BalanceAudit/BalanceAudit.csproj'),
                        '-c', 'Release', '--nologo', f'-p:VSPath={game}'], check=True)
        audit = ROOT / '.artifacts/balanceaudit.zip'
        with zipfile.ZipFile(audit, 'w', zipfile.ZIP_DEFLATED) as archive:
            archive.writestr('modinfo.json', json.dumps(dict(type='code', modid='balanceaudit',
                name='Developer balance audit', version='1.0.1', side='server', authors=['Warband Combat developers'])))
            archive.write(ROOT / 'tools/BalanceAudit/bin/Release/net10.0/BalanceAudit.dll', 'BalanceAudit.dll')
    stamp = datetime.now().strftime('%Y%m%d-%H%M%S')
    contexts = ('vanilla', 'co') if args.context == 'both' else (args.context,)
    for context in contexts:
        profile = ROOT / '.artifacts' / f'balance-{"smoke" if args.smoke else "export"}-{context}-{stamp}'
        mods = profile / 'Mods'; mods.mkdir(parents=True)
        shutil.copy2(package, mods / package.name)
        if args.training_smoke: shutil.copy2(training, mods / training.name)
        if not args.smoke: shutil.copy2(audit, mods / audit.name)
        if context == 'co':
            from importlib.util import spec_from_file_location, module_from_spec
            spec = spec_from_file_location('fetch_overhaul', ROOT / 'scripts/fetch-overhaul.py')
            fetcher = module_from_spec(spec); spec.loader.exec_module(fetcher)
            sources = fetcher.fetch()
            for entry in json.loads((ROOT / 'compat/overhaul-modpack.json').read_text())['packages']:
                shutil.copy2(sources / entry['file'], mods / entry['file'])
        config = dict(ServerName='Isolated Warband balance check', Ip='127.0.0.1',
            Port=args.port + (context == 'co'), AdvertiseServer=False, Upnp=False,
            AllowPvP=True, DefaultRoleCode='suplayer', ModPaths=[str(game / 'Mods'), str(mods)],
            Roles=[dict(Code='suplayer', PrivilegeLevel=0, Name='Balance tester', DefaultGameMode=1,
                        Privileges=['chat', 'attackplayers', 'attackcreatures'])],
            WorldConfig=dict(WorldName='Balance check', SaveFileLocation=str(profile / 'Saves/audit.vcdbs'),
                AllowCreativeMode=True, PlayStyle='creativebuilding', PlayStyleLangCode='creativebuilding',
                WorldType='superflat', WorldConfiguration=dict(worldClimate='superflat', gameMode='creative',
                    temporalStability='false', temporalStorms='off', loreContent='false')))
        (profile / 'serverconfig.json').write_text(json.dumps(config))
        output = ROOT / '.artifacts' / f'balance-{context}.json'
        environment = os.environ.copy()
        environment.pop('WARBAND_BALANCE_AUDIT', None)
        environment.pop('WARBAND_TRAINING_SMOKE', None)
        if args.training_smoke: environment['WARBAND_TRAINING_SMOKE'] = '1'
        if not args.smoke:
            output.unlink(missing_ok=True)
            environment['WARBAND_BALANCE_AUDIT'] = str(output)
        log = profile / 'console.log'
        with log.open('w') as stream:
            process = subprocess.Popen([args.dotnet, str(game / 'VintagestoryServer.dll'),
                '--dataPath', str(profile)], cwd=game, env=environment, stdin=subprocess.PIPE,
                stdout=stream, stderr=subprocess.STDOUT, text=True)
            try:
                deadline = time.monotonic() + 120
                while time.monotonic() < deadline:
                    contents = log.read_text(errors='replace')
                    if 'Entering runphase RunGame' in contents:
                        if '[Warband Combat] Loaded ' not in contents: raise RuntimeError('Balance profiles were not loaded')
                        if not args.smoke and not output.exists(): raise RuntimeError('Audit did not export')
                        if not args.training_smoke or '[Training Smoke] PASS:' in contents: break
                    if '[Training Smoke] FAILED:' in contents: raise RuntimeError(f'Training integration failed: {log}')
                    if process.poll() is not None: raise RuntimeError(f'Server exited early: {log}')
                    time.sleep(.2)
                else: raise TimeoutError(f'Server did not reach RunGame: {log}')
            finally:
                if process.poll() is None:
                    try:
                        process.stdin.write('/stop\n'); process.stdin.flush(); process.wait(timeout=30)
                    except (BrokenPipeError, subprocess.TimeoutExpired):
                        process.terminate()
                        try: process.wait(timeout=15)
                        except subprocess.TimeoutExpired: process.kill(); process.wait()
            if process.returncode != 0: raise RuntimeError(f'Server failed: {log}')
        errors = [line for line in log.read_text(errors='replace').splitlines() if '[Error]' in line or '[Fatal]' in line]
        if errors: raise RuntimeError(f'Engine errors in {log}: ' + '\n'.join(errors))
        print(f'PASS {context}: balanced profiles loaded, RunGame reached, graceful shutdown; {log}', flush=True)
    if args.refresh:
        subprocess.run(['python3', str(ROOT / 'scripts/generate-balance.py'), '--refresh-catalog'], check=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--game', default='/home/dead/Documents/vintagestory')
    parser.add_argument('--dotnet', default=os.environ.get('VS_DOTNET', '/home/dead/.local/share/vs-combat-dotnet/dotnet'))
    parser.add_argument('--context', choices=('vanilla', 'co', 'both'), default='both')
    parser.add_argument('--port', type=int, default=25568)
    parser.add_argument('--smoke', action='store_true', help='Validate the packaged mod without the audit plugin')
    parser.add_argument('--training-smoke', action='store_true', help='Also spawn, kill and revive an actual humanoid target')
    parser.add_argument('--refresh', action='store_true', help='Regenerate the committed catalog and tables after exports')
    options = parser.parse_args()
    if options.smoke and options.refresh: parser.error('--refresh requires export mode')
    if options.training_smoke and not options.smoke: parser.error('--training-smoke requires --smoke')
    run(options)

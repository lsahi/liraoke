"""Assemble a relocatable CPU runtime from a pinned, verified build environment.

Packaging only; end users never run this. Does not download or modify the source environment.
The source must use official pinned dependencies; retains Python and dependency license files.
"""
import argparse
import hashlib
import importlib.metadata
import json
from pathlib import Path
import shutil
import subprocess


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--python-root', type=Path, required=True)
    parser.add_argument('--site-packages', type=Path, required=True)
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    if args.output.exists():
        raise RuntimeError(f'Refusing to overwrite existing runtime: {args.output}')
    packages = {d.metadata['Name'].lower(): d.version for d in importlib.metadata.distributions(path=[str(args.site_packages)])}
    for name, version in {'torch': '2.8.0+cpu', 'demucs': '4.1.0', 'numpy': '2.2.6', 'sympy': '1.13.3'}.items():
        if packages.get(name) != version:
            raise RuntimeError(f'Expected {name}=={version}; found {packages.get(name)}')
    cpu = args.output / 'cpu'
    shutil.copytree(args.python_root, cpu, ignore=shutil.ignore_patterns('__pycache__', '*.pyc'))
    destination = cpu / 'Lib/site-packages'
    destination.mkdir(parents=True, exist_ok=True)
    for path in args.site_packages.rglob('*'):
        if not path.is_file():
            continue
        relative = path.relative_to(args.site_packages)
        if '__pycache__' in relative.parts or path.suffix == '.pyc':
            continue
        if relative.parts[0] == 'torch' and (path.suffix.lower() == '.lib' or relative.parts[1:2] == ('include',)):
            continue
        target = destination / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, target)
    models = args.output / 'model'
    models.mkdir()
    model = models / '955717e8.safetensors'
    shutil.copy2(args.model, model)
    with model.open('rb') as handle:
        model_hash = hashlib.file_digest(handle, 'sha256').hexdigest()
    worker = Path(__file__).resolve().parents[1] / 'src/KaraokeFilmMaker.Infrastructure/Separation/Runtime/demucs-worker.py'
    shutil.copy2(worker, args.output / 'demucs-worker.py')
    info = {'TorchVersion': '2.8.0+cpu', 'DemucsVersion': '4.1.0', 'ModelSha256': model_hash}
    subprocess.run([str(cpu / 'python.exe'), '-I', '-X', 'utf8', str(args.output / 'demucs-worker.py'),
                    '--model', str(model), '--model-sha256', model_hash, '--device', 'cpu',
                    '--expected-torch', '2.8.0+cpu', '--self-test'], check=True)
    # Publish the marker last: a partial assembly is never considered usable.
    (cpu / 'runtime.json').write_text(json.dumps(info, indent=2), encoding='utf-8')
    (args.output / 'build-dependencies.json').write_text(json.dumps(packages, indent=2), encoding='utf-8')
    print(f'CPU runtime ready: {args.output}', flush=True)


if __name__ == '__main__':
    main()

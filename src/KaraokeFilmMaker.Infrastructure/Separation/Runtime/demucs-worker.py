"""Liraoke offline inference worker. Inputs are local app-owned PCM WAVs, never code."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import random
import sys

os.environ['HF_HUB_OFFLINE'] = '1'
os.environ['PYTHONNOUSERSITE'] = '1'


def emit(message, fraction=None):
    print(json.dumps({'message': message, 'fraction': fraction}, ensure_ascii=False), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--model', type=Path, required=True)
    parser.add_argument('--model-sha256', required=True)
    parser.add_argument('--device', choices=['cpu', 'cuda'], required=True)
    parser.add_argument('--expected-torch', required=True)
    parser.add_argument('--gpu-index', type=int, default=0)
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('--input', type=Path)
    parser.add_argument('--output', type=Path)
    parser.add_argument('--mode', choices=['sum', 'subtract', 'vocals'], default='sum')
    args = parser.parse_args()
    with args.model.open('rb') as handle:
        if hashlib.file_digest(handle, 'sha256').hexdigest() != args.model_sha256.lower():
            raise RuntimeError('Model integrity check failed; repair the CPU component.')
    import numpy as np
    import soundfile as sf
    import torch
    if torch.__version__ != args.expected_torch:
        raise RuntimeError('Runtime version mismatch. Please repair the component.')
    from demucs.hf import load_safetensors_model
    from demucs.apply import apply_model, BagOfModels
    random.seed(42)
    np.random.seed(42)
    torch.manual_seed(42)
    torch.set_num_threads(min(8, os.cpu_count() or 1))
    device = 'cpu'
    if args.device == 'cuda':
        if not torch.cuda.is_available():
            raise RuntimeError('GPU is unavailable. Use CPU or repair the GPU component.')
        torch.cuda.set_device(args.gpu_index)
        torch.cuda.manual_seed_all(42)
        device = f'cuda:{args.gpu_index}'
    emit('加载本地模型…', 0.04)
    model = BagOfModels([load_safetensors_model(args.model)]).eval()
    if args.self_test:
        t = torch.arange(44100, dtype=torch.float32) / 44100
        wave = (torch.sin(t * 440 * 2 * torch.pi) * .1).repeat(2, 1)
    else:
        if args.input is None or args.output is None or args.output.exists():
            raise RuntimeError('Input/output invalid or output already exists.')
        data, rate = sf.read(args.input, dtype='float32', always_2d=True)
        if rate != 44100 or data.shape[1] != 2 or len(data) == 0:
            raise RuntimeError('Expected non-empty 44.1 kHz stereo WAV.')
        wave = torch.from_numpy(data.T.copy())
    if not torch.isfinite(wave).all():
        raise RuntimeError('Invalid audio samples.')
    ref = wave.mean(0)
    mean, std = ref.mean(), ref.std() + 1e-8

    def progress(info):
        offset = info.get('segment_offset', 0)
        emit('正在分离人声与伴奏…', min(.93, .08 + .85 * offset / wave.shape[-1]))

    with torch.inference_mode():
        stems = apply_model(model, ((wave - mean) / std)[None], device=device,
                            segment=7, shifts=1, overlap=.25, split=True, num_workers=0,
                            progress=False, callback=progress, callback_arg={'audio_length': wave.shape[-1]})
        stems = (stems * std + mean)[0]
        if not torch.isfinite(stems).all() or stems.shape[-1] != wave.shape[-1]:
            raise RuntimeError('Invalid inference output.')
        if args.self_test:
            emit(f'自检成功：{torch.__version__} / {device}', 1)
            return
        vocals = stems[model.sources.index('vocals')]
        accompaniment = sum((stem for i, stem in enumerate(stems) if model.sources[i] != 'vocals'), torch.zeros_like(vocals))
        residual = wave - vocals
        # Match the listening experiment: a common gain across all alternatives.
        peak = max(float(x.abs().max()) for x in (wave, vocals, accompaniment, residual))
        gain = min(1., .98 / max(peak, 1e-12))
        selected = {'sum': accompaniment, 'subtract': residual, 'vocals': vocals}[args.mode]
        emit('写入音频文件…', .96)
        sf.write(args.output, selected.cpu().T.numpy() * gain, 44100, subtype='PCM_24')
        info = sf.info(args.output)
        if info.frames != wave.shape[-1] or info.channels != 2 or info.samplerate != 44100:
            raise RuntimeError('Output validation failed.')
    emit('分离完成', 1)


if __name__ == '__main__':
    main()

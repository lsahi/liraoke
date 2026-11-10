"""Local alignment plugin protocol v1. Uses corrected units, never generates readings."""
import argparse, hashlib, json, os, pathlib, re, sys, time, traceback
os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1', TOKENIZERS_PARALLELISM='false')
def emit(message): print(json.dumps({'message': message}, ensure_ascii=False), flush=True)

def run(args):
    import numpy as np
    import torch
    import soundfile as sf
    torch.set_num_threads(min(4, os.cpu_count() or 1))
    device = 'cuda' if args.device == 'gpu' else 'cpu'
    if device == 'cuda' and not torch.cuda.is_available(): raise RuntimeError('GPU 自检失败，请使用 CPU 或更新 NVIDIA 驱动。')
    root = pathlib.Path(args.runtime)
    model_path = root / 'model'
    if args.plugin == 'nextfire':
        sys.path.insert(0, str(root/'upstream'))
        from kara_align.align.backends.wav2vec2_ctc import Wav2Vec2CTCBackend
        from kara_align.models import LyricsDoc, Line, Segment, Unit, AlignConfig, Calibration, AudioAsset
        from kara_align.align.runner import AlignInputs, run_alignment
        from kara_align.reading.profiles import get_profile
        backend = Wav2Vec2CTCBackend(model_id=str(model_path),device=device)
        backend._load()
        def align(audio, lines):
            doc = LyricsDoc(lines=[Line(id=l['id'], text=l['text'], segments=[Segment(id='s-'+u['id'],surface=u['surface'],reading=u['reading'],lang=u['language'],units=[Unit(id=u['id'],reading=u['reading'])]) for u in l['units']]) for l in lines])
            emission=backend.emissions(audio)
            asset=AudioAsset(role='original',sha256=hashlib.sha256(audio.tobytes()).hexdigest(),duration_ms=round(len(audio)/16),sample_rate=16000,channels=1,num_samples=len(audio))
            result=run_alignment(AlignInputs(lyrics=doc,mode='plain',calibration=Calibration(),config=AlignConfig(device=device,audio_role='original'),backend_info=backend.info(),tokenize=backend.tokenize,profile=get_profile(),emission_for=lambda _:emission,available_roles=['original'],audio_assets={'original':asset},audio_duration_ms=round(len(audio)/16),supports_language=backend.supports_language))
            return {l['id']:[{'id':u.unit_id,'start':None if u.start_ms is None else u.start_ms/1000,'end':None if u.end_ms is None else u.end_ms/1000} for u in result.units if u.line_id==l['id']] for l in lines}
    else:
        from transformers import AutoProcessor, AutoModelForTokenClassification
        processor=AutoProcessor.from_pretrained(model_path,local_files_only=True)
        dtype=torch.bfloat16 if device=='cuda' and torch.cuda.is_bf16_supported() else torch.float32
        model=AutoModelForTokenClassification.from_pretrained(model_path,local_files_only=True,dtype=dtype,attn_implementation='sdpa').to(device).eval()
        def align(audio, lines):
            if len(audio)>16000*300: raise RuntimeError('Qwen 粗定位需要有效 LRC 句段时间，或不超过 5 分钟的音频。')
            units=[u for l in lines for u in l['units']]
            readings=[u['reading'] for u in units]
            lang=max(('Japanese','Chinese','English'),key=lambda name:sum(u['language']=={'Japanese':'ja','Chinese':'zh','English':'en'}[name] for u in units))
            original=processor.split_words_for_alignment
            try:
                processor.split_words_for_alignment=lambda *a,**kw:readings
                inputs,words=processor.prepare_forced_aligner_inputs(audio=audio,transcript=' '.join(readings),language=lang,return_tensors='pt')
            finally: processor.split_words_for_alignment=original
            inputs=inputs.to(model.device,model.dtype)
            with torch.inference_mode(): prediction=model(**inputs)
            times=processor.decode_forced_alignment(logits=prediction.logits,input_ids=inputs['input_ids'],word_lists=words,timestamp_token_id=model.config.timestamp_token_id)[0]
            if len(times)!=len(units): raise RuntimeError('模型返回单元数不匹配。')
            mapped={u['id']:{'id':u['id'],'start':t['start_time'],'end':t['end_time']} for u,t in zip(units,times)}
            return {l['id']:[mapped[u['id']] for u in l['units']] for l in lines}
    if args.self_test:
        probe=np.zeros(16000,dtype=np.float32)
        if args.plugin=='nextfire': backend.emissions(probe)
        else: align(probe,[{'id':'probe','text':'あ','units':[{'id':'unit','reading':'あ','language':'ja'}]}])
        emit('模型加载和真实推理自检通过');return
    request=json.loads(pathlib.Path(args.request).read_text(encoding='utf-8-sig'))
    audio,sr=sf.read(request['audio'],dtype='float32');assert sr==16000 and audio.ndim==1
    duration=len(audio)/sr;lines=request['lines'];coarse=None
    # Trusted source LRC is used only when every sung row matches the current document.
    anchors=[]
    if request.get('lrc'):
        for raw in pathlib.Path(request['lrc']).read_text(encoding='utf-8-sig').splitlines():
            m=re.match(r'\[(\d+):(\d+(?:\.\d+)?)\]\s*(.*)',raw)
            if m: anchors.append((int(m[1])*60+float(m[2]),m[3].strip()))
    sung=[a for a in anchors if a[1]]
    norm=lambda x:re.sub(r'[\s\W_]+','',re.sub(r'[（(][ぁ-ゖァ-ヺー\s]+[)）]','',x))
    use_lrc=len(sung)==len(lines) and all(norm(a[1])==norm(l['text']) for a,l in zip(sung,lines)) and all(0<=a[0]<duration for a in sung) and all(a[0]<b[0] for a,b in zip(sung,sung[1:]))
    if not use_lrc:
        emit('正在定位整曲句段；已有人工时间点不参与推理…')
        coarse=align(audio,lines)
    results=[]
    for i,line in enumerate(lines):
        if line.get('protected'):
            results.append({'id':line['id'],'units':[],'error':'已有人工打点，已保留。'});continue
        emit(f"逐句对齐 {i+1}/{len(lines)}")
        try:
            if use_lrc:
                start=max(0,sung[i][0]-.5)
                ends=[t for t,_ in anchors if t>sung[i][0]]
                end=min(duration,(min(ends) if ends else duration)+.25)
            else:
                times=coarse[line['id']]
                valid=[t for t in times if t['start'] is not None and t['end'] is not None and t['end']>t['start']]
                if not valid: raise ValueError('未获得可靠句段位置，请提供匹配的 LRC 或手动打轴。')
                start=max(0,min(t['start'] for t in valid)-.5);end=min(duration,max(t['end'] for t in valid)+.5)
            if not 0<end-start<=35: raise ValueError('句段范围不可靠或超过35秒，已跳过；请检查歌词与音频版本。')
            # Actual audio cropping, then local inference. Restore the original song clock afterwards.
            first=round(start*sr);last=round(end*sr);start=first/sr
            refined=align(audio[first:last],[line])[line['id']]
            if len(refined)!=len(line['units']): raise ValueError('返回单元数量不匹配。')
            previous=-1
            for t in refined:
                if t['start'] is None or t['end'] is None or not np.isfinite(t['start']) or not np.isfinite(t['end']) or not previous<=t['start']<t['end']<=(last-first)/sr+.001:
                    raise ValueError('句内出现零时长、逆序或越界，请手动检查。')
                previous=t['end'];t['start']=round(t['start']+start,6);t['end']=round(t['end']+start,6)
            results.append({'id':line['id'],'units':refined,'window':[start,end],'positionSource':'lrc' if use_lrc else 'coarse-model'})
        except Exception as e:
            results.append({'id':line['id'],'units':[],'error':str(e)[:500]})
        pathlib.Path(args.output).write_text(json.dumps({'lines':results},ensure_ascii=False),encoding='utf-8')
    emit('本地分段对齐完成')

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--plugin',choices=['nextfire','qwen'],required=True);p.add_argument('--runtime',required=True);p.add_argument('--device',choices=['cpu','gpu'],default='cpu');p.add_argument('--request');p.add_argument('--output');p.add_argument('--self-test',action='store_true')
    try: run(p.parse_args())
    except Exception as e: emit('失败：'+str(e)[:500]);traceback.print_exc(file=sys.stderr);sys.exit(1)

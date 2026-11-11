"""Local alignment plugin protocol v1. Uses corrected units, never generates readings."""
import argparse, hashlib, json, os, pathlib, re, sys, time, traceback
os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1', TOKENIZERS_PARALLELISM='false')
def emit(message): print(json.dumps({'message': message}, ensure_ascii=False), flush=True)


def repair_units(raw, units, start, end):
    import math
    n=len(units)
    if len(raw)!=n or not n or end<=start: raise ValueError('返回单元数量或句段范围无效。')
    points=[dict(x) for x in raw]
    fixed={i for i,u in enumerate(units) if u.get('fixedStart') is not None}
    for i in fixed: points[i].update(start=units[i]['fixedStart'],end=units[i]['fixedEnd'])
    def valid(x): return all(isinstance(x.get(k),(int,float)) and math.isfinite(x[k]) for k in ('start','end')) and start<=x['start']<x['end']<=end
    # Measure union of actual overlap intervals, not summed duplicate overlaps.
    events=[]
    for x in points:
        if valid(x): events.extend([(x['start'],1),(x['end'],-1)])
    active=0;previous=start;overlap=0
    for time,delta in sorted(events):
        if active>1: overlap+=time-previous
        active+=delta;previous=time
    anchors={};previous_index=-1;previous_end=start
    for i,x in enumerate(points):
        next_fixed=next((j for j in sorted(fixed) if j>i),None)
        enough_after=x.get('end') is not None and (end-x['end'] >= (n-i-1)*.001 if next_fixed is None else points[next_fixed]['start']-x['end'] >= (next_fixed-i-1)*.001)
        if valid(x) and x['start']-previous_end >= (i-previous_index-1)*.001 and enough_after:
            anchors[i]=x;previous_index=i;previous_end=x['end']
        elif i in fixed: raise ValueError('人工锚点与句段冲突，已保留人工点。')
    if not anchors: raise ValueError('没有可靠发音锚点，不能对整句凭空分配时间。')
    repaired=[];boundaries=[-1]+list(anchors)+[n]
    for left,right in zip(boundaries,boundaries[1:]):
        low=start if left<0 else anchors[left]['end'];high=end if right==n else anchors[right]['start']
        count=right-left-1
        if count and high<=low: raise ValueError('未识别单元没有可分配时间。')
        for j in range(count):
            idx=left+1+j;points[idx]={'id':units[idx]['id'],'start':low+(high-low)*j/count,'end':low+(high-low)*(j+1)/count};repaired.append(idx)
    for i in range(n-1):
        if i not in fixed: points[i]['end']=points[i+1]['start']
    span=points[-1]['end']-points[0]['start']
    review='AI识别失败，推荐人工审核' if span>0 and overlap/span>.05 else None
    return points,review,repaired

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
    def infer(line,start,end):
        if not 0<end-start<=35: raise ValueError('句段范围不可靠或超过35秒。')
        first=round(start*sr);last=round(end*sr);start=first/sr;end=last/sr
        raw=align(audio[first:last],[line])[line['id']]
        for point in raw:
            for key in ('start','end'):
                if point[key] is not None: point[key]+=start
        points,review,repaired=repair_units(raw,line['units'],start,end)
        return {'id':line['id'],'units':points,'review':review,'interpolatedUnitIndices':repaired,'window':[start,end]}
    for i,line in enumerate(lines):
        if line.get('protected'):
            results.append({'id':line['id'],'units':[{'id':u['id'],'start':u['fixedStart'],'end':u['fixedEnd']} for u in line['units']], 'protected':True});continue
        emit(f"逐句对齐 {i+1}/{len(lines)}")
        try:
            if use_lrc:
                start=max(0,sung[i][0]-.5)
                ends=[t for t,_ in anchors if t>sung[i][0]]
                end=min(duration,(min(ends) if ends else duration)+.25)
            else:
                valid=[x for x in coarse[line['id']] if x['start'] is not None and x['end'] is not None and x['end']>x['start']]
                if not valid: raise ValueError('未获得可靠句段位置。')
                start=max(0,min(x['start'] for x in valid)-.5);end=min(duration,max(x['end'] for x in valid)+.5)
            known=[u for u in line['units'] if u.get('fixedStart') is not None]
            if known:
                start=min(start,min(u['fixedStart'] for u in known))
                end=max(end,max(u['fixedEnd'] for u in known))
            results.append(infer(line,start,end))
        except Exception as e: results.append({'id':line['id'],'units':[],'error':str(e)[:500]})
        pathlib.Path(args.output).write_text(json.dumps({'lines':results},ensure_ascii=False),encoding='utf-8')
    # One bounded retry after the complete first pass; known neighbours include manual rows.
    for i in range(1,len(lines)-1):
        current=results[i];left=results[i-1];right=results[i+1]
        if current.get('protected') or not left['units'] or not right['units']: continue
        low=left['units'][-1]['end'];high=right['units'][0]['start']
        conflict=not current['units'] or current['units'][0]['start']<low or current['units'][-1]['end']>high
        if not conflict or not 0<high-low<=35: continue
        emit(f"利用相邻句空档重试 {i+1}/{len(lines)}")
        try:
            retry=infer(lines[i],low,high)
            if not retry['interpolatedUnitIndices'] and not retry.get('review'):
                retry['positionSource']='neighbour-gap';results[i]=retry
            elif not current['units']:
                retry['review']='AI识别失败，推荐人工审核';results[i]=retry
        except Exception as e:
            current['retryError']=str(e)[:500] # Failed retry is not evidence of real overlapping vocals.
    pathlib.Path(args.output).write_text(json.dumps({'lines':results},ensure_ascii=False),encoding='utf-8')
    emit('本地分段对齐完成')

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--plugin',choices=['nextfire','qwen'],required=True);p.add_argument('--runtime',required=True);p.add_argument('--device',choices=['cpu','gpu'],default='cpu');p.add_argument('--request');p.add_argument('--output');p.add_argument('--self-test',action='store_true')
    try: run(p.parse_args())
    except Exception as e: emit('失败：'+str(e)[:500]);traceback.print_exc(file=sys.stderr);sys.exit(1)

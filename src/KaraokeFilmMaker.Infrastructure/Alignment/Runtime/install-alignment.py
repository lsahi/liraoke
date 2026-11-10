"""Pinned plugin installer: resumable public artifacts, verified before installation."""
import argparse, concurrent.futures, hashlib, json, os, pathlib, re, shutil
import subprocess, sys, threading, time, urllib.error, urllib.request, zipfile

def emit(s): print(json.dumps({'message': s}, ensure_ascii=False), flush=True)

CACHE = None
CHUNK_SIZE = 16 * 1024 * 1024
PARALLEL_THRESHOLD = 32 * 1024 * 1024
WORKERS = 8
ATTEMPTS = 3

def verified(path, checksum, size):
    if not path.is_file() or path.stat().st_size != size: return False
    with path.open('rb') as f: return hashlib.file_digest(f, 'sha256').hexdigest() == checksum

def publish_file(source, dest):
    # Preserve an existing destination if copying is interrupted.
    temporary = dest.with_name(dest.name + '.copying')
    try:
        shutil.copyfile(source, temporary)
        os.replace(temporary, dest)
    finally:
        temporary.unlink(missing_ok=True)

class DownloadProgress:
    def __init__(self, name, size, resumed=0):
        self.name, self.size, self.resumed = name, size, resumed
        self.count, self.started, self.last = resumed, time.monotonic(), 0
        self.lock = threading.Lock()

    def add(self, count):
        with self.lock:
            self.count += count
            now = time.monotonic()
            if now - self.last < 1 and self.count != self.size: return
            self.last = now
            speed = max(0, self.count-self.resumed)/max(now-self.started, 0.01)
            eta = f' · 约剩 {max(0,self.size-self.count)/speed/60:.1f} 分钟' if speed > 0 else ''
            emit(f'下载 {self.name}：{self.count/1048576:.1f}/{self.size/1048576:.1f} MiB · {speed/1048576:.2f} MiB/s{eta}')

def request(url, start=None, end=None):
    headers = {'Accept-Encoding': 'identity'}
    if start is not None: headers['Range'] = f'bytes={start}-{end}'
    return urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=60)

def check_range(response, start, end, size):
    expected = f'bytes {start}-{end}/{size}'
    if response.status != 206 or response.headers.get('Content-Range') != expected:
        raise RuntimeError('分块范围不匹配，未接受下载内容')
    if response.headers.get('Content-Encoding', 'identity') != 'identity':
        raise RuntimeError('分块响应编码不匹配')

def supports_ranges(url, size):
    for attempt in range(ATTEMPTS):
        try:
            with request(url, 0, 0) as response:
                if response.status == 200: return False
                check_range(response, 0, 0, size)
                if len(response.read(2)) != 1: raise RuntimeError('分块探测响应中断')
                return True
        except urllib.error.HTTPError as error:
            if error.code in (400, 405, 416, 501): return False
            if attempt == ATTEMPTS-1: raise
        except (OSError, RuntimeError):
            if attempt == ATTEMPTS-1: raise
    return False

def verify_download(path, checksum, size, name):
    actual=path.stat().st_size
    if actual != size:
        raise RuntimeError(f'下载校验失败：{name} 文件未完整下载，实际 {actual} 字节，预期 {size} 字节')
    if not verified(path,checksum,size):
        raise RuntimeError(f'下载校验失败：{name} 大小完整但 SHA-256 不符')

def failure_detail(error):
    if isinstance(error,RuntimeError): return str(error)
    if isinstance(error,urllib.error.HTTPError): return f'HTTP {error.code}'
    return f'网络或文件读取失败（{type(error).__name__}）'

def single_download(url, target, checksum, size, name):
    temporary = target.with_name(target.name + '.single-part')
    for attempt in range(ATTEMPTS):
        try:
            progress = DownloadProgress(name, size)
            with request(url) as response, temporary.open('wb') as output:
                total = 0
                while chunk := response.read(1024*1024):
                    total += len(chunk)
                    if total > size: raise RuntimeError('下载校验失败：文件超出预期大小')
                    output.write(chunk); progress.add(len(chunk))
            verify_download(temporary,checksum,size,name)
            os.replace(temporary, target)
            return
        except Exception as error:
            emit(f'下载失败 {attempt+1}/{ATTEMPTS}：{name} · {failure_detail(error)}')
            if attempt == ATTEMPTS-1: raise
            emit(f'下载中断或校验失败，重试 {attempt+1}/2：{name}')
        finally:
            temporary.unlink(missing_ok=True)

def parallel_download(url, target, checksum, size, name):
    # Hash-addressed pieces survive a cancelled installation and a new runtime directory.
    pieces = target.with_name(target.name + '.parts-v1')
    pieces.mkdir(parents=True, exist_ok=True)
    blocks = (size+CHUNK_SIZE-1)//CHUNK_SIZE
    def length(i): return min(CHUNK_SIZE, size-i*CHUNK_SIZE)
    def ready(i):
        part, receipt = pieces/str(i), pieces/(str(i)+'.sha256')
        try: return verified(part, receipt.read_text(encoding='ascii'), length(i))
        except (OSError, UnicodeError): return False
    for repair in range(ATTEMPTS):
        completed = {i for i in range(blocks) if ready(i)}
        resumed = sum(length(i) for i in completed)
        emit(f'分块下载：{name} · 最多 {WORKERS} 路并发 · 已复用 {len(completed)}/{blocks} 块（{resumed/1048576:.1f} MiB）')
        progress = DownloadProgress(name, size, resumed)
        stop = threading.Event()
        def fetch(i):
            if i in completed: return
            start, end = i*CHUNK_SIZE, i*CHUNK_SIZE+length(i)-1
            part, temporary = pieces/str(i), pieces/(str(i)+'.partial')
            for attempt in range(ATTEMPTS):
                if stop.is_set(): return
                counted = 0
                try:
                    h = hashlib.sha256()
                    with request(url, start, end) as response, temporary.open('wb') as output:
                        check_range(response, start, end, size)
                        while chunk := response.read(min(1024*1024, length(i)-counted+1)):
                            if stop.is_set(): return
                            if counted+len(chunk) > length(i): raise RuntimeError('分块超出预期大小')
                            output.write(chunk); h.update(chunk); counted += len(chunk); progress.add(len(chunk))
                    if counted != length(i): raise RuntimeError(f'分块响应中断：实际 {counted} 字节，预期 {length(i)} 字节')
                    os.replace(temporary, part)
                    receipt = pieces/(str(i)+'.sha256.tmp')
                    receipt.write_text(h.hexdigest(), encoding='ascii')
                    os.replace(receipt, pieces/(str(i)+'.sha256'))
                    return
                except Exception as error:
                    emit(f'分块 {i+1}/{blocks} 失败 {attempt+1}/{ATTEMPTS}：{failure_detail(error)}')
                    progress.add(-counted)
                    if attempt == ATTEMPTS-1: raise
                    emit(f'重试分块 {i+1}/{blocks}（{attempt+1}/2）：{name}')
                finally:
                    temporary.unlink(missing_ok=True)
        with concurrent.futures.ThreadPoolExecutor(max_workers=WORKERS) as pool:
            futures = [pool.submit(fetch,i) for i in range(blocks) if i not in completed]
            try:
                for future in concurrent.futures.as_completed(futures): future.result()
            except BaseException:
                stop.set()
                for future in futures: future.cancel()
                raise
        assembled = target.with_name(target.name + '.assembling')
        emit('分块已完成，正在合并并校验：' + name)
        try:
            digest = hashlib.sha256()
            with assembled.open('wb') as output:
                for i in range(blocks):
                    with (pieces/str(i)).open('rb') as part:
                        while chunk := part.read(1024*1024): output.write(chunk); digest.update(chunk)
            if assembled.stat().st_size == size and digest.hexdigest() == checksum:
                os.replace(assembled, target)
                # Only our fixed numeric piece files; never recursively delete a supplied directory.
                for i in range(blocks):
                    (pieces/str(i)).unlink(missing_ok=True)
                    (pieces/(str(i)+'.sha256')).unlink(missing_ok=True)
                return
            for i in range(blocks):
                (pieces/str(i)).unlink(missing_ok=True)
                (pieces/(str(i)+'.sha256')).unlink(missing_ok=True)
            if repair == ATTEMPTS-1: raise RuntimeError('下载校验失败：'+name+' 大小完整但 SHA-256 不符')
            emit('整文件校验失败，重新获取分块：' + name)
        finally:
            assembled.unlink(missing_ok=True)

def reuse_pip_cache(cache, target, checksum, size, name):
    # pip's HTTP cache can contain a fully downloaded wheel from an older app.
    # Search only our managed pip cache; never guess from a virtual environment.
    pip_cache=cache.parent/'pip'
    if size < PARALLEL_THRESHOLD or not pip_cache.is_dir(): return False
    for candidate in pip_cache.rglob('*.body'):
        if candidate.stat().st_size != size: continue
        emit('检查已有 pip 下载缓存：' + name)
        if not verified(candidate,checksum,size): continue
        temporary=target.with_name(target.name+'.pip-copy')
        try:
            shutil.copyfile(candidate,temporary)
            if not verified(temporary,checksum,size): continue
            os.replace(temporary,target)
            emit('复用已校验 pip 下载：' + name)
            return True
        finally:
            temporary.unlink(missing_ok=True)
    return False

def download(url, dest, checksum, size):
    if not re.fullmatch('[0-9a-f]{64}', checksum) or size < 0: raise RuntimeError('下载清单无效')
    cache = CACHE or dest.parent/'download-cache'
    cache.mkdir(parents=True, exist_ok=True)
    target = cache/checksum
    if verified(target, checksum, size) or reuse_pip_cache(cache, target, checksum, size, dest.name):
        emit('复用已校验下载：' + dest.name)
    else:
        if size >= PARALLEL_THRESHOLD and supports_ranges(url, size):
            parallel_download(url, target, checksum, size, dest.name)
        else:
            if size >= PARALLEL_THRESHOLD: emit('服务器不支持分块，回退单连接下载：' + dest.name)
            single_download(url, target, checksum, size, dest.name)
    publish_file(target, dest)

def extract(source,target):
    with zipfile.ZipFile(source) as z:
        for f in z.infolist():
            p=(target/f.filename).resolve()
            if not p.is_relative_to(target.resolve()) or (f.external_attr>>16)&0o170000==0o120000: raise RuntimeError('压缩包路径无效')
        z.extractall(target)
def run(a):
    global CACHE
    root=pathlib.Path(a.runtime).resolve();manifest=json.loads(pathlib.Path(a.manifest).read_text(encoding='utf-8'))
    CACHE=root.parent/'cache'/'downloads';CACHE.mkdir(parents=True,exist_ok=True)
    pip=root/'pip-25.3-py3-none-any.whl';v=manifest['pip'];download(v['url'],pip,v['sha256'],v['bytes'])
    site=root/'python/Lib/site-packages';site.mkdir(parents=True,exist_ok=True)
    extract(pip,site)
    python=root/'python/python.exe'
    env=dict(os.environ,PYTHONNOUSERSITE='1',PYTHONUTF8='1',PIP_CONFIG_FILE=os.devnull)
    def install(*packages, index='https://pypi.org/simple'):
        cmd=[str(python),'-m','pip','--isolated','install','--disable-pip-version-check','--progress-bar','raw','--cache-dir',str(root.parent/'cache'/'pip'),'--only-binary=:all:','--index-url',index,*packages]
        subprocess.run(cmd,env=env,check=True)
    emit('安装独立推理依赖，首次下载可能需要较长时间…')
    torch_spec=manifest['torch'][a.device]
    wheel=root/torch_spec['name']
    download(torch_spec['url'],wheel,torch_spec['sha256'],torch_spec['bytes'])
    install(str(wheel),index='https://download.pytorch.org/whl/'+('cu128' if a.device=='gpu' else 'cpu'))
    wheel.unlink()  # The verified archive remains in the shared cache.
    install('transformers=='+('4.57.6' if a.plugin=='nextfire' else '5.22.2'),'numpy==1.26.4','soundfile==0.13.1','pydantic==2.12.5')
    if a.plugin=='nextfire':
        archive=root/'upstream.zip';v=manifest['milikara'];download(v['url'],archive,v['sha256'],v['bytes'])
        unpack=root/'unpack';extract(archive,unpack)
        source=next(unpack.glob('MiliKara-*'));shutil.move(str(source),str(root/'upstream'))
        archive.unlink()
    spec=manifest[a.plugin];model=root/'model';model.mkdir()
    for f in spec['files']:
        emit('下载模型文件：'+f['name'])
        download('https://huggingface.co/'+spec['repo']+'/resolve/'+spec['revision']+'/'+f['name'],model/f['name'],f['sha256'],f['bytes'])
    emit('安装文件已校验，正在加载模型自检…')
    subprocess.run([str(python),a.worker,'--plugin',a.plugin,'--runtime',str(root),'--device',a.device,'--self-test'],env=env,check=True)
    (root/'installed.json').write_text(json.dumps({'version':1,'plugin':a.plugin,'device':a.device,'revision':spec['revision'],'selfTestPassed':True}),encoding='utf-8')
    emit('安装和模型自检完成')
if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('--plugin',choices=['nextfire','qwen'],required=True);p.add_argument('--device',choices=['cpu','gpu'],required=True);p.add_argument('--runtime',required=True);p.add_argument('--manifest',required=True);p.add_argument('--worker',required=True)
    try: run(p.parse_args())
    except Exception as e: emit('安装失败：'+str(e)[:500]);sys.exit(1)

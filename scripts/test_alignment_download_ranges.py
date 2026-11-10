"""Real loopback HTTP tests; no external services or large downloads."""
import collections, concurrent.futures, contextlib, hashlib, importlib.util
import http.server, pathlib, subprocess, sys, tempfile, threading, time, unittest
from unittest.mock import patch

ROOT=pathlib.Path(__file__).resolve().parents[1]
SCRIPT=ROOT/'src/KaraokeFilmMaker.Infrastructure/Alignment/Runtime/install-alignment.py'
spec=importlib.util.spec_from_file_location('downloader',SCRIPT)
installer=importlib.util.module_from_spec(spec);spec.loader.exec_module(installer)

class DownloadTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.root=pathlib.Path(self.temp.name)
        self.data=bytes(range(256))*512;self.digest=hashlib.sha256(self.data).hexdigest()
        self.block=16384;self.requests=collections.Counter();self.lock=threading.Lock()
        self.active=0;self.peak=0;self.fail=set();self.ignore=False;self.bad_range=False;self.corrupt=False;self.truncate_once=False;self.always_truncate=False;self.slow=False
        fixture=self
        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self,*args): pass
            def do_GET(self):
                interval=self.headers.get('Range')
                start,end=(map(int,interval[6:].split('-')) if interval else (0,len(fixture.data)-1))
                with fixture.lock:
                    fixture.requests[(start,end)]+=1;fixture.active+=1;fixture.peak=max(fixture.peak,fixture.active)
                try:
                    if start in fixture.fail and end>0:
                        self.send_error(503);return
                    time.sleep(0.03 if not fixture.slow or start==0 else 1)
                    payload=fixture.data if fixture.ignore else fixture.data[start:end+1]
                    if fixture.corrupt: payload=b'x'*len(payload)
                    self.send_response(200 if fixture.ignore or not interval else 206)
                    if interval and not fixture.ignore:self.send_header('Content-Range',f'bytes {start+1 if fixture.bad_range else start}-{end}/{len(fixture.data)}')
                    self.send_header('Content-Length',str(len(payload)));self.end_headers()
                    if (fixture.truncate_once or fixture.always_truncate) and end>0:
                        fixture.truncate_once=False;self.wfile.write(payload[:100]);return
                    try:self.wfile.write(payload)
                    except (BrokenPipeError,ConnectionResetError,ConnectionAbortedError):pass
                except (BrokenPipeError,ConnectionResetError,ConnectionAbortedError):pass
                finally:
                    with fixture.lock:fixture.active-=1
        self.server=http.server.ThreadingHTTPServer(('127.0.0.1',0),Handler)
        self.thread=threading.Thread(target=self.server.serve_forever,daemon=True);self.thread.start()
        self.url=f'http://127.0.0.1:{self.server.server_port}/artifact'
        installer.CACHE=self.root/'cache';installer.CHUNK_SIZE=self.block;installer.PARALLEL_THRESHOLD=self.block;installer.WORKERS=4
        self.output=self.root/'model.bin'
        self.quiet=patch.object(installer,'emit');self.quiet.start()

    def tearDown(self):
        self.server.shutdown();self.server.server_close();self.thread.join();self.quiet.stop();self.temp.cleanup()

    def download(self):installer.download(self.url,self.output,self.digest,len(self.data))

    def test_parallel_correct_bytes_and_verified_cache_avoids_network(self):
        self.download();self.assertEqual(self.data,self.output.read_bytes());self.assertGreater(self.peak,1)
        self.assertEqual(1,self.requests[(0,0)])
        for i in range(8):self.assertEqual(1,self.requests[(i*self.block,(i+1)*self.block-1)])
        count=sum(self.requests.values());self.output.unlink();self.download();self.assertEqual(count,sum(self.requests.values()))

    def test_previous_pip_complete_download_is_verified_and_reused_offline(self):
        pip_cache=installer.CACHE.parent/'pip'/'http-v2'/'nested';pip_cache.mkdir(parents=True)
        (pip_cache/'artifact.body').write_bytes(self.data)
        with patch.object(installer.urllib.request,'urlopen',side_effect=AssertionError('network forbidden')):self.download()
        self.assertEqual(self.data,self.output.read_bytes())
        self.assertEqual(self.data,(installer.CACHE/self.digest).read_bytes())

    def test_wrong_pip_cache_content_is_not_trusted_by_size(self):
        pip_cache=installer.CACHE.parent/'pip';pip_cache.mkdir()
        (pip_cache/'artifact.body').write_bytes(b'x'*len(self.data))
        self.download();self.assertEqual(self.data,self.output.read_bytes());self.assertTrue(self.requests)

    def test_failed_chunk_retains_completed_chunks_and_retry_is_bounded(self):
        self.fail={7*self.block}
        with self.assertRaises(Exception):self.download()
        self.assertEqual(3,self.requests[(7*self.block,8*self.block-1)])
        self.assertFalse(self.output.exists())
        pieces=installer.CACHE/(self.digest+'.parts-v1')
        completed=[int(p.name) for p in pieces.iterdir() if p.name.isdecimal()]
        self.assertTrue(completed)
        previous=dict(self.requests);self.fail.clear();self.download()
        for i in completed:self.assertEqual(previous[(i*self.block,(i+1)*self.block-1)],self.requests[(i*self.block,(i+1)*self.block-1)])
        self.assertEqual(self.data,self.output.read_bytes())

    def test_server_ignoring_ranges_falls_back_without_concatenating_whole_files(self):
        self.ignore=True;self.download();self.assertEqual(self.data,self.output.read_bytes())
        self.assertEqual(2,sum(self.requests.values()))

    def test_incorrect_content_range_is_rejected(self):
        self.bad_range=True
        with self.assertRaisesRegex(RuntimeError,'范围不匹配'):self.download()
        self.assertFalse(self.output.exists())

    def test_partial_response_is_retried(self):
        self.truncate_once=True;self.download();self.assertEqual(self.data,self.output.read_bytes())
        self.assertGreater(sum(self.requests.values()),9)

    def test_persistent_truncation_cannot_become_a_verified_model(self):
        self.always_truncate=True;self.output.write_bytes(b'old model')
        with self.assertRaises(Exception):self.download()
        self.assertFalse((installer.CACHE/self.digest).exists());self.assertEqual(b'old model',self.output.read_bytes())
        data_requests=[count for (start,end),count in self.requests.items() if end>0]
        self.assertTrue(data_requests);self.assertLessEqual(max(data_requests),3)

    def test_no_range_short_body_reports_actual_expected_bytes(self):
        self.ignore=True;self.always_truncate=True
        with self.assertRaisesRegex(RuntimeError,'实际 100 字节，预期 131072 字节'):self.download()
        self.assertFalse((installer.CACHE/self.digest).exists());self.assertFalse(self.output.exists())

    def test_wrong_whole_file_hash_never_replaces_destination(self):
        self.corrupt=True;self.output.write_bytes(b'previous')
        with self.assertRaisesRegex(RuntimeError,'校验失败'):self.download()
        self.assertEqual(b'previous',self.output.read_bytes());self.assertFalse((installer.CACHE/self.digest).exists())

    def test_corrupted_saved_piece_is_refetched(self):
        self.fail={7*self.block}
        with self.assertRaises(Exception):self.download()
        pieces=installer.CACHE/(self.digest+'.parts-v1')
        victim=next(p for p in pieces.iterdir() if p.name.isdecimal());i=int(victim.name)
        count=self.requests[(i*self.block,(i+1)*self.block-1)];victim.write_bytes(b'x'*self.block)
        self.fail.clear();self.download();self.assertEqual(count+1,self.requests[(i*self.block,(i+1)*self.block-1)])

    def test_killed_process_resumes_in_a_new_process_and_new_runtime_directory(self):
        self.slow=True
        bootstrap='''import importlib.util,pathlib,sys
s=importlib.util.spec_from_file_location('install',sys.argv[1]);m=importlib.util.module_from_spec(s);s.loader.exec_module(m)
m.CACHE=pathlib.Path(sys.argv[2]);m.CHUNK_SIZE=16384;m.PARALLEL_THRESHOLD=16384;m.WORKERS=2
m.download(sys.argv[3],pathlib.Path(sys.argv[4]),sys.argv[5],131072)
'''
        command=[sys.executable,'-c',bootstrap,str(SCRIPT),str(installer.CACHE),self.url,str(self.output),self.digest]
        child=subprocess.Popen(command,stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)
        try:
            receipt=installer.CACHE/(self.digest+'.parts-v1')/'0.sha256';deadline=time.monotonic()+10
            while not receipt.exists() and time.monotonic()<deadline:time.sleep(0.02)
            self.assertTrue(receipt.exists());child.kill();child.wait(timeout=5)
        finally:
            if child.poll() is None:child.kill();child.wait(timeout=5)
        before=self.requests[(0,self.block-1)]
        self.slow=False;command[-2]=str(self.root/'new-runtime-model.bin')
        done=subprocess.run(command,stdout=subprocess.PIPE,stderr=subprocess.PIPE,timeout=15)
        self.assertEqual(0,done.returncode,done.stderr.decode(errors='replace'))
        self.assertEqual(before,self.requests[(0,self.block-1)])
        self.assertEqual(self.data,(self.root/'new-runtime-model.bin').read_bytes())

if __name__=='__main__':unittest.main()

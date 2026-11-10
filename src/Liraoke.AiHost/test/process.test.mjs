import {test} from 'node:test';
import assert from 'node:assert/strict';
import {spawn} from 'node:child_process';
import {createServer} from 'node:http';
import {once} from 'node:events';
import {resolve} from 'node:path';
import {fileURLToPath} from 'node:url';

test('bundled standalone host uses stdio and real HTTP SSE without npm modules',async()=>{
 const server=createServer((req,res)=>{
  assert.equal(req.url,'/custom/completion');let body='';req.on('data',c=>body+=c);req.on('end',()=>{
   const request=JSON.parse(body);assert.equal(request.stream,true);assert.equal(request.messages.at(-1).content,'你好');
   res.writeHead(200,{'Content-Type':'text/event-stream'});
   res.end('data: {"choices":[{"delta":{"reasoning_content":"思考"}}]}\n\ndata: {"choices":[{"delta":{"content":"你好"}}]}\n\ndata: [DONE]\n\n');
  });
 });server.listen(0,'127.0.0.1');await once(server,'listening');
 const host=resolve(fileURLToPath(new URL('../../../',import.meta.url)),'tools/ai-runtime/host.mjs');
 const child=spawn(process.execPath,[host],{stdio:['pipe','pipe','pipe']});let output='',errors='';
 child.stdout.on('data',d=>output+=d);child.stderr.on('data',d=>errors+=d);
 try{
  child.stdin.write(JSON.stringify({connection:{model:'mock',protocol:'deepseek',endpoint:`http://127.0.0.1:${server.address().port}/custom/completion`,apiKey:'fake-process-secret'},history:[],prompt:'你好'})+'\n');
  const [exit]=await once(child,'exit');assert.equal(exit,0,errors);assert.equal(errors,'');
  const events=output.trim().split('\n').map(l=>JSON.parse(l));assert.deepEqual(events.map(x=>x.type),['thinking_started','thinking','text','done']);assert.ok(!output.includes('fake-process-secret'));
 }finally{child.kill();server.close();}
});


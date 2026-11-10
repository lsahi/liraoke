import {test} from 'node:test';
import assert from 'node:assert/strict';
import {run} from '../host.mjs';
import {sse} from '../adapters.mjs';
const connection={endpoint:'https://test.invalid/custom/complete',model:'example',protocol:'openai',apiKey:'never-log-this-secret'};
function response(events) {return new Response(events.map(e=>'data: '+(typeof e==='string'?e:JSON.stringify(e))+'\r\n\r\n').join(''),{headers:{'content-type':'text/event-stream'}});}
test('Pi + Cordis preserve history, exact endpoint, reasoning and text; no tools',async()=>{
 let request;const events=[];
 await run({connection,history:[{role:'user',text:'previous'},{role:'assistant',text:'answer'}],prompt:'你好'},e=>events.push(e),undefined,async(url,init)=>{
  request=JSON.parse(init.body);assert.equal(url,connection.endpoint);assert.equal(init.headers.Authorization,'Bearer '+connection.apiKey);assert.equal(init.redirect,'error');
  return response([{choices:[{delta:{reasoning_content:'分析'}}]},{choices:[{delta:{content:'你好'}}]},{choices:[{delta:{},finish_reason:'stop'}]},'[DONE]']);
 });
 assert.deepEqual(request.messages.slice(-3).map(m=>m.content),['previous','answer','你好']);assert.equal(request.tools,undefined);assert.equal(request.stream,true);
 assert.deepEqual(events.map(e=>e.type),['thinking_started','thinking','text','done']);
});
test('DeepSeek adds thinking to compatible request, Claude uses native auth and stream',async()=>{
 for(const protocol of ['deepseek','anthropic']) {
  const events=[];await run({connection:{...connection,protocol},prompt:'hello'},e=>events.push(e),undefined,async(_,init)=>{
   const b=JSON.parse(init.body);assert.equal(b.thinking.type,'enabled');
   if(protocol==='anthropic') {assert.equal(init.headers['x-api-key'],connection.apiKey);assert.equal(init.headers.Authorization,undefined);assert.ok(b.system);assert.ok(b.messages.every(m=>m.role!=='system'));
    return response([{type:'content_block_delta',delta:{type:'thinking_delta',thinking:'Think'}},{type:'content_block_delta',delta:{type:'text_delta',text:'Hello'}},{type:'message_stop'}]);}
   return response([{choices:[{delta:{content:'Hello'}}]},'[DONE]']);
  });assert.equal(events.at(-1).type,'done');
 }
});
test('network/HTTP errors do not expose response bodies or credentials',async()=>{
 const events=[];await run({connection,prompt:'hello'},e=>events.push(e),undefined,async()=>new Response(connection.apiKey,{status:401}));
 assert.equal(events.at(-1).type,'error');assert.match(events.at(-1).text,/401/);assert.ok(!JSON.stringify(events).includes(connection.apiKey));
});
test('truncated streams are failures, not successful connectivity',async()=>{
 const events=[];await run({connection,prompt:'hello'},e=>events.push(e),undefined,async()=>response([{choices:[{delta:{content:'partial'}}]}]));
 assert.equal(events.at(-1).type,'error');assert.match(events.at(-1).text,/中断/);
});
test('cancellation aborts the actual request',async()=>{
 const abort=new AbortController();let entered;const ready=new Promise(r=>entered=r);const events=[];
 const work=run({connection,prompt:'hello'},e=>events.push(e),abort.signal,(_,init)=>new Promise((_,reject)=>{init.signal.addEventListener('abort',()=>reject(new Error('aborted')));entered();}));
 await ready;abort.abort();await work;assert.equal(events.at(-1).type,'error');assert.match(events.at(-1).text,/停止/);
});
test('SSE survives fragmented UTF8 and CRLF',async()=>{
 const bytes=new TextEncoder().encode('data: {"text":"你好"}\r\n\r\n');const body=new ReadableStream({start(c){for(const byte of bytes)c.enqueue(Uint8Array.of(byte));c.close();}});
 const events=[];for await(const e of sse(body))events.push(JSON.parse(e));assert.equal(events[0].text,'你好');
});

test('restored tool outcomes and explicit user memory reach the next model request',async()=>{
 let payload;
 await run({connection,userMemory:['prefer GPU'],history:[{role:'context',text:'installation completed: qwen'},{role:'assistant',text:'partial response interrupted'}],prompt:'continue'},()=>{},undefined,async(_,init)=>{
  payload=JSON.parse(init.body);return response([{choices:[{delta:{content:'ready'}}]},'[DONE]']);
 });
 assert.ok(payload.messages.some(m=>m.role==='system' && m.content.includes('prefer GPU')));
 assert.ok(payload.messages.some(m=>m.role==='user' && m.content.includes('installation completed: qwen')));
 assert.ok(payload.messages.some(m=>m.role==='assistant' && m.content.includes('partial response interrupted')));
});

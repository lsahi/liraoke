import test from 'node:test';import assert from 'node:assert/strict';
import {buildQishuiSignerPayload,checkQishuiSigner,generateQishuiSignatureHeaders,validateSignerEndpoint} from '../vendor/meting/providers/qishui/signature.js';
test('Signer never receives Cookie or previous signature headers',()=>{
 const payload=buildQishuiSignerPayload({deviceId:'1',url:'https://api.qishui.com/x',headers:{Cookie:'secret','X-Medusa':'secret',Host:'host',Accept:'application/json'}});
 assert.deepEqual(payload.headers,['accept','application/json']);assert.ok(!JSON.stringify(payload).includes('secret'));
});
test('Validate endpoint without sending a request',()=>{
 for(const url of ['http://remote.example/sign','file:///a','https://u:p@example.com/sign','https://example.com/#secret']) assert.throws(()=>validateSignerEndpoint(url),/SIGNER_ENDPOINT/);
 for(const url of ['https://example.com/sign','http://127.0.0.1:8899/sign','http://[::1]:8899/sign']) assert.equal(validateSignerEndpoint(url),url);
});
test('Probe uses synthetic input and bounded redirect-free transport',async t=>{
 t.mock.method(globalThis,'fetch',async(url,options)=>{
  assert.equal(options.redirect,'manual'); assert.ok(options.signal);
  const payload=JSON.parse(options.body);assert.equal(payload.deviceId,'1000000000000000');assert.ok(!options.body.includes('Cookie'));
  return Response.json({ok:true,'X-Helios':'test','X-Medusa':'test'});
 });
 assert.equal(await checkQishuiSigner('https://example.com/sign'),true);
});
test('Authentication, redirect and malformed responses produce redacted errors',async t=>{
 let response;
 t.mock.method(globalThis,'fetch',async()=>response);
 for(const [r,code] of [[new Response('private details',{status:401}),'AUTH'],[new Response('',{status:302}),'REDIRECT'],
  [Response.json({ok:true,'X-Helios':'x','X-Medusa':'x\r\nsecret'}),'PROTOCOL'],[new Response('not-json'),'PROTOCOL'],
  [new Response('x'.repeat(65537)),'PROTOCOL'],[Response.json({ok:false,error:'private details'}),'PROTOCOL']]){
  response=r;await assert.rejects(()=>checkQishuiSigner('https://example.com/sign'),new RegExp('^Error: SIGNER_'+code+'$'));
 }
});
test('Resolve signing shares the same endpoint checks; failures are categorized',async t=>{
 t.mock.method(globalThis,'fetch',async()=>{throw new DOMException('secret','TimeoutError')});
 await assert.rejects(()=>generateQishuiSignatureHeaders({url:'https://api.qishui.com/a',headers:{Cookie:'secret'},deviceId:'1',signerUrl:'https://example.com/sign'}),/SIGNER_TIMEOUT/);
});

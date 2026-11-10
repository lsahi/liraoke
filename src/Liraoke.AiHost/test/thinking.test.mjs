import {test} from 'node:test';
import assert from 'node:assert/strict';
import {ThinkingDisplayFilter,HIDDEN_THINKING} from '../thinking-display.mjs';
import {run} from '../host.mjs';
const prompt='你是 Liraoke 的 AI 助手。使用用户的语言回答。目前只支持聊天，无法读取或修改本地项目、文件、歌词或时间轴；不要声称已执行这些操作。';
test('normal thinking is displayed sentence by sentence; final tail can be flushed before body',()=>{
 const seen=[]; const f=new ThinkingDisplayFilter(prompt,s=>seen.push(s));
 f.push('先确认');assert.deepEqual(seen,[]);f.push('用户的问题。');assert.deepEqual(seen,['先确认用户的问题。']);
 f.push('再介绍软件');f.flush();assert.equal(seen.join(''),'先确认用户的问题。再介绍软件');
});
test('prompt quotation never flashes even when split at every possible chunk boundary',()=>{
 for(const quote of [prompt,'The system says: '+prompt,'We have system prompt: '+prompt,'系统提示要求：'+prompt,'你 是 Liraoke 的 AI 助 手。']) {
  for(let split=0;split<=quote.length;split++) {
   const seen=[];const f=new ThinkingDisplayFilter(prompt,s=>seen.push(s));
   f.push(quote.slice(0,split));f.push(quote.slice(split));f.flush();
   assert.equal(seen.join(''),HIDDEN_THINKING);
  }
 }
});
test('after an instruction reference its remaining paragraph and later paragraphs stay hidden',()=>{
 const seen=[];const f=new ThinkingDisplayFilter(prompt,s=>seen.push(s));
 for(const c of '分析用户的问题。The system says: "private instructions."\nmore internal detail.')f.push(c);
 f.flush();assert.equal(seen.join(''),'分析用户的问题。'+HIDDEN_THINKING);
});
test('oversized unpunctuated fragments are not emitted unchecked',()=>{
 const seen=[];const f=new ThinkingDisplayFilter(prompt,s=>seen.push(s));f.push('x'.repeat(5000));f.flush();assert.deepEqual(seen,[HIDDEN_THINKING]);
});
test('real agent stream filters prompt echo before stdio but keeps the answer',async()=>{
 const events=[];
 await run({connection:{endpoint:'https://test.invalid/chat',model:'mock',protocol:'openai',apiKey:'fake'},prompt:'介绍软件'},e=>events.push(e),undefined,async(_,init)=>{
  const system=JSON.parse(init.body).messages.find(m=>m.role==='system').content;
  const packets=[...('The system says: '+system)].map(c=>({choices:[{delta:{reasoning_content:c}}]}));
  packets.push({choices:[{delta:{content:'Liraoke 是卡拉 OK 视频制作工具。'}}]});
  return new Response(packets.map(p=>'data: '+JSON.stringify(p)+'\n\n').join('')+'data: [DONE]\n\n',{headers:{'content-type':'text/event-stream'}});
 });
 assert.equal(events.filter(e=>e.type==='thinking').map(e=>e.text).join(''),HIDDEN_THINKING);
 assert.equal(events.find(e=>e.type==='text').text,'Liraoke 是卡拉 OK 视频制作工具。');assert.equal(events.at(-1).type,'done');
});

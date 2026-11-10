import test from 'node:test';import assert from 'node:assert/strict';import {run} from '../host.mjs';
const response=events=>new Response(events.map(x=>'data: '+JSON.stringify(x)+'\n\n').join(''),{headers:{'content-type':'text/event-stream'}});
for(const protocol of ['openai','deepseek','anthropic'])test(protocol+' executes a tool then streams the answer',async()=>{
 let rounds=0,executions=0;const events=[];
 await run({connection:{protocol,model:'mock',endpoint:'https://example.test/chat',apiKey:'secret'},prompt:'查询项目',tools:[{name:'get_project_context',description:'project',parameters:{type:'object',properties:{},required:[]}}]},e=>events.push(e),undefined,async(_,options)=>{
  const body=JSON.parse(options.body);assert.equal(body.tools.length,1);rounds++;
  if(rounds===1)return protocol==='anthropic'?response([{type:'content_block_start',index:0,content_block:{type:'tool_use',id:'call1',name:'get_project_context',input:{}}},{type:'content_block_delta',index:0,delta:{type:'input_json_delta',partial_json:'{}'}},{type:'message_delta',delta:{stop_reason:'tool_use'}},{type:'message_stop'}]):response([{choices:[{delta:{tool_calls:[{index:0,id:'call1',function:{name:'get_project_context',arguments:'{'}}]}}]},{choices:[{delta:{tool_calls:[{index:0,function:{arguments:'}'}}]},finish_reason:'tool_calls'}]}]);
  assert.ok(JSON.stringify(body.messages).includes('Test Project'));
  return protocol==='anthropic'?response([{type:'content_block_delta',delta:{text:'项目是 Test Project'}},{type:'message_stop'}]):response([{choices:[{delta:{content:'项目是 Test Project'},finish_reason:'stop'}]}]);
 },async(id,name,args)=>{assert.equal(id,'call1');assert.equal(name,'get_project_context');assert.deepEqual(args,{});executions++;return {ok:true,title:'Test Project'}});
 assert.equal(rounds,2);assert.equal(executions,1);assert.equal(events.at(-1).type,'done');
});

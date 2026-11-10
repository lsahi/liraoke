import { Agent as HttpAgent } from 'undici';
export class ChatHttpAgent extends HttpAgent {
  constructor(){super({connect:{timeout:0},headersTimeout:0,bodyTimeout:0});}
  dispatch(options,handler){return super.dispatch({...options,headersTimeout:0,bodyTimeout:0},handler);}
}
const chatDispatcher=new ChatHttpAgent();
import { AssistantMessageEventStream, getCurrentTools } from '@earendil-works/pi-ai';

// The full endpoint is used verbatim. No SDK suffixes or redirects can move credentials.
export function validateConnection(c) {
  const url = new URL(c.endpoint);
  if ((url.protocol !== 'https:' && !(url.protocol === 'http:' && ['localhost','127.0.0.1','[::1]'].includes(url.hostname))) || url.username || url.password || url.hash)
    throw new Error('请使用 HTTPS 完整接口地址（本机服务允许 HTTP），且不要在地址中填写账户密码。');
  if (!c.model?.trim() || !c.apiKey?.trim()) throw new Error('请填写模型和 APIKey。');
  if (!['openai','deepseek','anthropic'].includes(c.protocol)) throw new Error('不支持此 API 协议。');
}

export async function* sse(body) {
  let buffer = ''; const decoder = new TextDecoder();
  for await (const bytes of body) {
    buffer += decoder.decode(bytes, { stream:true });
    if (buffer.length > 2_000_000) throw new Error('接口单个事件过大。');
    let match;
    while ((match = /\r?\n\r?\n/.exec(buffer))) {
      const packet = buffer.slice(0,match.index); buffer = buffer.slice(match.index+match[0].length);
      const data = packet.split(/\r?\n/).filter(x=>x.startsWith('data:')).map(x=>x.slice(5).trimStart()).join('\n');
      if (data) yield data;
    }
  }
  if (buffer.trim() && !buffer.trim().startsWith(':')) throw new Error('流式响应意外中断，请重试。');
}

export function createAdapter(c, fetchFn = fetch) {
  validateConnection(c);
  return (model, context, options) => {
    const stream = new AssistantMessageEventStream();
    const message = {role:'assistant', content:[], api:model.api, provider:model.provider, model:model.id,
      timestamp:Date.now(), stopReason:'stop', usage:{input:0,output:0,cacheRead:0,cacheWrite:0,totalTokens:0,cost:{input:0,output:0,cacheRead:0,cacheWrite:0,total:0}}};
    const add = (type, text) => {
      if (typeof text !== 'string' || !text) return;
      let index=message.content.findIndex(b=>b.type===type);
      if (index<0) { index=message.content.length; message.content.push(type==='text'?{type,text:''}:{type,thinking:''}); stream.push({type:type+'_start',contentIndex:index,partial:message}); }
      message.content[index][type==='text'?'text':'thinking']+=text;
      stream.push({type:type+'_delta',delta:text,contentIndex:index,partial:message});
    };
    void (async()=>{
      try {
        const native=c.protocol==='anthropic';
        const messages=context.messages.map(m=>{
          if(m.role==='toolResult') return native?{role:'user',content:[{type:'tool_result',tool_use_id:m.toolCallId,content:m.content.map(b=>b.text??'').join('')}]}:{role:'tool',tool_call_id:m.toolCallId,content:m.content.map(b=>b.text??'').join('')};
          if(typeof m.content==='string')return {role:m.role,content:m.content};
          if(native)return {role:m.role,content:m.content.map(b=>b.type==='toolCall'?{type:'tool_use',id:b.id,name:b.name,input:b.arguments}:b.type==='thinking'?{type:'thinking',thinking:b.thinking,signature:b.signature??''}:{type:'text',text:b.text??''})};
          const result={role:m.role,content:m.content.filter(b=>b.type==='text').map(b=>b.text).join('')};
          const calls=m.content.filter(b=>b.type==='toolCall');if(calls.length)result.tool_calls=calls.map(b=>({id:b.id,type:'function',function:{name:b.name,arguments:JSON.stringify(b.arguments)}}));
          const thinking=m.content.filter(b=>b.type==='thinking').map(b=>b.thinking).join('');if(thinking)result.reasoning_content=thinking;
          return result;
        });
        const headers={'Content-Type':'application/json',Accept:'text/event-stream'};
        if(native) {headers['x-api-key']=c.apiKey;headers['anthropic-version']='2023-06-01';}
        else headers.Authorization=`Bearer ${c.apiKey}`;
        const body={model:c.model,stream:true,messages:native?messages.filter(m=>m.role!=='system'):messages};
        const activeTools=getCurrentTools(context.messages); if(activeTools.length)body.tools=activeTools.map(t=>native?{name:t.name,description:t.description,input_schema:t.parameters}:{type:'function',function:{name:t.name,description:t.description,parameters:t.parameters}});
        if(native) {body.system=messages.filter(m=>m.role==='system').map(m=>m.content).join('\n');body.max_tokens=8192;body.thinking={type:'enabled',budget_tokens:2048};}
        if(c.protocol==='deepseek' && !['deepseek-chat','deepseek-reasoner'].includes(c.model)) body.thinking={type:'enabled'};
        const response=await fetchFn(c.endpoint,{method:'POST',headers,body:JSON.stringify(body),signal:options.signal,redirect:'error',dispatcher:chatDispatcher});
        if(!response.ok) {
          await response.body?.cancel();
          const hints={401:'APIKey 无效',403:'无权限或地域受限',404:'请检查完整接口地址和模型名',429:'额度不足或请求过于频繁'};
          throw new Error(`HTTP ${response.status}：${hints[response.status]??'服务商请求失败，请稍后重试'}`);
        }
        if(!response.headers.get('content-type')?.includes('text/event-stream')) {await response.body?.cancel();throw new Error('接口未返回 SSE 流，请检查接口协议和完整地址。');}
        stream.push({type:'start',partial:message}); let complete=false; let count=0;const calls=new Map();
        for await (const raw of sse(response.body)) {
          if(raw==='[DONE]') {complete=true;break;}
          let event; try{event=JSON.parse(raw);}catch{throw new Error('接口返回了无效的流式数据。');}
          if(event.error || event.type==='error') throw new Error('服务商在流式响应中报告错误，请检查额度或稍后重试。');
          if(native) {
            if(event.type==='content_block_start' && event.content_block?.type==='tool_use')calls.set(event.index,{id:event.content_block.id,name:event.content_block.name,json:'',initial:event.content_block.input});
            if(event.type==='content_block_delta' && event.delta?.type==='input_json_delta'){const call=calls.get(event.index);if(call)call.json+=event.delta.partial_json;}
            if(event.type==='content_block_delta' && event.delta?.type==='signature_delta'){const b=message.content.find(b=>b.type==='thinking');if(b)b.signature=(b.signature??'')+event.delta.signature;}
            if(event.type==='message_delta' && event.delta?.stop_reason==='tool_use')message.stopReason='toolUse';
            if(event.type==='content_block_delta') {add('text',event.delta?.text);add('thinking',event.delta?.thinking);}
            if(event.type==='message_delta' && event.delta?.stop_reason==='max_tokens') message.stopReason='length';
            if(event.type==='message_stop') {complete=true;break;}
          } else {
            const choice=event.choices?.[0];
            add('thinking',choice?.delta?.reasoning_content ?? choice?.delta?.reasoning);
            add('text',choice?.delta?.content ?? choice?.delta?.refusal);
            for(const part of choice?.delta?.tool_calls??[]){const call=calls.get(part.index)??{id:'',name:'',json:''};call.id+=part.id??'';call.name+=part.function?.name??'';call.json+=part.function?.arguments??'';calls.set(part.index,call);}
            if(choice?.finish_reason==='tool_calls')message.stopReason='toolUse';
            if(choice?.finish_reason) {complete=true;if(choice.finish_reason==='length')message.stopReason='length';}
          }
          count+=raw.length;if(count>4_000_000)throw new Error('本次回复超过长度限制。');
        }
        if(!complete) throw new Error('流式响应意外中断，请重试。');
        if(calls.size){if(message.stopReason==='length')throw new Error('接口工具参数被截断。');for(const call of calls.values()){if(!call.id||!call.name)throw new Error('接口工具调用缺少标识。');message.content.push({type:'toolCall',id:call.id,name:call.name,arguments:call.json?JSON.parse(call.json):(call.initial??{})});}message.stopReason='toolUse';}
        if(!calls.size && !message.content.some(b=>b.type==='text'&&b.text.trim()))throw new Error('未收到正文；请检查模型支持或重试。');
        stream.push({type:'done',reason:message.stopReason,message});
      } catch(e) {
        message.stopReason=options.signal?.aborted?'aborted':'error';
        // Never expose arbitrary network errors, response bodies, endpoint queries or credentials.
        message.errorMessage=options.signal?.aborted?'已停止生成。': /^(HTTP |接口|服务商|流式|本次|未收到|此版本)/.test(e.message)?e.message:'无法连接 AI 服务，请检查网络、代理和接口地址。';
        stream.push({type:'error',reason:message.stopReason,error:message});
      } finally {stream.end();}
    })();
    return stream;
  };
}

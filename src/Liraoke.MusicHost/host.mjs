import {checkQishuiSigner} from './vendor/meting/providers/qishui/signature.js';
import readline from 'node:readline';
import {readFile,writeFile} from 'node:fs/promises';
import {get_search_songs as searchNetease} from './vendor/meting/providers/netease/search.js';
import {get_song_url as resolveNetease} from './vendor/meting/providers/netease/song.js';
import {get_search_songs as searchTencent} from './vendor/meting/providers/tencent/search.js';
import {get_song_url as resolveTencent} from './vendor/meting/providers/tencent/song.js';
import qishui from './vendor/meting/providers/qishui/index.js';
import {decryptQishuiAudio} from './vendor/meting/providers/qishui/audio.js';
import {normalizeTracks,normalizeAudio} from './normalization.mjs';
// Provider modules sometimes print raw responses. Never forward these to logs/UI.
console.log=console.warn=console.error=()=>{};
const originalFetch=globalThis.fetch;
globalThis.fetch=(url,options={})=>originalFetch(url,{...options,signal:AbortSignal.any([AbortSignal.timeout(20000),...(options.signal?[options.signal]:[])])});
let soda; qishui.register({register:(_,provider)=>soda=provider});
const providers={netease:{search:searchNetease,resolve:resolveNetease},tencent:{search:searchTencent,resolve:resolveTencent},
 qishui:{search:(query,cookie)=>soda.handle('search',query,cookie),resolve:(id,cookie,options)=>soda.handle('url',id,cookie,options)}};
export async function execute(request){
 const p=providers[request.sourceId]; if(!p) throw new Error('SOURCE');
 const cookie=String(request.cookie||''); if(cookie.length>32000||/[\r\n]/.test(cookie))throw new Error('CREDENTIAL');
 if(request.action==='checkSigner' && request.sourceId==='qishui') return checkQishuiSigner(request.signerEndpoint);
 if(request.action==='search'){
  const query=String(request.query||'').trim();if(!query||query.length>200)throw new Error('QUERY');
  return normalizeTracks(request.sourceId,await p.search(query,cookie));
 }
 if(request.action==='resolve'){
  if(!/^[\w-]{1,120}$/.test(request.id))throw new Error('ID');
  if(request.sourceId==='qishui'&&(!cookie||!request.signerEndpoint))throw new Error('QISHUI_SETUP');
  return normalizeAudio(await p.resolve(request.id,cookie,{quality:'standard',signerUrl:request.signerEndpoint}));
 }
 if(request.action==='decode'&&request.sourceId==='qishui'){
  const data=await readFile(request.inputPath);if(data.length>256*1024*1024)throw new Error('SIZE');
  await writeFile(request.outputPath,decryptQishuiAudio(data,request.decodeKey),{flag:'wx'});return true;
 }
 throw new Error('ACTION');
}
const input=readline.createInterface({input:process.stdin,crlfDelay:Infinity});
for await(const line of input){
 try {if(line.length>80000)throw new Error('INPUT');const result=await execute(JSON.parse(line));process.stdout.write(JSON.stringify({ok:true,result})+'\n');}
 catch(e){const known=['SOURCE','CREDENTIAL','QUERY','ID','QISHUI_SETUP','NO_AUDIO','ACTION','SIZE','SIGNER_ENDPOINT','SIGNER_AUTH','SIGNER_REDIRECT','SIGNER_HTTP','SIGNER_PROTOCOL','SIGNER_TIMEOUT','SIGNER_NETWORK'];process.stdout.write(JSON.stringify({ok:false,code:known.includes(e.message)?e.message:'UPSTREAM'})+'\n');}
 break;
}
input.close();

export function normalizeTracks(sourceId,raw){
 if(!Array.isArray(raw))throw new Error('UPSTREAM');
 return raw.slice(0,50).map(row=>{
  const id=String(row.id??row.songmid??row.url??'');if(!/^[\w-]{1,120}$/.test(id))return null;
  const seconds=Number(row.duration)/(sourceId==='qishui'?1:1000);
  return {sourceId,id,title:String(row.title??row.name??'').slice(0,500),artist:String(row.author??row.artist??'').slice(0,500),album:String(row.album??'').slice(0,500),
   durationSeconds:Number.isFinite(seconds)&&seconds>0&&seconds<=7200?seconds:null,coverUrl:row.pic?String(row.pic):null};
 }).filter(row=>row?.title);
}
export function normalizeAudio(raw){
 if(!raw?.url)throw new Error('NO_AUDIO');
 const url=new URL(raw.url);if(!['https:','http:'].includes(url.protocol)||url.username||url.password)throw new Error('NO_AUDIO');
 return {url:url.href,quality:String(raw.quality?.actual??raw.quality??'standard'),previewOnly:Boolean(raw.previewOnly||raw.freeTrialInfo),decodeKey:raw.auth||null};
}

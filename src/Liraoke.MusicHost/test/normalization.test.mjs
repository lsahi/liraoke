import test from 'node:test';import assert from 'node:assert/strict';
import {normalizeTracks,normalizeAudio} from '../normalization.mjs';
test('Qishui uses name/artist and seconds; keeps large string IDs',()=>{
 const [t]=normalizeTracks('qishui',[{id:'7146240707408168993',name:'歌 (Live)',artist:'甲 / 乙',duration:202}]);
 assert.equal(t.title,'歌 (Live)');assert.equal(t.artist,'甲 / 乙');assert.equal(t.durationSeconds,202);assert.equal(t.id,'7146240707408168993');
});
test('Netease converts milliseconds without removing version labels',()=>{
 assert.equal(normalizeTracks('netease',[{url:123,title:'帰ってくる (Remix)',author:'歌手',duration:127733}])[0].durationSeconds,127.733);
});
test('Invalid IDs and unknown durations are not importable metadata',()=>{
 assert.deepEqual(normalizeTracks('tencent',[{url:'https://example.com',title:'x'}]),[]);
 assert.equal(normalizeTracks('netease',[{url:1,title:'x'}])[0].durationSeconds,null);
});
test('Missing and unsafe audio URLs fail; trial status survives',()=>{
 assert.throws(()=>normalizeAudio(null));assert.throws(()=>normalizeAudio({url:'file:///a'}));
 assert.throws(()=>normalizeAudio({url:'https://user:pass@example.com/a'}));
 assert.equal(normalizeAudio({url:'https://cdn.example.com/a',previewOnly:true}).previewOnly,true);
});

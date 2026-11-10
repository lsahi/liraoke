import {build} from 'esbuild';
import {mkdir,writeFile,readFile,readdir} from 'node:fs/promises';
import path from 'node:path';
const out=path.resolve('../../tools/ai-runtime');await mkdir(out,{recursive:true});
await build({entryPoints:['host.mjs'],outfile:path.join(out,'host.mjs'),bundle:true,platform:'node',format:'esm',target:'node22',banner:{js:"import { createRequire as __cr } from 'node:module'; const require = __cr(import.meta.url);"},legalComments:'eof'});
let notices='Liraoke AI runtime third-party licenses (build dependencies included)\n';
async function scan(dir){for(const d of await readdir(dir,{withFileTypes:true})){
 if(!d.isDirectory()||d.name.startsWith('.'))continue;const p=path.join(dir,d.name);
 if(d.name.startsWith('@')){await scan(p);continue;}
 try{const pkg=JSON.parse(await readFile(path.join(p,'package.json'),'utf8'));notices+=`\n--- ${pkg.name} ${pkg.version} (${pkg.license??'see license'}) ---\n`;
 for(const f of await readdir(p))if(/^licen[cs]e(\.|$)/i.test(f))notices+=await readFile(path.join(p,f),'utf8');}catch{}
}}
await scan('node_modules');await writeFile(path.join(out,'DEPENDENCY-LICENSES.txt'),notices);

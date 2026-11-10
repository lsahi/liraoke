import {build} from 'esbuild';
import {mkdir,writeFile,readFile,cp} from 'node:fs/promises';
import path from 'node:path';
const out=path.resolve('../../tools/music-runtime');await mkdir(out,{recursive:true});
await build({entryPoints:['host.mjs'],outfile:path.join(out,'host.mjs'),bundle:true,platform:'node',format:'esm',target:'node22',legalComments:'eof'});
await cp('vendor/meting/PROVENANCE.txt',path.join(out,'PROVENANCE.txt'));
await writeFile(path.join(out,'DEPENDENCY-LICENSES.txt'),'nanoid 4.0.2\n'+await readFile('node_modules/nanoid/LICENSE','utf8'));

import { build } from 'esbuild';
import { readFile, writeFile } from 'node:fs/promises';
import path from 'node:path';
const output = path.resolve('../../src/Desktop/Scene');
await build({entryPoints:[path.join(output,'scene.js')],bundle:true,format:'esm',outfile:path.join(output,'bundle.js'),nodePaths:[path.resolve('node_modules')],minify:true,legalComments:'eof'});
const notices=[];
for(const [name,file] of [['three','LICENSE'],['lucide','LICENSE']]) notices.push(name+'\n'+await readFile(path.join('node_modules',name,file),'utf8'));
await writeFile(path.join(output,'THIRD-PARTY.txt'),notices.join('\n\n'),'utf8');
console.log('Offline scene bundle and third-party notices generated.');

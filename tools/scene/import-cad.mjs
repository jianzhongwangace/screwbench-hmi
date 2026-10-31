import occtFactory from 'occt-import-js';
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import path from 'node:path';
import { createHash } from 'node:crypto';
const input=path.resolve(process.argv[2]);
const output=path.resolve('../../src/Desktop/Scene/models',process.argv[3] || 'assembly');
await mkdir(output,{recursive:true});
const bytes=await readFile(input);
const occt=await occtFactory();
const result=occt.ReadStepFile(bytes,{linearUnit:'millimeter',linearDeflectionType:'absolute_value',linearDeflection:.15,angularDeflection:.35});
if(!result.success)throw new Error('STEP import failed: '+input);
const chunks=[];let offset=0;
function store(array,Type){const typed=new Type(array),buffer=Buffer.from(typed.buffer);const entry={offset,count:typed.length};chunks.push(buffer);offset+=buffer.length;return entry;}
const meshes=result.meshes.map((mesh,id)=>{
  const p=mesh.attributes.position.array;const min=[Infinity,Infinity,Infinity],max=[-Infinity,-Infinity,-Infinity];
  for(let i=0;i<p.length;i++) {const a=i%3;min[a]=Math.min(min[a],p[i]);max[a]=Math.max(max[a],p[i]);}
  return {id,name:mesh.name,color:mesh.color,faces:mesh.brep_faces,position:store(p,Float32Array),normal:mesh.attributes.normal?store(mesh.attributes.normal.array,Float32Array):null,index:store(mesh.index.array,Uint32Array),bounds:{min,max}};
});
const metadata={source:path.basename(input),sha256:createHash('sha256').update(bytes).digest('hex'),unit:'mm',linearDeflectionMm:.15,root:result.root,meshes};
await writeFile(path.join(output,'assembly.json'),JSON.stringify(metadata));
await writeFile(path.join(output,'assembly.bin'),Buffer.concat(chunks));
await writeFile(path.join(output,'inventory.json'),JSON.stringify({source:metadata.source,root:result.root,meshes:meshes.map(({id,name,color,bounds})=>({id,name,color,bounds}))},null,2));
console.log(JSON.stringify({source:metadata.source,meshes:meshes.length,megabytes:offset/1024/1024,parts:meshes.map(({id,name,bounds})=>({id,name,bounds}))},null,2));

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { createIcons, Box, PanelTop, PanelsTopLeft, Scan, ZoomIn, ZoomOut, Settings2, X } from 'lucide';
import { defaults, validCalibration, mapSample, PositionFeed } from './mapping.mjs';
import { buildMechanism } from './mechanism.js';

createIcons({ icons: { Box, PanelTop, PanelsTopLeft, Scan, ZoomIn, ZoomOut, Settings2, X } });
const $ = id => document.getElementById(id);
const feed = new PositionFeed();
let calibration = {...defaults}, state='Disconnected',source='NONE',session=null,pose=null;
let selected='carriage', view='iso', loaded=false;
const callouts={};document.querySelectorAll('.sensor-callout').forEach(el=>{callouts[el.dataset.sensor]=el;el.addEventListener('click',()=>{document.querySelectorAll('.sensor-callout').forEach(x=>x.classList.remove('active'));el.classList.add('active');});});
try {const c=JSON.parse(localStorage.getItem('displayCalibration'));if(validCalibration(c))calibration=c;}catch{}
const parts=[
 ['carriage','螺母 / 滑台 / 读数头','#008d7b','位移通道 x 驱动 · 螺母防转'],
 ['screw','被测丝杠','#a48843','转角通道 θ 驱动 · 轴向固定'],
 ['spindle','电机轴 / 联轴器','#587eae','与丝杠同轴 1:1 联动假设'],
 ['actuator','加载杆 / 浮动关节','#8a99a0','随滑台几何联动 · 无独立位置反馈'],
 ['fixed','床身 / 支撑 / 外壳','#596970','固定部件']
];
const scene=new THREE.Scene();scene.background=new THREE.Color(0xedf2f2);
const camera=new THREE.PerspectiveCamera(36,1,1,15000);
let renderer;
try {renderer=new THREE.WebGLRenderer({antialias:true,preserveDrawingBuffer:true});}
catch(error){$('scene-error').hidden=false;$('scene-error').textContent='三维渲染初始化失败：'+error.message;throw error;}
renderer.setPixelRatio(Math.min(devicePixelRatio,2));renderer.outputColorSpace=THREE.SRGBColorSpace;
$('viewport').append(renderer.domElement);
renderer.domElement.addEventListener('webglcontextlost',e=>{e.preventDefault();$('scene-error').hidden=false;$('scene-error').textContent='三维渲染已中断，请重新打开软件。';});
const controls=new OrbitControls(camera,renderer.domElement);controls.enableDamping=true;
controls.minDistance=150;controls.maxDistance=7000;
scene.add(new THREE.HemisphereLight(0xffffff,0x778893,2.4));
const light=new THREE.DirectionalLight(0xffffff,3);light.position.set(400,1000,800);scene.add(light);
const backLight=new THREE.DirectionalLight(0xffffff,1.5);backLight.position.set(-600,400,-400);scene.add(backLight);
const rig=new THREE.Group();scene.add(rig);
const mechanism=buildMechanism(rig);
const {groups,meshes}=mechanism;
const carriage=groups.carriage,shaft=groups.screw;
let bounds=new THREE.Box3(),center=new THREE.Vector3(),size=new THREE.Vector3(850,125,170);
const outline=new THREE.BoxHelper(carriage,0x008d7b);outline.visible=false;scene.add(outline);
async function initializeMechanism(){
  scene.updateMatrixWorld(true);bounds.setFromObject(rig);bounds.getCenter(center);bounds.getSize(size);
  const floor=new THREE.Mesh(new THREE.PlaneGeometry(5000,5000),new THREE.MeshStandardMaterial({color:0xe5ebec,roughness:1}));floor.rotation.x=-Math.PI/2;floor.position.y=bounds.min.y-2;scene.add(floor);
  loaded=true;$('scene-error').hidden=true;$('cad-source').textContent='二维参考重建 · 非制造模型';
  select('carriage');fit();applyPose();window.chrome?.webview?.postMessage({type:'ready'});
}
function applyPose(){
  if(!loaded)return;const mapped=mapSample(feed.sample,calibration);if(!mapped)return;
  $('range-warning').hidden=!mapped.outOfRange;$('display-position').innerHTML=`${mapped.displacement.toFixed(4)} <small>mm</small>`;
  if(mapped.outOfRange)return;
  $('mapping-note').textContent=(mapped.rotation == null ? '转角未提供，旋转部件保持原姿态。' : '')+'加载杆为几何联动，不代表电缸实测位置';
  pose=mapped;mechanism.update(mapped.displacement,mapped.rotation ?? shaft.rotation.x);scene.updateMatrixWorld(true);outline.update();
}
function updateCallouts(){
  if(!loaded)return;
  rig.updateMatrixWorld(true);camera.updateMatrixWorld();
  const r=renderer.domElement.getBoundingClientRect(),svg=$('sensor-leaders');
  svg.setAttribute('viewBox',`0 0 ${r.width} ${r.height}`);
  const anchors={torque:new THREE.Vector3(-205,94,0),rpm:new THREE.Vector3(-337,88,0),force:new THREE.Vector3(40,94,-39),position:new THREE.Vector3(0,22,60),angle:new THREE.Vector3(120,64,8)};
  const projected=Object.entries(anchors).map(([key,p])=>{if(key==='force'||key==='position')p.x+=carriage.position.x;p.applyMatrix4(rig.matrixWorld).project(camera);return {key,x:(p.x+1)*.5*r.width,y:(1-p.y)*.5*r.height,visible:p.z>=-1&&p.z<=1&&Math.abs(p.x)<=1&&Math.abs(p.y)<=1};});
  const rows=[projected.filter(p=>['rpm','torque','force'].includes(p.key)),projected.filter(p=>['position','angle'].includes(p.key))];
  rows.forEach((items,row)=>{
    items.sort((a,b)=>a.x-b.x);
    const width=Math.min(156,Math.floor((r.width-40)/3)-8),gap=r.width/(items.length+1);
    items.forEach((p,i)=>{
      const el=callouts[p.key],x=items.length===3?(i+.5)*r.width/3:gap*(i+1),y=row===0?Math.max(100,r.height*.22):r.height*.83;
      el.style.width=`${width}px`;el.style.left=`${x}px`;el.style.top=`${y}px`;
      let line=svg.querySelector(`[data-line="${p.key}"]`),dot=svg.querySelector(`[data-dot="${p.key}"]`);
      if(!line){line=document.createElementNS('http://www.w3.org/2000/svg','polyline');line.dataset.line=p.key;svg.append(line);dot=document.createElementNS('http://www.w3.org/2000/svg','circle');dot.dataset.dot=p.key;dot.setAttribute('r','3.5');svg.append(dot);}
      const edge=y+(row===0?1:-1)*el.offsetHeight/2,bend=edge+(row===0?24:-24);
      line.setAttribute('points',`${x},${edge} ${x},${bend} ${p.x},${p.y}`);dot.setAttribute('cx',p.x);dot.setAttribute('cy',p.y);
      line.style.display=dot.style.display=p.visible?'':'none';
    });
  });
}
function select(part,mesh=null){
  selected=part;const entry=parts.find(p=>p[0]===part);$('part-name').textContent=mesh?mesh.userData.name:entry[1];$('part-kind').textContent=entry[3];
  document.querySelectorAll('.part').forEach(b=>b.setAttribute('aria-pressed',String(b.dataset.part===part)));
  outline.setFromObject(mesh||groups[part]);outline.visible=loaded&&part!=='fixed';
}
for(const [id,name,color]of parts){const b=document.createElement('button');b.className='part';b.dataset.part=id;b.setAttribute('aria-pressed',String(id===selected));const swatch=document.createElement('span');swatch.className='swatch';swatch.style.background=color;b.append(swatch,document.createTextNode(name));b.onclick=()=>select(id);$('parts').append(b);}
const raycaster=new THREE.Raycaster(),pointer=new THREE.Vector2();let press=null;
renderer.domElement.addEventListener('pointerdown',e=>press=[e.clientX,e.clientY]);
renderer.domElement.addEventListener('pointerup',e=>{if(!press||Math.hypot(e.clientX-press[0],e.clientY-press[1])>5)return;const r=renderer.domElement.getBoundingClientRect();pointer.set((e.clientX-r.left)/r.width*2-1,-(e.clientY-r.top)/r.height*2+1);raycaster.setFromCamera(pointer,camera);const hit=raycaster.intersectObjects(meshes).find(h=>h.object.parent.visible);if(hit)select(hit.object.userData.part,hit.object);});
function fit(next=view){
  view=next;controls.target.copy(center);camera.up.set(0,1,0);
  const distance=Math.max(size.y,size.x/Math.max(camera.aspect,.25))*1.18/(2*Math.tan(THREE.MathUtils.degToRad(camera.fov/2)))+size.z;
  const directions={iso:new THREE.Vector3(.35,.65,1.8),front:new THREE.Vector3(0,.04,1),top:new THREE.Vector3(0,1,.001)};
  camera.position.copy(center).add(directions[view].normalize().multiplyScalar(distance));controls.update();
  document.querySelectorAll('[data-view]').forEach(b=>b.classList.toggle('active',b.dataset.view===view));
}
document.querySelectorAll('[data-view]').forEach(b=>b.onclick=()=>fit(b.dataset.view));$('fit').onclick=()=>fit();
function zoom(factor){camera.position.sub(controls.target).multiplyScalar(factor).add(controls.target);controls.update();}
$('zoom-in').onclick=()=>zoom(.8);$('zoom-out').onclick=()=>zoom(1.25);
new ResizeObserver(()=>{const r=$('viewport').getBoundingClientRect();if(!r.width||!r.height)return;renderer.setSize(r.width,r.height);camera.aspect=r.width/r.height;camera.updateProjectionMatrix();fit();}).observe($('viewport'));
function fields(){for(const k of ['positionZero','angleZero','positionSign','angleSign'])$(k.replace(/[A-Z]/g,m=>'-'+m.toLowerCase())).value=calibration[k];}
$('settings-button').onclick=()=>{fields();$('settings').showModal();};$('close-settings').onclick=()=>$('settings').close();
$('reset-calibration').onclick=()=>{calibration={...defaults};fields();applyPose();try{localStorage.removeItem('displayCalibration');}catch{}};
$('calibration').onsubmit=e=>{e.preventDefault();const c={positionZero:+$('position-zero').value,angleZero:+$('angle-zero').value,positionSign:+$('position-sign').value,angleSign:+$('angle-sign').value};if(!validCalibration(c))return;calibration=c;try{localStorage.setItem('displayCalibration',JSON.stringify(c));}catch{}applyPose();$('settings').close();};
function clearPose(){feed.reset();pose=null;mechanism.update(0,0);for(const id of ['position','display-position','angle','rpm','force','torque'])$(id).textContent='--';for(const el of Object.values(callouts))el.querySelector('span').textContent='--';$('sample-id').textContent='样本 --';$('range-warning').hidden=true;$('mapping-note').textContent='加载杆为几何联动，不代表电缸实测位置';if(loaded)outline.update();}
function receive(message){
  if(!message||message.type!=='frame'||!['SIMULATED','MEASURED','IMPORTED','DEMO','NONE'].includes(message.source))return;
  if(typeof message.session !== 'string' || !message.session.trim() || !['Disconnected','Ready','Running','Paused','Completed','Stopped','Fault'].includes(message.state))return;
  if(message.session!==session||message.source!==source){session=message.session;clearPose();}
  state=message.state;source=message.source;$('source').textContent=source==='DEMO'?'JSON演示 · 非实测':source==='SIMULATED'?'模拟数据':source==='MEASURED'?'外部实测输入':source==='IMPORTED'?'导入快照 · 非实时':'无数据源';$('source').classList.toggle('real',source==='MEASURED');
  if(message.sample&&feed.accept(message.sample,performance.now())){
    const s=feed.sample;for(const[id,key,unit,digits]of[['position','position','mm',4],['angle','angle','rad',3],['rpm','rpm','rpm',1],['force','force','N',2],['torque','torque','N·m',4]])$(id).innerHTML=Number.isFinite(s[key])?`${s[key].toFixed(digits)} <small>${unit}</small>`:'--';
    $('sample-id').textContent=`样本 ${s.sequence} · ${s.time.toFixed(2)} s`;applyPose();
    for(const [key,unit,digits] of [['torque','N·m',4],['rpm','rpm',1],['force','N',2],['position','mm',4],['angle','rad',3]])
      callouts[key].querySelector('span').textContent=Number.isFinite(s[key])?`${s[key].toFixed(digits)} ${unit}`:`-- ${unit}`;
  }
  $('status').textContent=feed.status(state,performance.now());
}
window.chrome?.webview?.addEventListener('message',e=>receive(e.data));
window.screwBench={receive,inspect:()=>({loaded,pose,carriageX:carriage.position.x,shaftAngle:shaft.rotation.x,selected,state,source,calibration,sequence:feed.sequence,camera:camera.position.toArray(),status:feed.status(state,performance.now()),calls:renderer.info.render.calls,meshCount:meshes.length,...mechanism.inspect()}),projectPart:id=>{const m=meshes.find(m=>m.userData.id===id);if(!m)return null;const point=new THREE.Box3().setFromObject(m).getCenter(new THREE.Vector3()).project(camera);return{x:(point.x+1)/2*renderer.domElement.clientWidth,y:(1-point.y)/2*renderer.domElement.clientHeight};}};
clearPose();fit();
function render(){requestAnimationFrame(render);controls.update();updateCallouts();$('status').textContent=feed.status(state,performance.now());renderer.render(scene,camera);}render();
initializeMechanism().catch(error=>{$('scene-error').hidden=false;$('scene-error').textContent='三维模型加载失败：'+error.message;console.error(error);});

import * as THREE from 'three';

// Dimensions are illustrative millimetres, inferred from the supplied reference image, not a manufacturing drawing.
export function buildMechanism(rig) {
  const groups = Object.fromEntries(['carriage','screw','spindle','actuator','fixed'].map(k=>[k,new THREE.Group()]));
  Object.values(groups).forEach(g=>rig.add(g));
  groups.screw.position.y=64;groups.spindle.position.y=64;
  const meshes=[];
  const mats={};
  for(const [key,color] of Object.entries({dark:0x303c40,base:0x47565a,steel:0xbac6cb,blue:0x326b98,gold:0xb5995b,teal:0x008677,red:0xb14b45,groove:0x687c84}))
    mats[key]=new THREE.MeshStandardMaterial({color,metalness:key==='steel'?.72:.4,roughness:key==='steel'?.32:.48});
  function mesh(geometry,at,material,part,name){const m=new THREE.Mesh(geometry,mats[material]);m.position.set(...at);m.userData={part,name,id:meshes.length};groups[part].add(m);meshes.push(m);return m;}
  function box(dims,at,mat='steel',part='fixed',name='固定结构'){return mesh(new THREE.BoxGeometry(...dims),at,mat,part,name);}
  function cylinder(r,len,at,mat='steel',part='fixed',name='轴件'){const m=mesh(new THREE.CylinderGeometry(r,r,len,48),at,mat,part,name);m.rotation.z=Math.PI/2;return m;}
  function foot(x,z){box([34,8,26],[x,-8,z],'dark');}
  box([850,20,156],[0,0,0],'base','fixed','床身');
  for(const x of [-360,0,360])for(const z of [-53,53])foot(x,z);
  for(const z of [-30,30]){box([380,7,12],[48,15,z],'steel','fixed','直线导轨');for(let x=-125;x<=220;x+=35)box([3,1,4],[x,19,z],'dark','fixed','导轨紧固件');}
  // Motor, torque transducer and bearing housings are fixed. Only shafts/couplings rotate.
  box([110,70,75],[-337,52,0],'dark','fixed','驱动伺服电机外壳');
  cylinder(37,9,[-277,64,0],'gold','fixed','电机法兰');
  box([124,12,92],[-332,16,0],'dark');
  for(const y of [29,39,49,59,69,79])box([89,2,3],[-340,y,39],'steel','fixed','电机散热筋');
  box([25,19,29],[-352,97,0],'dark','fixed','电机接线盒');
  cylinder(8,167,[-216,0,0],'steel','spindle','同轴传动轴');
  for(const x of [-248,-155]){cylinder(18,25,[x,0,0],'steel','spindle','联轴器');cylinder(18.3,5,[x,0,0],'red','spindle','联轴器弹性体');box([9,3,5],[x,17,0],'dark','spindle','联轴器旋转标记');}
  cylinder(30,42,[-205,64,0],'blue','fixed','扭矩与转速传感器外壳');
  box([52,17,56],[-205,21,0],'dark');box([22,20,27],[-205,41,0],'blue');
  box([10,14,12],[-205,99,0],'steel','fixed','传感器接口');
  for(const x of [-112,223]){box([33,57,48],[x,40,0],'dark','fixed','丝杠支撑座');cylinder(17,35,[x,64,0],'steel','fixed','支撑轴承');box([49,8,65],[x,15,0],'dark');}
  cylinder(8,395,[57,0,0],'steel','screw','被测丝杠');
  class Helix extends THREE.Curve{getPoint(t,target=new THREE.Vector3()){const a=t*Math.PI*2*68;return target.set(-129+t*372,8.2*Math.cos(a),8.2*Math.sin(a));}}
  const thread=mesh(new THREE.TubeGeometry(new Helix(),2200,.65,5,false),[0,0,0],'groove','screw','丝杠螺旋滚道');
  thread.castShadow=true;box([9,2,4],[239,8,0],'red','screw','丝杠转角标记');
  // All carriage-local geometry shares the measured displacement, including the grating read head.
  box([88,12,104],[0,26,0],'steel','carriage','测量滑台');
  for(const z of [-30,30])box([35,10,22],[0,19,z],'teal','carriage','直线导轨滑块');
  box([66,16,48],[-4,40,0],'dark','carriage','螺母防转座');
  cylinder(23,61,[-7,64,0],'gold','carriage','被测螺母');
  for(const x of [-36,22]){cylinder(27,6,[x,64,0],'gold','carriage','螺母法兰');for(const a of [0,Math.PI/2,Math.PI,Math.PI*1.5])cylinder(2,7,[x,64+23*Math.cos(a),23*Math.sin(a)],'steel','carriage','法兰紧固件');}
  box([42,11,71],[32,39,-25],'steel','carriage','加载连接座');
  cylinder(16,22,[40,80,-39],'blue','carriage','拉压力传感器');
  box([26,19,25],[40,56,-39],'steel','carriage','力传感器安装座');
  box([8,9,10],[40,100,-39],'steel','carriage','力传感器接口');
  const link=cylinder(4,1,[160,80,-39],'steel','actuator','加载连接杆（几何联动）');
  const joint=cylinder(10,10,[160,80,-39],'dark','actuator','浮动关节（几何联动）');
  cylinder(30,91,[348,80,-39],'steel','fixed','加载电缸外壳');
  for(const x of [298,398])box([13,67,70],[x,67,-39],'dark','fixed','电缸端盖');
  box([127,14,98],[348,20,-27],'dark','fixed','加载电缸底座');
  for(const x of [310,387])box([14,18,45],[x,36,-39],'dark');
  box([371,9,9],[50,12,61],'steel','fixed','光栅尺身');
  box([26,13,16],[0,17,60],'blue','carriage','光栅读数头');
  for(let x=-100;x<=220;x+=10)box([.6,1,5],[x,17,65],'dark','fixed','尺身刻度');
  for(const x of [-123,236]){box([12,19,19],[x,18,61],'red','fixed','行程限位开关');box([8,16,20],[x+15,18,61],'steel','fixed','机械限位块');}
  function update(displacement,rotation){groups.carriage.position.x=displacement;groups.screw.rotation.x=rotation;groups.spindle.rotation.x=rotation;const start=52+displacement,end=294;link.scale.y=end-start;link.position.x=(start+end)/2;joint.position.x=start+35;}
  update(0,0);
  const readHead = meshes.find(m => m.userData.name === '光栅读数头');
  const ruler = meshes.find(m => m.userData.name === '光栅尺身');
  const nut = meshes.find(m => m.userData.name === '被测螺母');
  const motorShell = meshes.find(m => m.userData.name === '驱动伺服电机外壳');
  const world = mesh => mesh.getWorldPosition(new THREE.Vector3()).toArray();
  return {groups,meshes,update,inspect:()=>({linkStart:52+groups.carriage.position.x,linkEnd:294,linkLength:link.scale.y,
    readHeadPosition:world(readHead), rulerPosition:world(ruler), nutPosition:world(nut), motorShellPosition:world(motorShell)})};
}

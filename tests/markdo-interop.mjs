// Independent Node/OpenSSL client implementing the original MarkDo Dart wire format.
// Secrets stay in memory; stdout reports assertions only. The host uses an isolated temporary database.
import assert from 'node:assert/strict';
import {createCipheriv,createDecipheriv,createHash,randomBytes,generateKeyPairSync,createPublicKey,diffieHellman} from 'node:crypto';
import {spawn,execFileSync} from 'node:child_process';
import {mkdtemp,readFile,rm} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join,resolve,dirname,basename} from 'node:path';
import {fileURLToPath} from 'node:url';
import {createInterface} from 'node:readline';
import http from 'node:http';
import net from 'node:net';

const repo = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const dotnet = process.env.APPKEEPER_DOTNET || 'dotnet';
const root = await mkdtemp(join(tmpdir(),'AppKeeper-MarkDo-interop-'));
execFileSync(dotnet,['build','tests/AppKeeper.SyncInterop/AppKeeper.SyncInterop.csproj','-c','Release','--nologo'],{cwd:repo,stdio:'pipe',timeout:180000});
const host = spawn(dotnet,[join(repo,'tests/AppKeeper.SyncInterop/bin/Release/net10.0-windows/AppKeeper.SyncInterop.dll'),root],{stdio:['pipe','pipe','pipe']});
let stderr = '';
host.stderr.on('data',data=>stderr+=data);
const lines = createInterface({input:host.stdout});
const inbox = [], waiters = [];
lines.on('line',line=>{try { const message=JSON.parse(line); if(waiters.length)waiters.shift()(message);else inbox.push(message); }catch{}});
const receive = () => inbox.length ? Promise.resolve(inbox.shift()) : new Promise((yes,no)=>{
  const timeout=setTimeout(()=>no(new Error('Interop host timed out: '+stderr.slice(-300))),15000);
  waiters.push(value=>{clearTimeout(timeout);yes(value);});
});
async function command(value){host.stdin.write(JSON.stringify(value)+'\n');return receive();}
let server = await receive(), port = server.port;
assert.equal(server.ready,true);
const pairingContext = 'MarkDo pairing v1', syncContext = 'MarkDo sync v1';
const nonce = ()=>randomBytes(32).toString('base64url');
const pairKey = (offer,code)=>createHash('sha256').update(`${pairingContext}|${offer.sessionId}|${offer.secret}|${code}`,'utf8').digest();
function encrypt(value,key,context=syncContext){
  const iv=randomBytes(12), cipher=createCipheriv('aes-256-gcm',key,iv);
  cipher.setAAD(Buffer.from(context));
  const body=Buffer.concat([cipher.update(JSON.stringify(value),'utf8'),cipher.final()]);
  return Buffer.concat([iv,body,cipher.getAuthTag()]).toString('base64');
}
function decrypt(value,key,context=syncContext){
  const bytes=Buffer.from(value,'base64'), cipher=createDecipheriv('aes-256-gcm',key,bytes.subarray(0,12));
  cipher.setAAD(Buffer.from(context));cipher.setAuthTag(bytes.subarray(-16));
  return JSON.parse(Buffer.concat([cipher.update(bytes.subarray(12,-16)),cipher.final()]).toString('utf8'));
}
function request(path,body,headers={}){
  return new Promise((yes,no)=>{
    const request=http.request({hostname:'127.0.0.1',port,path,method:body===undefined?'GET':'POST',headers},response=>{
      let text='';response.setEncoding('utf8');response.on('data',part=>text+=part);
      response.on('end',()=>yes({status:response.statusCode,text,headers:response.headers}));
    });
    request.setTimeout(12000,()=>request.destroy(new Error('HTTP test timeout')));request.on('error',no);
    // Deliberately use HTTP chunked transfer, as Dart's request.write does.
    if(body!==undefined){request.write(body.slice(0,13));request.write(body.slice(13));}request.end();
  });
}
const phone = id=>({id, name:'测试手机 '+id, key:null, operations:new Map()});
async function pair(device,offer,code){
  const key=pairKey(offer,code), requestId=nonce();
  const response=await request('/v1/pair',encrypt({deviceId:device.id,deviceName:device.name,serverId:server.serverId,requestId},key,pairingContext),{'X-MarkDo-Session':offer.sessionId});
  if(response.status!==200)return response.status;
  assert.equal(response.headers['x-markdo-server'],server.serverId);
  const message=decrypt(response.text,key,pairingContext);
  assert.equal(message.requestId,requestId);assert.equal(message.serverId,server.serverId);
  device.key=Buffer.from(message.key,'base64url');device.token=message.key;
  assert.ok(message.devices.every(d=>!('key' in d)&&!('protectedKey' in d)));
  return response.status;
}
async function manualOffer(){
  const keys=generateKeyPairSync('x25519');
  const publicKey=keys.publicKey.export({format:'der',type:'spki'}).subarray(-32).toString('base64');
  const response=await request('/v1/manual-offer',JSON.stringify({publicKey}));
  if(response.status!==200)return response;
  const offer=JSON.parse(response.text);assert.ok(!('secret' in offer));
  const remote=createPublicKey({key:Buffer.concat([Buffer.from('302a300506032b656e032100','hex'),Buffer.from(offer.publicKey,'base64')]),format:'der',type:'spki'});
  offer.secret=diffieHellman({privateKey:keys.privateKey,publicKey:remote}).toString('base64').replaceAll('+','-').replaceAll('/','_');
  return offer;
}
async function sync(device,extra={}){
  const requestId=nonce(), body=encrypt({version:4,deviceId:device.id,requestId,operations:[...device.operations.values()],...extra},device.key);
  const response=await request('/v1/sync',body,{'X-MarkDo-Device':device.id});
  if(response.status===200){
    const message=decrypt(response.text,device.key);assert.equal(message.requestId,requestId);assert.equal(message.serverId,server.serverId);
    for(const operation of message.operations)device.operations.set(operation.id,operation);
    assert.ok(message.devices.every(d=>!('key' in d)));return {response,body,message};
  }
  return {response,body};
}
function operation(id,deviceId,clock,kind,taskId,payload){return {id,deviceId,clock,kind,taskId,payload,createdAt:new Date().toISOString()};}
async function availablePort(){const socket=net.createServer();await new Promise(yes=>socket.listen(0,'127.0.0.1',yes));const number=socket.address().port;await new Promise(yes=>socket.close(yes));return number;}

try{
  let info=JSON.parse((await request('/v1/pairing')).text);assert.equal(info.ready,false);
  let session=await command({op:'session'});
  info=JSON.parse((await request('/v1/pairing')).text);assert.equal(info.app,'markdo');assert.equal(info.ready,true);
  assert.ok(!JSON.stringify(info).includes(session.code));assert.ok(!JSON.stringify(info).includes(session.offer.secret));
  assert.equal(session.offer.version,1);assert.equal(session.offer.secret.length,43);assert.ok(Date.parse(session.offer.expires)>Date.now());
  const one=phone('phone-one'),two=phone('phone-two');
  const wrong=((Number(session.code)+1)%1000000).toString().padStart(6,'0');
  assert.equal(await pair(one,session.offer,wrong),401);
  assert.equal(await pair(one,session.offer,session.code),200);
  assert.equal(await pair(two,session.offer,session.code),410);
  session=await command({op:'session'});
  const offer=await manualOffer();assert.equal(offer.port,port);
  assert.equal(await pair(two,offer,session.code),200);
  assert.notDeepEqual(one.key,two.key);
  await command({op:'append',kind:'create',taskId:'desktop-task',payload:{title:'电脑创建',category:'工作'}});
  one.operations.set('one-create',operation('one-create',one.id,2,'create','one-task',{title:'手机一创建',category:'生活'}));
  two.operations.set('two-create',operation('two-create',two.id,3,'create','two-task',{title:'手机二离线创建',category:'工作'}));
  for(const device of [one,two,one,two])assert.equal((await sync(device)).response.status,200);
  assert.equal(one.operations.size,3);assert.deepEqual([...one.operations.keys()].sort(),[...two.operations.keys()].sort());
  let tasks=await command({op:'tasks'});assert.equal(tasks.length,3);
  one.operations.set('one-edit',operation('one-edit',one.id,10,'edit','desktop-task',{title:'同钟手机一',plannedStart:'2026-11-18T09:30:00Z',startHasTime:true}));
  two.operations.set('two-edit',operation('two-edit',two.id,10,'edit','desktop-task',{title:'同钟手机二'}));
  two.operations.set('done',operation('done',two.id,11,'complete','two-task',{completedAt:new Date().toISOString(),effortSeconds:null,inputValue:null,inputUnit:null,rating:5,note:'中文与 emoji 🐦'}));
  for(const device of [two,one,two,one])assert.equal((await sync(device)).response.status,200);
  tasks=await command({op:'tasks'});assert.equal(tasks.find(t=>t.id==='desktop-task').title,'同钟手机二');
  assert.equal(tasks.find(t=>t.id==='two-task').seconds,null);assert.equal(tasks.find(t=>t.id==='two-task').rating,5);
  const successful=await sync(one);assert.equal(successful.response.status,200);
  assert.equal((await request('/v1/sync',successful.body,{'X-MarkDo-Device':one.id})).status,401);
  assert.equal((await sync(one,{version:3})).response.status,426);
  assert.equal((await sync(one,{deviceId:two.id})).response.status,401);
  const before=await command({op:'operations'});
  const validNew=operation('rolled-back',one.id,20,'create','rolled-back-task',{title:'must not commit'});
  assert.equal((await sync(one,{operations:[validNew,{...validNew,id:'invalid',kind:'wipe'}]})).response.status,400);
  assert.equal((await command({op:'operations'})).length,before.length);
  const collision={...before[0],payload:{title:'inconsistent duplicate'}};
  assert.equal((await sync(one,{operations:[validNew,collision]})).response.status,400);
  assert.equal((await command({op:'operations'})).length,before.length);
  await command({op:'append',kind:'delete',taskId:'one-task',payload:{}});
  await sync(one);await sync(two);assert.ok(!(await command({op:'tasks'})).some(t=>t.id==='one-task'));
  const lastClock=Math.max(...(await command({op:'operations'})).map(o=>o.clock));
  one.operations.set('reopen',operation('reopen',one.id,lastClock+1,'reopen','one-task',{}));
  await sync(one);assert.ok((await command({op:'tasks'})).some(t=>t.id==='one-task'));
  const disk=await readFile(join(root,'AppKeeper.agenda-sync.json'),'utf8');
  assert.ok(!disk.includes(one.token)&&!disk.includes(two.token),'long-term keys must be protected on disk');
  const restarted=await command({op:'restart'});port=restarted.port;assert.equal(restarted.serverId,server.serverId);
  assert.equal((await sync(two)).response.status,200);
  await command({op:'revoke',id:two.id});assert.equal((await sync(two)).response.status,401);
  session=await command({op:'session'});assert.equal(await pair(two,await manualOffer(),session.code),200);
  assert.equal((await sync(two)).response.status,200);
  // QR and manual attempts share one lockout budget; refreshing invalidates all previous offers.
  session=await command({op:'session'});
  const stale=session.offer;
  const incorrect=((Number(session.code)+1)%1000000).toString().padStart(6,'0');
  for(let i=0;i<8;i++)assert.equal(await pair(phone('guess-'+i),i%2?await manualOffer():session.offer,incorrect),401);
  assert.equal(await pair(one,session.offer,session.code),410);
  session=await command({op:'session'});assert.equal(await pair(one,stale,session.code),410);
  assert.equal(await pair(one,session.offer,session.code),200);
  const movedPort=await availablePort();assert.ok(!(await command({op:'configure',enabled:true,port:movedPort})).error);port=movedPort;
  assert.equal((await sync(one)).response.status,200);
  const disabled=await command({op:'configure',enabled:false,port:movedPort});assert.equal(disabled.serving,false);
  assert.equal((await command({op:'configure',enabled:true,port:movedPort})).serving,true);
  assert.equal((await sync(one)).response.status,200);
  const publicState=await command({op:'state'});assert.ok(publicState.lastSync);assert.equal(publicState.phones.length,2);
  console.log('PASS - independent Node/OpenSSL MarkDo v4 client: QR + X25519 manual pairing, shared lockout, two-phone convergence, conflict order, replay rejection, atomic rollback, DPAPI restart, revocation and port lifecycle');
}finally{
  try{await command({op:'stop'});}catch{}
  host.stdin.end();
  await new Promise(yes=>{if(host.exitCode!==null)return yes();host.once('exit',yes);setTimeout(()=>{host.kill();yes();},6000).unref();});
  assert.equal(dirname(resolve(root)),resolve(tmpdir()));
  assert.ok(basename(root).startsWith('AppKeeper-MarkDo-interop-'));
  await rm(root,{recursive:true,force:true});
}

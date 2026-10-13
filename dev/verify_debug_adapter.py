import urllib.request,json,time,pathlib,uuid
ROOT=pathlib.Path(__file__).resolve().parent
OP=urllib.request.build_opener(urllib.request.ProxyHandler({}))
def api(url,data=None,host=False):
 h={'Content-Type':'application/json'}
 if host:h['Authorization']='Bearer local-debug-only'
 r=urllib.request.Request(url,data=json.dumps(data).encode() if data is not None else None,headers=h)
 return json.load(OP.open(r,timeout=30))
base='http://127.0.0.1:15088'
state=api('http://127.0.0.1:17001/api/v1/plugins/AiChatPlugin',host=True)
if not state.get('enabled'):
 api('http://127.0.0.1:17001/api/v1/plugins/AiChatPlugin/enable',{},True)
 time.sleep(8)
else:
 time.sleep(2)
log=ROOT/'host.log'
log_start=log.stat().st_size
report={'before':api(base+'/api/diagnostics'),'rounds':[], 'lastId':max((r['id'] for r in api(base+'/api/records')),default=0)}
run_id=uuid.uuid4().hex[:8]
for i in range(6):
 segments=[{'$type':'ShiroBot.SDK.Models.TextSegment','text':'#ai memory-test'}]
 if i%2==0:segments.append({'$type':'ShiroBot.SDK.Models.ImageSegment','uri':'http://127.0.0.1:15090/image.jpg'})
 payload={'type':'ShiroBot.SDK.Models.MessageEvent','data':{'messageId':'memory-'+run_id+'-'+str(i),'channel':{'id':'memory-user','type':'Direct'},'sender':{'id':'memory-user','name':'Test'},'segments':segments}}
 api(base+'/api/events',payload)
 for _ in range(100):
  records=api(base+'/api/records')
  if any(r['direction']=='outbound' and 'memory-test-ok' in json.dumps(r['data']) for r in records if r['id']>report.get('lastId',0)):break
  time.sleep(.1)
 else:raise Exception('No chat reply: '+str(i))
 report['lastId']=max(r['id'] for r in records)
 report['rounds'].append(api(base+'/api/diagnostics'))
report['disable']=api('http://127.0.0.1:17001/api/v1/plugins/AiChatPlugin/disable',{},True)
report['after']=api(base+'/api/diagnostics')
with log.open('rb') as f:
 f.seek(log_start); unload_log=f.read().decode()
report['assemblyReleased']='插件热卸载成功: AiChatPlugin' in unload_log and '程序集仍有残留引用' not in unload_log
report['unloadLog']=unload_log
(ROOT/'debug-adapter-report.json').write_text(json.dumps(report,indent=2))
print(json.dumps(report,indent=2))
assert report['assemblyReleased'], 'Plugin logic stopped but assembly remained loaded; see debug-adapter-report.json'

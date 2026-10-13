import json,base64,pathlib,hashlib
from http.server import ThreadingHTTPServer,BaseHTTPRequestHandler
ROOT=pathlib.Path(__file__).resolve().parent
IMAGE=b'\xff\xd8\xff\xe0'+bytes(range(256))*(16384)
class Handler(BaseHTTPRequestHandler):
 def log_message(self,*args): pass
 def respond(self,data,content='application/json'):
  self.send_response(200);self.send_header('Content-Type',content);self.send_header('Content-Length',str(len(data)));self.end_headers();self.wfile.write(data)
 def do_GET(self):
  if self.path.startswith('/image'):self.respond(IMAGE,'image/jpeg')
  else:self.respond(b'{"data":[{"id":"memory-test"}]}')
 def do_POST(self):
  if self.headers.get('Transfer-Encoding','').lower()=='chunked':
   parts=[]
   while True:
    size=int(self.rfile.readline().split(b';')[0],16)
    if not size:self.rfile.readline();break
    parts.append(self.rfile.read(size));self.rfile.read(2)
   raw=b''.join(parts)
  else:raw=self.rfile.read(int(self.headers['Content-Length']))
  d=json.loads(raw);images=[]
  for m in d['messages']:
   if isinstance(m.get('content'),list):
    for p in m['content']:
     if p.get('type')=='image_url':
      image=base64.b64decode(p['image_url']['url'].split(',',1)[1]);assert image==IMAGE
      images.append(hashlib.sha256(image).hexdigest())
  with (ROOT/'mock-requests.jsonl').open('a') as f:f.write(json.dumps({'bytes':len(raw),'images':images,'tools':bool(d.get('tools')),'roles':[m['role'] for m in d['messages']]})+'\n')
  if d['messages'][-1]['role']=='user' and d.get('tools'):
   message={'content':'','tool_calls':[{'id':'test-call','type':'function','function':{'name':'timer_tool','arguments':'{"at":"invalid","text":"test"}'}}]}
  else:message={'content':'memory-test-ok'}
  self.respond(json.dumps({'choices':[{'message':message}]}).encode())
ThreadingHTTPServer(('127.0.0.1',15090),Handler).serve_forever()

"""Exercise an isolated deployed daemon. Credentials are read from environment and never printed."""
import argparse, atexit, http.cookiejar, json, os, pathlib, re, shutil, subprocess, tempfile, time, urllib.error, urllib.request, uuid

parser = argparse.ArgumentParser()
parser.add_argument('--origin', default=os.environ.get('SECONDBRAIN_QUALIFICATION_ORIGIN'))
parser.add_argument('--compose-project')
parser.add_argument('--incoming', default=os.environ.get('SECONDBRAIN_QUALIFICATION_INCOMING', '/srv/secondbrain-incoming'))
args = parser.parse_args()
assert args.origin, 'An isolated daemon origin is required.'
password = os.environ['SECONDBRAIN_QUALIFICATION_PASSWORD']
cookies = http.cookiejar.CookieJar()
client = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cookies))
csrf = None
http_status = None
metrics = {}
prefix = 'm1-' + uuid.uuid4().hex[:12]
rsync = shutil.which('rsync')
assert rsync, 'Install rsync on the qualification host.'
tools = tempfile.TemporaryDirectory(prefix='sb-m1-rsync-')
atexit.register(tools.cleanup)
os.chmod(tools.name,0o755)
shutil.copy2(rsync,pathlib.Path(tools.name)/'rsync')
libraries = subprocess.run(['ldd',rsync],check=True,capture_output=True,text=True).stdout
loader = None
for library in re.findall(r'(/[^\s()]+)',libraries):
    shutil.copy2(library,pathlib.Path(tools.name)/pathlib.Path(library).name)
    if 'ld-linux' in pathlib.Path(library).name: loader=pathlib.Path(library).name
assert loader, 'The Linux rsync dynamic loader must be recorded.'

def request(method, path, body=None, headers=None, raw=None):
    global http_status
    supplied = {'Origin': args.origin, **(headers or {})}
    if csrf and method != 'GET': supplied['X-CSRF-TOKEN'] = csrf
    if body is not None:
        raw = json.dumps(body).encode(); supplied['Content-Type'] = 'application/json'
    with client.open(urllib.request.Request(args.origin + path, raw, supplied, method=method), timeout=130) as response:
        http_status = response.status
        data = response.read()
        return json.loads(data) if data else None

def login():
    global csrf
    request('POST', '/auth/login', {'password': password})
    csrf = request('GET', '/auth/antiforgery')['token']
    request('POST', '/auth/step-up', {'password': password})
    csrf = request('GET', '/auth/antiforgery')['token']

def wait(predicate, budget=30):
    started = time.monotonic()
    while time.monotonic() - started < budget:
        result = predicate()
        if result: return result, time.monotonic() - started
        time.sleep(.1)
    raise AssertionError('Deployed workflow did not converge within ' + str(budget) + ' seconds.')

def search(term, mode='keyword', source=None):
    return request('POST', '/v1/search', {'query':term, 'mode':mode, 'filters':{'source':source} if source else None})['results']

def pdf_bytes(marker=None):
    objects = [b'<< /Type /Catalog /Pages 2 0 R >>',b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',
               b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',
               b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>']
    content=b'BT /F1 12 Tf 72 700 Td ('+(marker or prefix).encode()+b' PDF spectroscopy fixture) Tj ET'
    objects.append(b'<< /Length '+str(len(content)).encode()+b' >>\nstream\n'+content+b'\nendstream')
    result=b'%PDF-1.4\n'; offsets=[]
    for index, item in enumerate(objects):
        offsets.append(len(result)); result+=str(index+1).encode()+b' 0 obj\n'+item+b'\nendobj\n'
    xref=len(result); result+=b'xref\n0 6\n0000000000 65535 f \n'
    for offset in offsets: result+=f'{offset:010d} 00000 n \n'.encode()
    return result+b'trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n'+str(xref).encode()+b'\n%%EOF\n'

def upload(data, filename, wait_seconds=5):
    boundary='fixture'+uuid.uuid4().hex
    body=('--'+boundary+'\r\nContent-Disposition: form-data; name="file"; filename="'+filename+'"\r\nContent-Type: application/octet-stream\r\n\r\n').encode()+data+('\r\n--'+boundary+'--\r\n').encode()
    return request('POST','/v1/documents?wait='+str(wait_seconds),headers={'Content-Type':'multipart/form-data; boundary='+boundary},raw=body)

def sync(operation, name, value='', inbox=False, folder=None):
    path = args.incoming + ('/inbox/' if inbox else '/' + (folder or prefix) + '/') + name
    destination = args.incoming+'/'+prefix+'/'+value if operation=='move' else ''
    payload = value if isinstance(value,bytes) else value.encode()
    if args.compose_project:
        command = ['docker','compose','--project-name',args.compose_project,'-f','deploy/compose.yaml','-f','deploy/tests/compose-smoke.yaml',
                   '--profile','provision','run','--rm','--no-deps','-T','--user','1656:1654','--volume',tools.name+':/tools:ro','--entrypoint','sh','provision','-c']
        script = {'write':'umask 0027; mkdir -p "$(dirname "$1")"; cat > /tmp/rsync-input; chmod 0640 /tmp/rsync-input; /tools/'+loader+' --library-path /tools /tools/rsync --delay-updates --times /tmp/rsync-input "$1"', 'move':'mv "$1" "$2"', 'remove':'rm "$1"'}[operation]
        subprocess.run(command + [script,'sync-fixture',path,destination], input=payload if operation=='write' else None, check=True, stdout=subprocess.DEVNULL, timeout=30)
    else:
        # Operator harness is root; source writes still execute as the supported sync identity.
        script = {'write':'umask 0027; mkdir -p "$(dirname "$1")"; input=$(mktemp); trap \'rm -f "$input"\' EXIT; cat > "$input"; chmod 0640 "$input"; rsync --delay-updates --times "$input" "$1"', 'move':'mv "$1" "$2"', 'remove':'rm "$1"'}[operation]
        subprocess.run(['setpriv','--reuid','1656','--regid','1654','--clear-groups','sh','-c',script,'sync-fixture',path,destination],
                       input=payload if operation=='write' else None, check=True, timeout=10)

def restart():
    if args.compose_project:
        subprocess.run(['docker','compose','--project-name',args.compose_project,'-f','deploy/compose.yaml','-f','deploy/tests/compose-smoke.yaml','restart','daemon'], check=True, stdout=subprocess.DEVNULL, timeout=45)
    else:
        subprocess.run(['systemctl','restart',os.environ['SECONDBRAIN_QUALIFICATION_UNIT']], check=True, timeout=45)
    def healthy():
        try:
            request('GET','/health'); return http_status == 200
        except (urllib.error.URLError, ConnectionError): return False
    wait(healthy); login()

login()
request('GET','/health'); assert http_status == 200
capture_started=time.monotonic()
captured = request('POST','/v1/documents', {'content':prefix+' capture astronomy', 'title':prefix+' text', 'tags':['qualification']})
identifier = captured['document']['id']
wait(lambda: any(hit['document_id']==identifier for hit in search(prefix)))
metrics['capture_keyword_seconds']=time.monotonic()-capture_started
assert metrics['capture_keyword_seconds'] < 1, json.dumps(metrics, sort_keys=True)
wait(lambda: request('GET','/v1/documents/'+identifier)['status']=='indexed',3)
metrics['capture_vectors_seconds']=time.monotonic()-capture_started
assert metrics['capture_vectors_seconds'] < 3, json.dumps(metrics, sort_keys=True)
assert any(doc['id']==identifier for doc in request('POST','/v1/query', {'tags':['qualification']})['records'])
pdf = upload(pdf_bytes(),prefix+'.pdf')
pdf_id=pdf['document']['id']
wait(lambda: any(hit['document_id']==pdf_id and hit['page']==1 for hit in search('spectroscopy')))
inbox_started=time.monotonic()
sync('write',prefix+'-inbox.pdf',pdf_bytes(),inbox=True)
inbox_hits,_=wait(lambda: search('spectroscopy',source='inbox'),30)
metrics['inbox_pdf_drop_to_keyword_seconds']=time.monotonic()-inbox_started
assert metrics['inbox_pdf_drop_to_keyword_seconds'] < 30, json.dumps(metrics, sort_keys=True)
inbox_id=inbox_hits[0]['document_id']
assert inbox_id != pdf_id and inbox_hits[0]['page']==1
sync('write',prefix+'-inbox.pdf',pdf_bytes(prefix+' rewritten quasar'),inbox=True)
rewritten,_=wait(lambda: search('quasar',source='inbox'),30)
assert rewritten[0]['document_id'] != inbox_id
malformed = upload(b'%PDF-1.7\nbroken',prefix+'-malformed.pdf')
malformed_id=malformed['document']['id']
wait(lambda: any(job['document_id']==malformed_id and job['status']=='failed' for job in request('GET','/v1/jobs')))
request('GET','/health'); assert http_status == 200
assert any(hit['document_id']==identifier for hit in search(prefix))
sync('write','note.md','---\ntitle: '+prefix+' source\ntags: [qualification]\n---\n# '+prefix+'\n\nInitial observational astronomy.\n')
source = request('POST','/v1/sources', {'path':args.incoming+'/'+prefix,'name':prefix,'include':['**/*.md'],'exclude':[], 'recursive':True,
                                    'mode':'index','enrich':False,'default_type':'note','type_map':{},'watch':'events','poll_interval_s':2})
hits, _ = wait(lambda: search('observational',source=source['id']))
source_id = hits[0]['document_id']
edit_started=time.monotonic()
sync('write','note.md','# '+prefix+'\n\nUpdated scintillation evidence.\n')
wait(lambda: any(hit['document_id']==source_id for hit in search('scintillation')),10)
metrics['watched_edit_seconds']=time.monotonic()-edit_started
assert metrics['watched_edit_seconds'] < 10, json.dumps(metrics, sort_keys=True)
# Qualify actual rsync publication under polling separately from event notification.
poll_folder=prefix+'-poll'
sync('write','note.md','# '+prefix+'\n\nInitial polling heliosphere evidence.\n',folder=poll_folder)
poll_source=request('POST','/v1/sources', {'path':args.incoming+'/'+poll_folder,'name':poll_folder,'include':['**/*.md'],'exclude':[], 'recursive':True,
                                         'mode':'index','enrich':False,'default_type':'note','type_map':{},'watch':'poll','poll_interval_s':5})
poll_hits,_=wait(lambda: search('heliosphere',source=poll_source['id']))
poll_id=poll_hits[0]['document_id']
poll_started=time.monotonic()
sync('write','note.md','# '+prefix+'\n\nUpdated polling magnetosphere evidence.\n',folder=poll_folder)
poll_committed=time.monotonic()
try:
    wait(lambda: any(hit['document_id']==poll_id for hit in search('magnetosphere',source=poll_source['id'])),5)
finally:
    metrics['poll_edit_after_rsync_commit_seconds']=time.monotonic()-poll_committed
    metrics['poll_edit_including_rsync_seconds']=time.monotonic()-poll_started
    metrics['poll_interval_seconds']=5
    print(json.dumps({'checkpoint':'polled-edit', 'metrics':metrics}, sort_keys=True), flush=True)
assert metrics['poll_edit_after_rsync_commit_seconds'] < 5, json.dumps(metrics, sort_keys=True)
sync('move','note.md','moved.md')
request('POST','/v1/sources/'+source['id']+'/scan?deep=true')
wait(lambda: any(hit['document_id']==source_id for hit in search('scintillation',source=source['id'])))
document = request('GET','/v1/documents/'+source_id)
request('DELETE','/v1/documents/'+source_id,headers={'If-Match':'"'+str(document['revision'])+'"'})
request('POST','/v1/sources/'+source['id']+'/scan?deep=true')
assert not any(hit['document_id']==source_id for hit in search('scintillation'))
document = request('GET','/v1/documents/'+source_id)
request('POST','/v1/documents/'+source_id+'/restore',headers={'If-Match':'"'+str(document['revision'])+'"'})
wait(lambda: any(hit['document_id']==source_id for hit in search('scintillation')))
# Pause only this disposable extractor so the file has demonstrably unfinished work.
if args.compose_project:
    extractor_control=['docker','compose','--project-name',args.compose_project,'-f','deploy/compose.yaml','-f','deploy/tests/compose-smoke.yaml']
    subprocess.run(extractor_control+['pause','extractor'],check=True,stdout=subprocess.DEVNULL,timeout=15)
else:
    extractor_unit=os.environ['SECONDBRAIN_QUALIFICATION_UNIT'].removesuffix('.service')+'-extractor.service'
    subprocess.run(['systemctl','kill','--kill-whom=all','--signal=STOP',extractor_unit],check=True,timeout=15)
try:
    pending=upload((prefix+' restart durable nebula').encode(),prefix+'-restart.txt',0)
    pending_id=pending['document']['id']
    assert any(job['document_id']==pending_id and job['stage']=='extract' and job['status'] in ('queued','running') for job in request('GET','/v1/jobs'))
    restart()
finally:
    if args.compose_project: subprocess.run(extractor_control+['unpause','extractor'],check=True,stdout=subprocess.DEVNULL,timeout=15)
    else: subprocess.run(['systemctl','kill','--kill-whom=all','--signal=CONT',extractor_unit],check=True,timeout=15)
wait(lambda: any(hit['document_id']==pending['document']['id'] for hit in search('nebula')))
before = request('GET','/v1/documents/'+source_id+'?include=text,chunks')
request('POST','/v1/maintenance/rebuild')
after = request('GET','/v1/documents/'+source_id+'?include=text,chunks')
assert (before['id'],before['revision'],before['text'],before['chunks']) == (after['id'],after['revision'],after['text'],after['chunks'])
assert any(hit['document_id']==source_id for hit in search('scintillation'))
assert any(job['document_id']==pending['document']['id'] for job in request('GET','/v1/jobs'))
print(json.dumps({'profile':'isolated deployed daemon','workflow':'text/PDF/crafted-PDF/query/watch/edit/move/suppress/restore/restart/rebuild/jobs',
                  'sync_tool':'rsync --delay-updates --times','source_identity_preserved':True,'rebuild_text_chunks_equal':True,'metrics':metrics}, sort_keys=True))

import argparse, array, base64, concurrent.futures, io, json, os, pathlib, socket, struct, subprocess, tempfile, time, uuid, zipfile

arguments=argparse.ArgumentParser(description="Qualify the shipped extractor image under its hardened offline profile.")
arguments.add_argument('--engine',default=os.environ.get('CONTAINER_ENGINE','podman'),choices=['podman','docker'])
arguments.add_argument('--image',default='localhost/secondbrain-extractor:m1-qualification')
options=arguments.parse_args(); engine=options.engine; image=options.image
if engine=='docker' and os.geteuid()!=0:
    arguments.error('Docker qualification must run as root so the host fixture can reach the UID1655 socket without broadening its permissions.')
name='secondbrain-m1-extractor-qual-'+uuid.uuid4().hex[:10]
fixture=tempfile.TemporaryDirectory(prefix='sb-extractor-qualification-')
root=pathlib.Path(fixture.name); runtime=root/'runtime'; runtime.mkdir(mode=0o700 if engine=='podman' else 0o750)
if engine=='docker':
    os.chown(runtime,1655,1654)
    os.chmod(runtime,0o750)
secret=root/'unmounted-secret'; secret.write_text('qualification private marker')

def command(args, **kwargs):
    return subprocess.run(args, check=True, capture_output=True, text=True, timeout=kwargs.pop('timeout',30), **kwargs)

def rpc(data=None, hint='.txt', operation='extract_v2'):
    request_id=uuid.uuid4().hex
    payload=json.dumps({'version':1,'id':request_id,'operation':operation,**({'format_hint':hint} if operation=='extract_v2' else {})}).encode()
    with socket.socket(socket.AF_UNIX,socket.SOCK_STREAM) as peer:
        peer.settimeout(20); peer.connect(str(runtime/'extractor.sock'))
        if data is None: peer.sendall(struct.pack('>I',len(payload)))
        else:
            path=root/('stage-'+uuid.uuid4().hex); path.write_bytes(data)
            with path.open('rb') as descriptor:
                path.unlink()
                peer.sendmsg([struct.pack('>I',len(payload))],[(socket.SOL_SOCKET,socket.SCM_RIGHTS,array.array('i',[descriptor.fileno()]))])
        peer.sendall(payload)
        def read(size):
            output=b''
            while len(output)<size:
                chunk=peer.recv(size-len(output))
                if not chunk: raise RuntimeError('truncated response')
                output+=chunk
            return output
        output=b''; sequence=0
        while True:
            length=struct.unpack('>I',read(4))[0]
            assert 0<length<=65536
            frame=json.loads(read(length))
            if operation=='ping': assert frame['ok'] and frame['result']=='pong'; return frame
            assert frame['id']==request_id and frame['version']==2 and frame['sequence']==sequence
            output+=base64.b64decode(frame['data'],validate=True); assert len(output)<=20*1024*1024
            if frame['final']: return json.loads(output)
            sequence+=1; assert sequence<1024

try:
    command([engine,'run','--detach','--rm','--name',name,*(['--userns=keep-id:uid=1655,gid=1654'] if engine=='podman' else []),'--init','--user','1655:1654','--network','none','--read-only','--cap-drop','ALL','--security-opt','no-new-privileges','--memory','1g','--pids-limit','64','--ulimit','core=0:0','--ulimit','fsize=536870912:536870912','--tmpfs','/tmp:rw,noexec,nosuid,size=512m,mode=1777','--volume',str(runtime)+':/run/secondbrain:Z','--env','DOTNET_EnableDiagnostics=0','--env','TMPDIR=/tmp','--env','SECONDBRAIN_QUALIFICATION_SECRET=fixture-value',image])
    until=time.monotonic()+20
    while not (runtime/'extractor.sock').exists():
        if time.monotonic()>until: raise RuntimeError('extractor socket never started')
        time.sleep(.05)
    assert command([engine,'exec',name,'id','-u']).stdout.strip()=='1655'
    state=command([engine,'exec',name,'cat','/proc/1/status']).stdout
    limits=command([engine,'exec',name,'cat','/proc/1/limits']).stdout
    assert 'unlimited' in next(line for line in limits.splitlines() if line.startswith('Max cpu time'))
    assert '536870912' in next(line for line in limits.splitlines() if line.startswith('Max file size'))
    assert command([engine,'exec',name,'cat','/sys/fs/cgroup/memory.max']).stdout.strip()=='1073741824'
    assert command([engine,'exec',name,'cat','/sys/fs/cgroup/pids.max']).stdout.strip()=='64'
    assert 'NoNewPrivs:\t1' in state and 'CapEff:\t0000000000000000' in state
    command([engine,'exec',name,'/bin/sh','-c','test ! -e /srv/secondbrain/state/state.db && test ! -e /etc/secondbrain/secrets && test ! -e "$1"','probe',str(secret)])
    command([engine,'exec',name,'/bin/bash','-c','if timeout 2 bash -c "exec 3<>/dev/tcp/1.1.1.1/443" >/dev/null 2>&1; then exit 1; fi'])
    command([engine,'exec',name,'/bin/sh','-c','if touch /app/qualification-write 2>/dev/null; then exit 1; fi'])
    rpc(operation='ping')
    markdown=rpc(b'---\ntitle: Container fixture\ntags: [isolated]\n---\n# Safe astronomy\n[[Target]]\n',hint='.md')
    assert markdown['status']=='extracted' and markdown['physical_format']=='markdown' and markdown['title']=='Container fixture'
    html=rpc(b"<!doctype html><html><body><p>Visible HTML fixture</p><script>hidden-active-fixture</script></body></html>",hint='.txt')
    assert html['physical_format']=='html' and 'Visible HTML fixture' in html['lossless_text'] and 'hidden-active-fixture' not in html['lossless_text']
    docx=io.BytesIO()
    with zipfile.ZipFile(docx,'w',compression=zipfile.ZIP_DEFLATED) as archive:
        archive.writestr('word/document.xml',"<w:document xmlns:w='http://schemas.openxmlformats.org/wordprocessingml/2006/main'><w:body><w:p><w:r><w:t>Word fixture</w:t></w:r></w:p></w:body></w:document>")
    word=rpc(docx.getvalue(),hint='.txt'); assert word['physical_format']=='docx' and 'Word fixture' in word['lossless_text']
    bomb=io.BytesIO()
    with zipfile.ZipFile(bomb,'w',compression=zipfile.ZIP_DEFLATED) as archive: archive.writestr('word/document.xml','<x>'+('x'*1000000)+'</x>')
    rejected_bomb=rpc(bomb.getvalue(),hint='.docx')
    assert rejected_bomb['status']=='skipped' and rejected_bomb['bound']=='compression_ratio'
    rpc(operation='ping')
    objects=[b'<< /Type /Catalog /Pages 2 0 R >>',b'<< /Type /Pages /Kids [3 0 R] /Count 1 >>',b'<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>',b'<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>']
    content=b'BT /F1 12 Tf 72 700 Td (PDF text fixture) Tj ET'; objects.append(b'<< /Length '+str(len(content)).encode()+b' >>\nstream\n'+content+b'\nendstream')
    pdf=b'%PDF-1.4\n'; offsets=[]
    for index,item in enumerate(objects): offsets.append(len(pdf)); pdf+=str(index+1).encode()+b' 0 obj\n'+item+b'\nendobj\n'
    xref=len(pdf); pdf+=b'xref\n0 6\n0000000000 65535 f \n'
    for offset in offsets: pdf+=f'{offset:010d} 00000 n \n'.encode()
    pdf+=b'trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n'+str(xref).encode()+b'\n%%EOF\n'
    pdf_text=rpc(pdf,hint='.txt'); assert pdf_text['physical_format']=='pdf' and 'PDF text fixture' in pdf_text['lossless_text'] and len(pdf_text['pages'])==1
    malformed=rpc(b'%PDF-1.7\nbroken',hint='.pdf'); assert malformed['status']=='failed'
    escaped=rpc(b'\x01'*(4*1024*1024)); assert escaped['status']=='skipped' and escaped['bound']=='serialized_output_bytes'
    rpc(operation='ping')
    with concurrent.futures.ThreadPoolExecutor() as pool:
        extracting=pool.submit(rpc,b'a'*(8*1024*1024))
        probe=r'''for pass in $(seq 1 100); do for p in /proc/[0-9]*/cmdline; do test "$(cat "${p%cmdline}comm" 2>/dev/null)" = dotnet || continue; if tr '\000' ' ' < "$p" 2>/dev/null | grep -q -- '--parse-worker'; then workerlimits=$(cat "${p%cmdline}limits" 2>/dev/null) || continue; printf '%s\n' "$workerlimits" | grep -Eq '^Max cpu time[[:space:]]+120[[:space:]]+120' || continue; envfile="${p%cmdline}environ"; values=$(tr '\000' '\n' < "$envfile" 2>/dev/null) || continue; if printf '%s\n' "$values" | grep -q '^SECONDBRAIN_QUALIFICATION_SECRET='; then exit 1; fi; printf '%s\n' "$workerlimits"; exit 0; fi; done; sleep 0.01; done; exit 2'''
        worker_limits=command([engine,'exec',name,'/bin/sh','-c',probe]).stdout
        assert '120' in next(line for line in worker_limits.splitlines() if line.startswith('Max cpu time'))
        large=extracting.result(timeout=20)
        assert large['status']=='extracted' and len(large['lossless_text'])==8*1024*1024
    print(json.dumps({'image':image,'image_id':command([engine,'image','inspect','--format','{{.Id}}',image]).stdout.strip(),'uid':1655,'network':'external TCP denied','stores_secrets':'not mounted/inaccessible','root_filesystem':'read-only','capabilities':'none','no_new_privileges':True,'memory_max_bytes':1073741824,'pids_max':64,'parser_cpu_limit_seconds':120,'supervisor_cpu_limit':'unlimited','file_size_limit_bytes':536870912,'worker_environment':'injected fixture secret absent','descriptor_formats':['markdown','text','pdf','html','docx'],'serialized_output_bound':'passed','docx_compression_bomb_then_ping':'passed','crafted_pdf_then_ping':'passed','large_stream_bytes':8*1024*1024,'profile':'shipped Compose-equivalent '+engine+'; rootless keep-id or root-owned CI fixture enables host socket access'}))
finally:
    subprocess.run([engine,'rm','--force',name],capture_output=True,timeout=15)
    fixture.cleanup()

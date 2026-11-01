"""Docker fixture: named disposable volumes only; no host/daemon mount in actors."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import select
import struct
import subprocess
import time
import uuid

HERE = Path(__file__).resolve().parent
IMAGE = os.environ.get("DUORA_BOUNDARY_IMAGE", "duora-generation-boundary:local")
ACTORS = {"legacy": 1654, "client": 17020, "authority": 17010, "controller": 17030, "root": 0}


def checked(args, **kwargs):
    return subprocess.run(args, check=True, capture_output=True, text=True, timeout=60, **kwargs)


# Executed as root with CAP_CHOWN only, exclusively inside disposable named volumes.
BOOTSTRAP = r'''
import hashlib, json, os, stat, uuid, base64
from pathlib import Path
root=Path('/state')
root.chmod(0o755)
witness=Path('/witness-state')
witness.chmod(0o755)
def folder(path, uid, gid, mode):
    path.mkdir()
    path.chmod(mode)
    os.chown(path,uid,gid)
def identity(path):
    s=path.stat(follow_symlinks=False)
    return dict(inode=s.st_ino,deviceMajor=os.major(s.st_dev),deviceMinor=os.minor(s.st_dev))
def put(path,uid,gid,mode,content):
    with path.open('xb') as f:
        f.write(content)
        f.flush()
        os.fsync(f.fileno())
    path.chmod(mode)
    os.chown(path,uid,gid)
def sync(path):
    fd=os.open(path,os.O_RDONLY|os.O_DIRECTORY)
    try: os.fsync(fd)
    finally: os.close(fd)
domain=uuid.uuid4().hex
generations=[dict(generation=uuid.uuid4().hex,area=area,nonce=os.urandom(32).hex(),committed=committed)
             for area in ('candidate','promotion') for committed in (False,False,True)]
def journal(entries):
    prev='0'*64
    lines=[]
    for seq,entry in enumerate(entries,1):
        payload=base64.b64encode(json.dumps(entry,separators=(',',':')).encode()).decode()
        digest=hashlib.sha256(f'{seq}\n{prev}\n{payload}'.encode()).hexdigest()
        lines.append(json.dumps(dict(seq=seq,prev=prev,payload=payload,hash=digest),separators=(',',':')))
        prev=digest
    return ('\n'.join(lines)+'\n').encode()
def entry(state,g=None):
    return dict(domain=domain,epoch=1,protocolVersion=2,generation=g['generation'] if g else '0'*32,
                area=g['area'] if g else '',state=state,witnessRevision=0)
# Immutable names belong to root; only the authority group can access the controls.
folder(root/'control',0,17010,0o750)
put(root/'control/domain.lock',0,17010,0o660,b'')
put(root/'control/domain.journal',0,17010,0o660,journal([entry('Genesis')]+[entry(state,g) for g in generations for state in ('Reserved','Open')]))
folder(root/'data',0,0,0o755)
for area in ('review-candidates','derivatives'): folder(root/'data'/area,17010,17010,0o700)
folder(root/'rpc',0,0,0o755)
folder(root/'rpc/authority',17010,17010,0o711)
folder(root/'rpc/controller',17030,17030,0o711)
folder(witness/'control',0,17030,0o750)
put(witness/'control/domain.lock',0,17030,0o660,b'')
put(witness/'control/domain.journal',0,17030,0o660,journal([entry('Genesis')]))
for path,gid in ((root,17010),(witness,17030)):
    layout=dict(domain=domain,epoch=1,protocolVersion=2,generations=generations,
                root=identity(path),control=identity(path/'control'),
                files={name:identity(path/'control'/name) for name in ('domain.lock','domain.journal')},
                data={area:identity(root/'data'/area) for area in ('review-candidates','derivatives')})
    put(path/'enrollment.json',0,gid,0o440,json.dumps(layout,separators=(',',':')).encode())
    sync(path/'control')
    sync(path)
print(json.dumps(dict(domain=domain,epoch=1,protocolVersion=2,generations=generations)))
'''


class BoundaryFixture:
    def __init__(self, area, servers=True):
        self.area = area
        self.servers = servers
        self.container = "duora-boundary-" + uuid.uuid4().hex
        self.volumes = [self.container + "-data", self.container + "-witness"]
        self.processes = {}
        self._closed = False

    def start(self):
        try:
            for volume in self.volumes:
                checked(["docker", "volume", "create", volume])
            checked(["docker", "run", "-d", "--name", self.container, "--network", "none",
                     "--read-only", "--cap-drop", "ALL", "--cap-add", "CHOWN",
                     "--security-opt", "no-new-privileges", "--user", "0:0",
                     "--tmpfs", "/tmp:rw,nosuid,nodev,mode=1777",
                     "--mount", f"type=volume,source={self.volumes[0]},destination=/state",
                     "--mount", f"type=volume,source={self.volumes[1]},destination=/witness-state",
                     "--mount", f"type=bind,source={HERE},destination=/harness,readonly",
                     "--entrypoint", "/bin/sleep", IMAGE, "infinity"])
            result = checked(["docker", "exec", "-i", self.container, "python3", "-c", BOOTSTRAP])
            self.manifest = json.loads(result.stdout)
            self.grants = [dict(domain={k: self.manifest[k] for k in ("domain", "epoch", "protocolVersion")},
                                generation=g["generation"], area=g["area"], nonce=g["nonce"])
                           for g in self.manifest["generations"] if g["area"] == self.area and not g["committed"]]
            self.active = self.grants[0]
            if self.servers:
                self.start_server("controller")
                self.start_server("authority")
            return self
        except BaseException:
            self.close_fixture()
            raise

    def actor(self, actor, command, **kwargs):
        uid = ACTORS[actor]
        return checked(["docker", "exec", "-i", "--user", f"{uid}:{uid}", self.container] + command, **kwargs)

    def call(self, actor, command):
        return json.loads(self.actor(actor, ["python3", "/harness/client.py"], input=json.dumps(command)).stdout)

    def rpc(self, actor, operation, body_length=3, grant=None, variant="thumb", **overrides):
        request = dict(version=2, operation=operation, grant=grant or self.active, variant=variant, length=body_length)
        request.update(overrides.pop("request_overrides", {}))
        command = dict(mode="rpc", socket="/state/rpc/authority/service.sock", request=request, **overrides)
        return self.call(actor, command)

    def inspect(self, path):
        script = "import hashlib,json,os,sys; p=sys.argv[1]; s=os.lstat(p); print(json.dumps({'inode':s.st_ino,'hash':hashlib.sha256(open(p,'rb').read()).hexdigest(),'mode':s.st_mode&0o777,'uid':s.st_uid,'gid':s.st_gid}))"
        result = self.actor("root", ["python3", "-c", script, path])
        return json.loads(result.stdout)

    def file_attack(self, actor, operation, target):
        path = "/state/control/" + target
        before = self.inspect(path)
        result = self.call(actor, dict(mode="attack", operation=operation, target=path))
        try:
            after = self.inspect(path)
        except subprocess.CalledProcessError:
            after = None
        return dict(result, before=before, after=after)

    def start_server(self, role, point="none", variant=0):
        uid = ACTORS[role]
        process = subprocess.Popen(["docker", "exec", "-i", "--user", f"{uid}:{uid}", self.container,
                                    "dotnet", "/probe/BoundaryProbe.dll", role, point, str(variant)],
                                   stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                                   text=True, bufsize=1)
        self.processes[role] = process
        line = self.read_line(role)
        ready = json.loads(line)
        if ready.get("status") != "LISTENING":
            raise AssertionError("server did not start")
        process.remote_pid = ready["pid"]

    def read_line(self, role):
        process = self.processes[role]
        if not select.select([process.stdout], [], [], 20)[0]:
            raise AssertionError("server barrier timed out")
        line = process.stdout.readline()
        if not line:
            raise AssertionError(process.stderr.read())
        return line.strip()

    def wait_barrier(self, role="authority"):
        assert self.read_line(role) == "READY"

    def release_barrier(self, role="authority"):
        self.processes[role].stdin.write("go\n")
        self.processes[role].stdin.flush()

    def kill_server(self, role):
        process = self.processes.pop(role)
        self.actor(role, ["python3", "-c", "import os,signal,sys; os.kill(int(sys.argv[1]),signal.SIGKILL)", str(process.remote_pid)])
        out, err = process.communicate(timeout=20)
        if process.returncode != 137:
            raise AssertionError((process.returncode, out, err))

    def private_files(self, grant=None):
        grant = grant or self.active
        prefix = ("review-candidates/" if grant["area"] == "candidate" else "derivatives/review-") + grant["generation"]
        script = "import json,pathlib,sys; print(json.dumps(sorted(str(p.relative_to('/state/data')) for p in pathlib.Path('/state/data').rglob('*') if p.is_file() and str(p.relative_to('/state/data')).startswith(sys.argv[1]))))"
        return json.loads(self.actor("authority", ["python3", "-c", script, prefix]).stdout)

    def fault(self, script, *args):
        return self.actor("root", ["python3", "-c", script, *args])

    def close_fixture(self):
        if self._closed:
            return
        self._closed = True
        subprocess.run(["docker", "rm", "-f", self.container], capture_output=True, timeout=30)
        for process in self.processes.values():
            process.communicate(timeout=20)
        for volume in self.volumes:
            subprocess.run(["docker", "volume", "rm", volume], capture_output=True, timeout=30)


@contextlib.contextmanager
def fixture(area="candidate", servers=True):
    instance = BoundaryFixture(area, servers).start()
    try:
        yield instance
    finally:
        instance.close_fixture()

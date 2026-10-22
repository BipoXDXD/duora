"""Native hosted-runner isolation proof. Root orchestration is confined to disposable CI.
Never invoke this harness on the developer Mac or on an application host.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import platform
import pwd
import shutil
import signal
import socket
import struct
import subprocess
import tempfile
import time
import zlib
from pathlib import Path

REPO = Path(__file__).resolve().parents[2]
TRIVY = "ghcr.io/aquasecurity/trivy:0.75.0@sha256:af6acf9a6b85dfe389a1941505c0ce9efef52a4719635e1a962f022a3d855daa"
CLIENT = '''import socket,sys
s=socket.socket(socket.AF_UNIX);s.settimeout(65);s.connect(sys.argv[1])
s.sendall(sys.stdin.buffer.read(5243000));s.shutdown(socket.SHUT_WR)
result=bytearray()
while True:
 b=s.recv(65536)
 if not b:break
 result.extend(b)
 if len(result)>25175000:raise SystemExit(9)
sys.stdout.buffer.write(result)
'''


def checked(args, **kwargs):
    return subprocess.run(args, check=True, **kwargs)


class Proof:
    def __init__(self, args):
        assert os.geteuid() == 0 and platform.system() == "Linux" and os.environ.get("GITHUB_ACTIONS") == "true"
        self.args = args
        self.user = pwd.getpwuid(args.launcher_uid).pw_name
        self.worker = pwd.getpwuid(args.worker_uid).pw_name
        self.host = f"unix:///run/user/{args.launcher_uid}/docker.sock"
        self.root = Path(tempfile.mkdtemp(prefix="duora-s2-", dir="/tmp"))
        self.root.chmod(0o711)
        self.code = self.root / "code"; self.code.mkdir(mode=0o755)
        for name in ["launcher.py", "docker_runtime.py", "run_launcher.py"]:
            shutil.copyfile(REPO/"scripts/media"/name, self.code/name)
            (self.code/name).chmod(0o644)
        # Separate client group; neither user gains access to the other's Docker API.
        checked(["groupadd", "duora-media-ci-client"])
        group = int(subprocess.check_output(["getent","group","duora-media-ci-client"],text=True).split(":")[2])
        for user in [self.user,self.worker]: checked(["usermod","-aG","duora-media-ci-client",user])
        self.control = self.root/"control"; self.control.mkdir(mode=0o750)
        self.state = self.root/"state"; self.state.mkdir(mode=0o700)
        os.chown(self.control,args.launcher_uid,group); os.chown(self.state,args.launcher_uid,group)
        self.profile = {"schema":1,"image":"", "runtime_socket":f"/run/user/{args.launcher_uid}/docker.sock",
            "control_socket":str(self.control/"socket"),"journal_directory":str(self.state),
            "worker_uid":args.worker_uid,"control_gid":group}
        self.profile_path = self.root/"profile.json"
        self.process = None
        self.events = []
        self.report = {"architecture":platform.machine(),"candidateOnly":True,"cases":[],"complete":False}

    def docker(self, *args):
        return subprocess.check_output(["runuser","-u",self.user,"--","docker","--host",self.host,*args],timeout=60)

    def build(self):
        self.images = {}
        for target in ["sanitizer","isolation-probe"]:
            tag = f"duora-{target}:s2-ci"
            checked(["docker","build","-f",str(REPO/"infra/images/media-sanitizer/Dockerfile"),
                     "--target",target,"-t",tag,str(REPO)])
            # Same scanner/policy as the existing images gate; no publication or registry credentials.
            checked(["docker","run","--rm","-v","/var/run/docker.sock:/var/run/docker.sock:ro",
                "-v",str(REPO/".trivyignore")+":/trivyignore:ro",TRIVY,"image","--severity","HIGH,CRITICAL",
                "--ignore-unfixed","--ignorefile","/trivyignore","--exit-code","1",tag])
            archive = self.root/(target+".tar")
            checked(["docker","save","-o",str(archive),tag])
            with archive.open("rb") as source:
                checked(["runuser","-u",self.user,"--","docker","--host",self.host,"load"],stdin=source)
            archive.unlink()
            image = self.docker("image","inspect",tag,"--format","{{.Id}}").decode().strip()
            self.images[target]=image
        self.report["images"]=self.images
        info=json.loads(self.docker("info","--format","{{json .}}"))
        self.report["runtime"]={key:info[key] for key in ["ServerVersion","CgroupDriver","CgroupVersion"]}

    def start(self, image):
        assert not self.process
        self.profile["image"]=image
        self.profile_path.write_text(json.dumps(self.profile)); self.profile_path.chmod(0o644)
        self.process=subprocess.Popen(["runuser","-u",self.user,"--","env","-i","PATH=/usr/bin:/bin",
            "CANARY_SECRET=synthetic-not-forwarded","/usr/bin/python3",str(self.code/"run_launcher.py"),str(self.profile_path)],
            stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL,start_new_session=True)
        deadline=time.monotonic()+30
        while time.monotonic()<deadline:
            if self.process.poll() is not None: raise AssertionError("launcher-startup-exit-"+str(self.process.returncode))
            if Path(self.profile["control_socket"]).exists():
                try:
                    # SIGKILL leaves the old socket path behind. Readiness means the
                    # new server has completed recovery and answers the closed protocol.
                    if self.exchange(os.urandom(16).hex(), operation=2) == (5,b""): return
                except subprocess.CalledProcessError:
                    pass
            time.sleep(0.1)
        raise AssertionError("launcher-startup-timeout")

    def stop(self, *, crash=False):
        if not self.process:return
        os.killpg(self.process.pid, signal.SIGKILL if crash else signal.SIGTERM)
        try:self.process.wait(timeout=15)
        finally:
            if self.process.poll() is None:
                os.killpg(self.process.pid,signal.SIGKILL);self.process.wait(timeout=5)
            self.process=None

    def wire(self, job, content, *, format=1, deadline=50000, total=25165824, stderr=65536):
        return (b"DMRQ\x01"+bytes.fromhex(job)+bytes([format])+struct.pack("<i",len(content))
                +hashlib.sha256(content).digest()+struct.pack("<iiii",16777216,total,stderr,deadline)+content)

    def raw_client(self, content):
        return checked(["runuser","-u",self.worker,"--","python3","-c",CLIENT,self.profile["control_socket"]],
            input=content,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,timeout=70).stdout

    def exchange(self, job, content=b"", *, operation=1, **kwargs):
        wire=self.wire(job,content,**kwargs) if operation==1 else b""
        result=self.raw_client(b"DMLC\x01"+bytes([operation])+bytes.fromhex(job)+wire)
        assert len(result)>=54 and result[:5]==b"DMLR\x01" and result[6:22]==bytes.fromhex(job)
        digest=hashlib.sha256(json.dumps({"policy":"s2-candidate-v1",**{k:v for k,v in self.profile.items() if k!="schema"}},sort_keys=True).encode()).digest()
        assert result[22:54]==digest
        return result[5],result[54:]

    def assert_empty(self):
        assert not self.docker("ps","--all","--filter","label=io.duora.media.managed=1","--quiet").strip()

    def case(self, name, action):
        began=time.monotonic()
        try:
            action()
            self.report["cases"].append({"name":name,"passed":True,"milliseconds":round((time.monotonic()-began)*1000,2)})
        except BaseException:
            self.report["cases"].append({"name":name,"passed":False,"milliseconds":round((time.monotonic()-began)*1000,2)})
            raise

    def fault(self, mode, **kwargs):
        job=os.urandom(16).hex()
        code,output=self.exchange(job,mode.encode(),**kwargs)
        assert code==4 and not output,(mode,code,len(output))
        self.assert_empty()

    def async_sleep(self, mode=b"sleep"):
        job=os.urandom(16).hex()
        wire=b"DMLC\x01\x01"+bytes.fromhex(job)+self.wire(job,mode)
        client=subprocess.Popen(["runuser","-u",self.worker,"--","python3","-c",CLIENT,self.profile["control_socket"]],
            stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL)
        client.stdin.write(wire);client.stdin.close();client.stdin=None
        deadline=time.monotonic()+8
        while time.monotonic()<deadline:
            ids=self.docker("ps","--filter","name=^/duora-media-"+job+"$","--quiet").strip()
            if ids:return job,client
            if client.poll() is not None:raise AssertionError("fault-candidate-never-started")
            time.sleep(0.1)
        raise AssertionError("fault-candidate-startup-timeout")

    def run(self):
        self.build();self.start(self.images["isolation-probe"])
        def inspect():
            code,payload=self.exchange(os.urandom(16).hex(),b"inspect")
            assert code==0
            data=json.loads(payload)
            for key in ["nonRoot","noCapabilities","noPrivileges","seccomp","noSecrets","noStorage","noDockerSocket",
                        "rootReadOnly","tmpfsBounded","noExec","noNetwork","noDns"]:assert data[key],key
            assert (data["memory"],data["swap"],data["cpu"],data["pids"])==("536870912","0","100000 100000","64")
            self.report["effectiveControls"]=data;self.assert_empty()
        self.case("effective-cgroups-network-dns-storage-secrets-readonly-tmpfs-seccomp",inspect)
        def peer():
            with socket.socket(socket.AF_UNIX) as client:
                client.settimeout(2);client.connect(self.profile["control_socket"])
                assert not client.recv(1) # root is not the allowed Worker UID.
        self.case("unauthorized-kernel-peer-rejected-before-header",peer)
        for mode in ["stdout","stderr","crash"]:
            self.case(mode+"-withholds-all-bytes",lambda mode=mode:self.fault(mode,total=4096))
        self.case("hard-deadline-withholds-bytes",lambda:self.fault("sleep",deadline=1500))
        self.case("descendants-terminated-at-deadline",lambda:self.fault("children",deadline=4000))
        def oom():
            since=str(int(time.time())-1)
            self.fault("oom",deadline=15000)
            events=self.docker("events","--since",since,"--until",str(int(time.time())+1),
                "--filter","label=io.duora.media.managed=1","--format","{{json .}}")
            assert any(json.loads(line).get("Action")=="oom" for line in events.splitlines())
            self.report["kernelOomObserved"]=True
        self.case("kernel-oom-is-not-success",oom)
        def cancel():
            job,client=self.async_sleep()
            assert self.exchange(os.urandom(16).hex(),b"inspect")[0]==1
            self.exchange(job,operation=3)
            output=client.communicate(timeout=15)[0]
            assert output[5]==3 and len(output)==54
            self.assert_empty()
        self.case("one-slot-and-explicit-cancel",cancel)
        def sigkill():
            job,client=self.async_sleep()
            self.docker("kill","--signal","KILL","duora-media-"+job)
            output=client.communicate(timeout=15)[0]
            assert output[5]==4 and len(output)==54
            self.assert_empty()
        self.case("sigkill-cannot-release-a-result",sigkill)
        def restart():
            job,client=self.async_sleep(b"children")
            self.stop(crash=True)
            client.communicate(timeout=15)
            self.start(self.images["isolation-probe"])
            self.assert_empty()
            assert self.exchange(job,b"inspect")[0]==2
            assert b"children" not in (self.state/"journal.json").read_bytes()
        self.case("launcher-sigkill-orphan-cleanup-and-durable-dedupe",restart)
        self.stop();self.start(self.images["sanitizer"])
        def corpus():
            def chunk(name,data):return struct.pack(">I",len(data))+name+data+struct.pack(">I",zlib.crc32(name+data))
            png=b"\x89PNG\r\n\x1a\n"+chunk(b"IHDR",struct.pack(">IIBBBBB",2,2,8,2,0,0,0))+chunk(b"IDAT",zlib.compress(b"\x00\xff\x00\x00\x00\xff\x00"*2))+chunk(b"IEND",b"")
            job=os.urandom(16).hex();code,payload=self.exchange(job,png,format=2)
            assert code==0 and payload[:5]==b"DMRS\x01" and payload[5:21]==bytes.fromhex(job) and payload[53]==1
            self.report["sanitizerResponseBytes"]=len(payload)
            self.report["sanitizerResponseHash"]=hashlib.sha256(payload).hexdigest()
            self.assert_empty()
        self.case("actual-unmodified-dotnet-sanitizer-produces-three-variants",corpus)
        self.report["complete"]=True

    def dispose(self):
        self.stop()
        self.args.report.parent.mkdir(parents=True,exist_ok=True)
        self.args.report.write_text(json.dumps(self.report,indent=2)+"\n")
        shutil.rmtree(self.root,ignore_errors=True)


def main():
    parser=argparse.ArgumentParser()
    parser.add_argument("--launcher-uid",type=int,required=True)
    parser.add_argument("--worker-uid",type=int,required=True)
    parser.add_argument("--report",type=Path,required=True)
    proof=Proof(parser.parse_args())
    try:proof.run()
    finally:proof.dispose()


if __name__=="__main__": main()

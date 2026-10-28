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
import re
import shutil
import signal
import socket
import struct
import subprocess
import tempfile
import time
import sys
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

NATIVE_CORPUS_CASES = {"jpeg-20mp-texture", "png-20mp-texture", "webp-20mp-texture", "png-dense-noise",
    "jpeg-metadata-icc", "png-metadata-icc", "webp-metadata-icc", "jpeg-gps-rotation",
    "png-html-polyglot", "jpeg-zip-polyglot", "png-animation", "webp-animation", "png-declared-jpeg",
    "jpeg-bomb", "png-bomb", "webp-bomb", "png-truncated", "jpeg-byte-limit"}


def validate_native_corpus_report(path, profile_sha256):
    if not path.is_file() or path.is_symlink() or path.stat().st_size > 1024 * 1024:
        raise ValueError("Missing or oversized native corpus report")
    report = json.loads(path.read_text())
    if (type(report.get("schema")) is not int or report["schema"] != 1
            or report.get("complete") is not True or report.get("profileSha256") != profile_sha256):
        raise ValueError("Incomplete native corpus report or mismatched running profile")
    cases = report.get("cases")
    if (not isinstance(cases, list) or len(cases) != 18
            or any(not isinstance(row, dict) or not isinstance(row.get("name"), str) for row in cases)
            or {row["name"] for row in cases} != NATIVE_CORPUS_CASES):
        raise ValueError("Native corpus report must contain all 18 unique trusted cases")
    return report


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
        from launcher import AUTHORITY_DIRECTORY
        self.authority = AUTHORITY_DIRECTORY
        self.authority.mkdir(mode=0o700)
        os.chown(self.authority,args.launcher_uid,group)
        self.client_group = group
        self.profile = {"schema":1,"image":"", "runtime_socket":f"/run/user/{args.launcher_uid}/docker.sock",
            "control_socket":str(self.control/"socket"),"journal_directory":str(self.state),
            "worker_uid":args.worker_uid,"control_gid":group}
        self.profile_path = self.root/"profile.json"
        self.process = None
        self.events = []
        self.report = {"architecture":platform.machine(),"candidateOnly":True,"cases":[],"complete":False}

    def docker(self, *args, wire=None):
        return subprocess.check_output(["runuser","-u",self.user,"--","docker","--host",self.host,*args],input=wire,timeout=60)

    def build(self):
        self.images = {}
        targets = ["sanitizer", "isolation-probe"]
        if self.args.sanitizer_image:
            # Publisher preloads this immutable digest before the launcher starts;
            # no rebuild, pull, auth or registry access is delegated to a photo job.
            info = json.loads(self.docker("image", "inspect", self.args.sanitizer_image))[0]
            assert self.args.sanitizer_image in info.get("RepoDigests", [])
            assert info["Id"] == self.args.sanitizer_config and info["Os"] == "linux"
            assert info["Architecture"] == {"x86_64": "amd64", "aarch64": "arm64"}[platform.machine()]
            self.images["sanitizer"] = info["Id"]
            targets = ["isolation-probe"]
        for target in targets:
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
        self.sanitizer_image = self.args.sanitizer_image or self.images["sanitizer"]
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
        return self.response(job, result)

    def response(self, job, result):
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

    def finish_deadline_proof(self, job, code, output):
        assert code in (4, 8) and not output, ("deadline", code, len(output))
        if code == 4:
            self.assert_empty()
            return
        # Blocked is not confirmed cleanup: prove admission refusal and durable
        # fencing, then restart the real launcher and prove recovery and dedupe.
        assert self.exchange(job, operation=2) == (8, b"")
        refused = os.urandom(16).hex()
        assert self.exchange(refused, b"sleep") == (8, b"")
        path = self.state / "journal.json"
        assert path.stat().st_size <= 2 * 1024 * 1024
        records = json.loads(path.read_text())
        assert any(row["id"] == job for row in records)
        assert not any(row["id"] == refused for row in records)
        self.stop()
        self.start(self.images["isolation-probe"])
        self.assert_empty()
        assert self.exchange(job, operation=2) == (7, b"")
        assert self.exchange(job, b"sleep") == (2, b"")
        self.assert_empty()

    def deadline_fault(self, mode, *, deadline=1500):
        job = os.urandom(16).hex()
        code, output = self.exchange(job, mode.encode(), deadline=deadline)
        self.finish_deadline_proof(job, code, output)

    def hard_deadline(self):
        self.deadline_fault("sleep")
        # Exercise blocked recovery deterministically on our one disposable
        # CI daemon, rather than waiting for scheduler pressure to select it.
        job, client = self.async_sleep(deadline=5000)
        pids = subprocess.check_output(["pgrep", "-u", str(self.args.launcher_uid), "-x", "dockerd"], text=True).split()
        assert len(pids) == 1 and pids[0].isdecimal()
        pid = int(pids[0])
        try:
            os.kill(pid, signal.SIGSTOP)
            result, _ = client.communicate(timeout=15)
            assert client.returncode == 0
        finally:
            os.kill(pid, signal.SIGCONT)
            if client.poll() is None:
                os.killpg(client.pid, signal.SIGKILL)
                client.wait(timeout=5)
        code, output = self.response(job, result)
        assert code == 8 and not output
        self.finish_deadline_proof(job, code, output)

    def async_sleep(self, mode=b"sleep", *, deadline=50000):
        job=os.urandom(16).hex()
        wire=b"DMLC\x01\x01"+bytes.fromhex(job)+self.wire(job,mode,deadline=deadline)
        client=subprocess.Popen(["runuser","-u",self.worker,"--","python3","-c",CLIENT,self.profile["control_socket"]],
            stdin=subprocess.PIPE,stdout=subprocess.PIPE,stderr=subprocess.DEVNULL,start_new_session=True)
        client.stdin.write(wire);client.stdin.close();client.stdin=None
        startup_deadline=time.monotonic()+8
        while time.monotonic()<startup_deadline:
            ids=self.docker("ps","--filter","name=^/duora-media-"+job+"$","--quiet").strip()
            if ids:return job,client
            if client.poll() is not None:raise AssertionError("fault-candidate-never-started")
            time.sleep(0.1)
        raise AssertionError("fault-candidate-startup-timeout")

    def systemd_template(self):
        # Execute the supplied sandboxing template on the disposable runner only.
        # /tmp is deliberately unavailable inside its PrivateTmp namespace.
        code = Path("/opt/duora-s2-ci")
        configuration = Path("/etc/duora-s2-ci.json")
        unit = Path("/run/systemd/system/duora-media-s2-ci.service")
        previous = dict(self.profile)
        journal = self.authority/"service-journal"; journal.mkdir(mode=0o700)
        os.chown(journal,self.args.launcher_uid,self.client_group)
        shutil.copytree(self.code,code)
        self.profile.update(control_socket="/run/duora-media/socket", journal_directory=str(journal))
        configuration.write_text(json.dumps(self.profile)); configuration.chmod(0o644)
        template=(REPO/"infra/media/systemd/duora-media-launcher.service").read_text()
        # A silent no-op replacement would run the CI unit against the production paths.
        for fixed in ("User=duora-media-launcher", "Group=duora-media-client",
                      "/opt/duora/media/run_launcher.py /etc/duora-media-launcher.json"):
            assert fixed in template, fixed
        template=template.replace("User=duora-media-launcher", "User="+self.user)
        template=template.replace("Group=duora-media-client", "Group=duora-media-ci-client")
        template=template.replace("/opt/duora/media/run_launcher.py /etc/duora-media-launcher.json",
                                  str(code/"run_launcher.py")+" "+str(configuration))
        unit.write_text(template)
        try:
            checked(["systemctl","daemon-reload"])
            checked(["systemctl","start",unit.name])
            deadline=time.monotonic()+30
            while time.monotonic()<deadline:
                try:
                    if self.exchange(os.urandom(16).hex(),operation=2)==(5,b""): break
                except subprocess.CalledProcessError: pass
                time.sleep(0.1)
            else: raise AssertionError("restricted-systemd-template-not-ready")
            outcome,result=self.exchange(os.urandom(16).hex(),self.synthetic_png,format=2)
            assert outcome==0 and result[:5]==b"DMRS\x01"
            self.assert_empty()
            if self.args.sanitizer_image:
                from launcher import Profile
                self.native_corpus()
                # Capture the actual profile while this restricted service is running.
                # Fault cases use the separately reported immutable probe under S2 policy.
                self.report["imageReference"] = self.sanitizer_image
                self.report["profileSha256"] = Profile.from_dict(self.profile, self.args.launcher_uid).digest
                self.args.tested_profile.write_text(json.dumps(self.profile) + "\n")
        finally:
            checked(["systemctl","stop",unit.name])
            unit.unlink(missing_ok=True); checked(["systemctl","daemon-reload"])
            configuration.unlink(missing_ok=True); shutil.rmtree(code,ignore_errors=True)
            self.profile=previous

    def native_corpus(self):
        from launcher import Profile
        profile = Profile.from_dict(self.profile, self.args.launcher_uid).digest
        directory = self.root / "native-corpus"
        directory.mkdir(mode=0o700)
        os.chown(directory, self.args.worker_uid, pwd.getpwuid(self.args.worker_uid).pw_gid)
        report = directory / "report.json"
        environment = {"PATH": "/usr/bin:/bin", "DOTNET_ROOT": str(self.args.corpus_dotnet.parent),
            "DOTNET_NOLOGO": "true", "DOTNET_CLI_TELEMETRY_OPTOUT": "true",
            "GITHUB_ACTIONS": "true", "GITHUB_STEP_SUMMARY": str(directory / "summary.md"),
            "DUORA_NATIVE_CORPUS_REQUIRED": "true", "DUORA_NATIVE_CORPUS_SOCKET": self.profile["control_socket"],
            "DUORA_NATIVE_CORPUS_UID": str(self.args.launcher_uid), "DUORA_NATIVE_CORPUS_PROFILE": profile,
            "DUORA_NATIVE_CORPUS_REPORT": str(report)}
        command = ["runuser", "-u", self.worker, "--", "env", "-i",
            *[f"{key}={value}" for key, value in environment.items()], str(self.args.corpus_dotnet),
            str(self.args.corpus_assembly), "-class", "Duora.IntegrationTests.Media.SanitizerResourceEnvelopeTests",
            "-showLiveOutput", "-reporter", "verbose"]
        process = subprocess.Popen(command, start_new_session=True)
        try:
            assert process.wait(timeout=1200) == 0, "Native corpus comparison did not pass"
        finally:
            # Also kill descendants if a test runner exited early while leaving a baseline child.
            try: os.killpg(process.pid, signal.SIGKILL)
            except ProcessLookupError: pass
            process.wait(timeout=5)
        result = validate_native_corpus_report(report, profile)
        self.assert_empty()
        self.args.corpus_report.write_text(json.dumps(result, indent=2) + "\n")

    def run(self):
        self.build()
        def writable_root_control():
            # Same immutable probe/profile, changing only rootfs RO. This must
            # falsify the probe even though /app remains root-owned (DAC denied).
            sys.path.insert(0,str(REPO/"scripts/media"))
            from launcher import Profile, job_name
            self.profile["image"]=self.images["isolation-probe"]
            profile=Profile.from_dict(self.profile,self.args.launcher_uid)
            job=os.urandom(16).hex()
            arguments=profile.create_args(job); arguments.remove("--read-only")
            self.docker(*arguments)
            try:
                payload=self.docker("start","--attach","--interactive",job_name(job),wire=self.wire(job,b"inspect"))
                observation=json.loads(payload)
                assert observation["rootReadOnly"] is False and observation["rootMountReadOnly"] is False
                self.report["writableRootNegativeControl"]=True
            finally: self.docker("rm","--force",job_name(job))
            self.assert_empty()
        self.case("writable-root-negative-control-rejects-dac-false-positive",writable_root_control)
        self.start(self.images["isolation-probe"])
        def inspect():
            code,payload=self.exchange(os.urandom(16).hex(),b"inspect")
            assert code==0
            data=json.loads(payload)
            for key in ["nonRoot","noCapabilities","noPrivileges","seccomp","noSecrets","noStorage","noDockerSocket",
                        "rootReadOnly","rootMountReadOnly","tmpfsBounded","noExec","noNetwork","noDns"]:assert data[key],key
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
        self.case("hard-deadline-withholds-bytes",self.hard_deadline)
        self.case("descendants-terminated-at-deadline",lambda:self.deadline_fault("children",deadline=4000))
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
        self.stop();self.start(self.sanitizer_image)
        def corpus():
            def chunk(name,data):return struct.pack(">I",len(data))+name+data+struct.pack(">I",zlib.crc32(name+data))
            png=b"\x89PNG\r\n\x1a\n"+chunk(b"IHDR",struct.pack(">IIBBBBB",2,2,8,2,0,0,0))+chunk(b"IDAT",zlib.compress(b"\x00\xff\x00\x00\x00\xff\x00"*2))+chunk(b"IEND",b"")
            self.synthetic_png=png
            job=os.urandom(16).hex();code,payload=self.exchange(job,png,format=2)
            assert code==0 and payload[:5]==b"DMRS\x01" and payload[5:21]==bytes.fromhex(job) and payload[53]==1
            self.report["sanitizerResponseBytes"]=len(payload)
            self.report["sanitizerResponseHash"]=hashlib.sha256(payload).hexdigest()
            self.assert_empty()
        self.case("actual-unmodified-dotnet-sanitizer-produces-three-variants",corpus)
        self.stop()
        self.case("supplied-restricted-systemd-template-starts-and-runs-sanitizer",self.systemd_template)
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
    parser.add_argument("--sanitizer-image")
    parser.add_argument("--sanitizer-config")
    parser.add_argument("--tested-profile", type=Path)
    parser.add_argument("--corpus-dotnet", type=Path)
    parser.add_argument("--corpus-assembly", type=Path)
    parser.add_argument("--corpus-report", type=Path)
    args = parser.parse_args()
    if any((args.sanitizer_image, args.sanitizer_config, args.tested_profile)):
        if (not isinstance(args.sanitizer_image, str)
                or re.fullmatch(r"ghcr\.io/bipoxdxd/duora-media-sanitizer@sha256:[0-9a-f]{64}", args.sanitizer_image) is None
                or not isinstance(args.sanitizer_config, str)
                or re.fullmatch(r"sha256:[0-9a-f]{64}", args.sanitizer_config) is None
                or args.tested_profile is None):
            parser.error("Release proof requires an immutable Duora image, config ID and profile output.")
        if (args.corpus_dotnet is None or args.corpus_assembly is None or args.corpus_report is None
                or not args.corpus_dotnet.is_absolute() or not args.corpus_dotnet.is_file()
                or not args.corpus_assembly.is_absolute() or not args.corpus_assembly.is_file()
                or not args.corpus_report.is_absolute() or args.corpus_report.exists()):
            parser.error("Release proof requires native corpus instrumentation and a fresh absolute report path.")
    elif any((args.corpus_dotnet, args.corpus_assembly, args.corpus_report)):
        parser.error("Native corpus qualification requires the pinned Release image.")
    proof=Proof(args)
    try:proof.run()
    finally:proof.dispose()


if __name__=="__main__": main()

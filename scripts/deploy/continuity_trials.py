"""Evidence retention and bounded synthetic CPU work; never a production entry point."""
import json
import math
from pathlib import Path
import shutil
import subprocess
import sys
import time


CPU_WORKER = r'''
import json, os, signal, sys, time
from pathlib import Path
directory = Path(sys.argv[1])
requested = False
def stop(_signal, _frame):
    global requested
    requested = True
signal.signal(signal.SIGTERM, stop)
nice = os.nice(10)
started = time.monotonic()
started_at = time.time()
cpu_started = time.process_time()
def write(name, data):
    temporary = directory / (name + '.tmp')
    temporary.write_text(json.dumps(data))
    temporary.replace(directory / name)
write('ready.json', {'phase': 'ready', 'nice': nice, 'startedAt': started_at})
batches = 0
while not requested and time.monotonic() < started + 300:
    sum(value * value for value in range(10000))
    batches += 1
finished = time.monotonic()
write('finished.json', {'phase': 'finished', 'nice': nice, 'startedAt': started_at,
    'finishedAt': time.time(), 'wallSeconds': finished - started,
    'cpuSeconds': time.process_time() - cpu_started, 'batches': batches,
    'reason': 'budget-expired' if finished >= started + 300 else 'requested'})
'''


def validate_trial(trial: int) -> None:
    if type(trial) is not int or trial not in (1, 2, 3):
        raise ValueError("Native qualification trial must be 1, 2 or 3")


def save_qualification(report: dict, bundle: Path, runner_temp: Path, *, trial: int) -> Path:
    """Keep trial 1 at the existing consumer path; never overwrite any independent proof."""
    validate_trial(trial)
    architecture = report["architecture"]
    if architecture not in ("arm64", "amd64"):
        raise ValueError("Unsupported native qualification architecture")
    base = runner_temp / "duora-executor-lab" / architecture
    output = base if trial == 1 else base / "trials" / str(trial)
    output.mkdir(parents=True)
    report["qualificationOrdinal"] = trial
    report["qualificationPath"] = str(output.relative_to(base) / "qualification.json")
    shutil.copytree(bundle, output / "bundle")
    (output / "qualification.json").write_text(json.dumps(report, indent=2) + "\n")
    for path in (output.parent, output, *output.rglob("*")):
        path.chmod(0o755 if path.is_dir() else 0o644)
    return output


class TrialCpuLoad:
    """One low-priority worker; its measured work must cover the qualified operations."""

    def __init__(self, directory: Path):
        directory.mkdir()
        self.directory = directory
        self.closed = False
        self.process = subprocess.Popen([sys.executable, "-c", CPU_WORKER, str(directory)],
                                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            deadline = time.monotonic() + 5
            while not (directory / "ready.json").is_file():
                if self.process.poll() is not None or time.monotonic() >= deadline:
                    raise RuntimeError("Controlled load did not start")
                time.sleep(0.01)
            ready = json.loads((directory / "ready.json").read_text())
            if ready.get("phase") != "ready" or type(ready.get("nice")) is not int or ready["nice"] < 10:
                raise RuntimeError("Controlled load startup proof is invalid")
        except BaseException:
            self.abort()
            raise

    def finish(self) -> dict:
        try:
            if self.process.poll() is not None:
                raise RuntimeError("Controlled load did not cover the whole qualification")
            self.process.terminate()
            if self.process.wait(timeout=3) != 0:
                raise RuntimeError("Controlled load exited unsuccessfully")
            metrics = json.loads((self.directory / "finished.json").read_text())
            fields = {"phase", "nice", "startedAt", "finishedAt", "wallSeconds", "cpuSeconds", "batches", "reason"}
            if set(metrics) != fields or metrics["phase"] != "finished" or metrics["reason"] != "requested":
                raise RuntimeError("Controlled load did not cover the whole qualification")
            for field in ("startedAt", "finishedAt", "wallSeconds", "cpuSeconds"):
                if type(metrics[field]) not in (int, float) or not math.isfinite(metrics[field]) or metrics[field] <= 0:
                    raise RuntimeError("Controlled load metrics are invalid")
            if (type(metrics["batches"]) is not int or metrics["batches"] <= 0
                    or type(metrics["nice"]) is not int or metrics["nice"] < 10
                    or metrics["finishedAt"] <= metrics["startedAt"]):
                raise RuntimeError("Controlled load metrics are invalid")
            return dict(metrics, workers=1, budgetSeconds=300)
        except subprocess.TimeoutExpired:
            raise RuntimeError("Controlled load shutdown exceeded its deadline") from None
        finally:
            self.abort()

    def abort(self) -> None:
        if self.closed:
            return
        self.closed = True
        if self.process.poll() is None:
            try:
                self.process.terminate()
            except ProcessLookupError:
                pass
            try:
                self.process.wait(timeout=3)
            except subprocess.TimeoutExpired:
                self.process.kill()
                self.process.wait(timeout=3)

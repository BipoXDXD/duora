"""Synthetic actor helper. No shell, credentials, or production data."""
import errno
import json
import os
import socket
import struct
import sys


def exact(stream, length):
    result = b""
    while len(result) < length:
        chunk = stream.recv(length - len(result))
        if not chunk:
            raise EOFError("incomplete reply")
        result += chunk
    return result


def main():
    command = json.load(sys.stdin)
    if command["mode"] == "attack":
        path = command["target"]
        try:
            operation = command["operation"]
            if operation == "unlink":
                os.unlink(path)
            elif operation == "rename":
                os.rename(path, path + ".replaced")
            elif operation == "truncate":
                os.truncate(path, 0)
            elif operation == "overwrite":
                with open(path, "r+b") as content:
                    content.write(b"corrupt")
            elif operation == "chmod":
                os.chmod(path, 0o777)
            elif operation == "create":
                with open(path, "xb") as content:
                    content.write(b"late")
            else:
                raise ValueError("unknown syscall")
            result = {"errno": None}
        except OSError as error:
            result = {"errno": errno.errorcode[error.errno]}
    elif command["mode"] == "identity":
        status = open("/proc/self/status", encoding="utf-8").read().splitlines()
        result = {"uid": os.getuid(), "gid": os.getgid(), "groups": os.getgroups(),
                  "cap_eff": next(line.split()[1] for line in status if line.startswith("CapEff:")),
                  "no_new_privs": next(line.split()[1] for line in status if line.startswith("NoNewPrivs:"))}
    else:
        request = command["request"]
        payload = json.dumps(request, separators=(",", ":")).encode()
        count = command.get("body_length", request.get("length", 0))
        body = (bytes([1, 2, 3]) * ((count + 2) // 3))[:count]
        with socket.socket(socket.AF_UNIX, socket.SOCK_STREAM) as stream:
            stream.settimeout(20)
            stream.connect(command["socket"])
            stream.sendall(struct.pack(">I", command.get("frame_length", len(payload))) + payload + body)
            stream.shutdown(socket.SHUT_WR)
            length = struct.unpack(">I", exact(stream, 4))[0]
            if length > 4096:
                raise ValueError("unbounded reply")
            result = json.loads(exact(stream, length))
    print(json.dumps(result))


if __name__ == "__main__":
    main()

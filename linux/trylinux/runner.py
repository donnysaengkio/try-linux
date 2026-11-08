"""Running systems: one QEMU process each, controlled over QMP (a UNIX socket in the system's folder).

Callbacks (`changed`, `ended`) run on a background thread; the UI moves them to its main loop."""
import json
import os
import socket
import subprocess
import threading
import time

from . import qemu


class QMP:
    """A QMP connection: commands, and the events QEMU sends (SHUTDOWN, STOP, RESUME...)."""

    def __init__(self, path, on_event=None, timeout=10):
        deadline = time.time() + timeout
        while True:
            try:
                self.sock = socket.socket(socket.AF_UNIX, socket.SOCK_STREAM)
                self.sock.connect(path)
                break
            except OSError:
                self.sock.close()
                if time.time() > deadline:
                    raise
                time.sleep(0.1)
        self.file = self.sock.makefile("rwb", buffering=0)
        self.on_event = on_event
        self.lock = threading.Lock()
        self.replies = []
        self.reply_ready = threading.Condition()
        self._read()   # the greeting
        threading.Thread(target=self._loop, daemon=True).start()
        self.command("qmp_capabilities")

    def _read(self):
        line = self.file.readline()
        if not line:
            raise EOFError()
        return json.loads(line)

    def _loop(self):
        try:
            while True:
                msg = self._read()
                if "event" in msg:
                    if self.on_event:
                        self.on_event(msg)
                else:
                    with self.reply_ready:
                        self.replies.append(msg)
                        self.reply_ready.notify_all()
        except (EOFError, OSError, ValueError):
            with self.reply_ready:
                self.replies.append({"error": {"desc": "QEMU closed the connection"}})
                self.reply_ready.notify_all()

    def command(self, name, timeout=10, **arguments):
        with self.lock:
            msg = {"execute": name}
            if arguments:
                msg["arguments"] = arguments
            with self.reply_ready:
                self.replies.clear()
            self.file.write((json.dumps(msg) + "\n").encode())
            with self.reply_ready:
                if not self.reply_ready.wait_for(lambda: self.replies, timeout):
                    raise TimeoutError(name)
                reply = self.replies.pop(0)
        if "error" in reply:
            raise RuntimeError(reply["error"].get("desc", "QMP error"))
        return reply.get("return")

    def close(self):
        try:
            self.sock.close()
        except OSError:
            pass


class RunningVM:
    def __init__(self, host, cfg, installer, changed=None, ended=None):
        self.host, self.cfg, self.installer = host, cfg, installer
        self.changed, self.ended = changed, ended
        self.paused = False
        self.reset = False            # the last shutdown was a restart
        self.err = ""
        self.qmp = None
        self.started = time.time()
        sock = os.path.join(cfg.dir, "qmp.sock")
        if os.path.exists(sock):
            os.remove(sock)
        qemu.prepare_firmware(host, cfg)
        cmd = [host.qemu] + qemu.args(host, cfg, installer, qmp=sock)
        log = open(os.path.join(cfg.dir, "qemu.log"), "w")
        log.write(" ".join(cmd) + "\n\n")
        log.flush()
        self.proc = subprocess.Popen(cmd, cwd=cfg.dir, stdout=log, stderr=subprocess.PIPE, text=True)
        self._log = log
        threading.Thread(target=self._collect, daemon=True).start()
        try:
            self.qmp = qemu_qmp = QMP(sock, self._event)
            qemu_qmp.command("query-status")
        except Exception:
            self.qmp = None
        threading.Thread(target=self._wait, daemon=True).start()

    def _collect(self):
        for line in self.proc.stderr:
            self.err += line
            self._log.write(line)
            self._log.flush()

    def _event(self, msg):
        ev = msg.get("event")
        if ev == "SHUTDOWN":
            self.reset = "reset" in msg.get("data", {}).get("reason", "")
        elif ev == "STOP":
            self.paused = True
        elif ev == "RESUME":
            self.paused = False
        if self.changed:
            self.changed(self)

    def _wait(self):
        code = self.proc.wait()
        time.sleep(0.2)   # the last stderr lines
        if self.qmp:
            self.qmp.close()
        self._log.close()
        if self.ended:
            self.ended(self, code)

    @property
    def alive(self):
        return self.proc.poll() is None

    def _cmd(self, name, **a):
        if not self.qmp:
            raise RuntimeError("Try Linux can't control this system (no QMP connection).")
        return self.qmp.command(name, **a)

    def shut_down(self):
        self._cmd("system_powerdown")

    def restart(self):
        self._cmd("system_reset")

    def pause(self):
        self._cmd("stop")

    def resume(self):
        self._cmd("cont")

    def force_stop(self):
        try:
            self._cmd("quit")
        except Exception:
            self.proc.kill()

    def change_cd(self, iso):
        if iso:
            self._cmd("blockdev-change-medium", id="cd", filename=iso, format="raw")
        else:
            self._cmd("eject", id="cd", force=True)


def friendly_error(err, cfg):
    """What went wrong, from QEMU's error output, in words a person can act on."""
    e = err.strip()
    low = e.lower()
    if "hostfwd" in low or "host forwarding" in low or "could not set up host forwarding" in low:
        return ("A port forward of %s is already in use on this PC. Change it in Settings > Devices > Network."
                % cfg.display_name)
    if "kvm" in low and ("permission denied" in low or "could not access" in low):
        return "Try Linux can't use KVM. Add yourself to the kvm group (sudo usermod -aG kvm $USER), then sign out and back in."
    if "egl" in low or "opengl" in low or "gl=on" in low or "virgl" in low:
        return ("The 3D graphics couldn't start on this PC. Turn off 3D acceleration in Settings > Hardware > Graphics.")
    if "audiodev" in low or "pipewire" in low or "pulseaudio" in low:
        return "The sound couldn't start. Turn off sound in Settings > Devices, or check that PipeWire or PulseAudio is running."
    lines = [l for l in e.splitlines() if l.strip()]
    return lines[-1] if lines else "QEMU stopped unexpectedly."

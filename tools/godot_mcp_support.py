"""Bounded MCP stdio driver for developer layouts and self-contained artifacts."""
import json, math, os, queue, subprocess, threading, time
from pathlib import Path

class Mcp:
    def __init__(self, command, endpoint, log, timeout=60, env=None):
        if not math.isfinite(timeout) or timeout <= 0: raise ValueError('MCP deadline must be finite and positive')
        self.id = 0
        self.timeout = timeout
        self.lines = queue.Queue()
        self.log_path = Path(log)
        self.log = self.log_path.open('w')
        environment = dict(os.environ, GAMEDIRECTOR_ENDPOINT=endpoint)
        # Tests may supply a genuine hashed developer distribution, never bypass integrity.
        environment.pop('GAMEDIRECTOR_DISTRIBUTION_ROOT', None)
        environment.update(env or {})
        # Own process group (POSIX session / Windows group) so close() reaches gd's MCP companion too.
        group = dict(creationflags=subprocess.CREATE_NEW_PROCESS_GROUP) if os.name == 'nt' else dict(start_new_session=True)
        self.p = subprocess.Popen(command, stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=self.log,
                                  text=True, env=environment, **group)
        self.reader = threading.Thread(target=self._read, daemon=True)
        self.reader.start()
        try:
            self.request('initialize', dict(protocolVersion='2024-11-05', capabilities={}, clientInfo=dict(name='godot-acceptance', version='1')))
            self.send(dict(jsonrpc='2.0', method='notifications/initialized'))
        except BaseException:
            self.close(cancel=True)
            raise
    def _read(self):
        for line in self.p.stdout: self.lines.put(line)
        self.lines.put(None)
    def send(self, message):
        try:
            self.p.stdin.write(json.dumps(message) + '\n')
            self.p.stdin.flush()
        except BrokenPipeError:
            raise RuntimeError('MCP closed stdin: ' + self.log_path.read_text()[-2000:]) from None
    def request(self, method, params):
        self.id += 1
        self.send(dict(jsonrpc='2.0', id=self.id, method=method, params=params))
        deadline = time.monotonic() + self.timeout
        while True:
            try: line = self.lines.get(timeout=max(0, deadline - time.monotonic()))
            except queue.Empty: raise TimeoutError(f'MCP {method} exceeded {self.timeout}s') from None
            if line is None: raise RuntimeError('MCP closed stdout: ' + self.log_path.read_text()[-2000:])
            message = json.loads(line)
            if message.get('id') == self.id:
                if 'error' in message: raise RuntimeError(str(message))
                return message['result']
    def call(self, name, arguments): return self.request('tools/call', dict(name=name, arguments=arguments))
    def close(self, cancel=False):
        try: self.p.stdin.close()
        except BrokenPipeError: pass
        try: self.p.wait(timeout=0 if cancel else 10)
        except subprocess.TimeoutExpired:
            # Own process group includes gd's MCP companion, not another agent's server.
            self._terminate(force=False)
            try: self.p.wait(timeout=1 if cancel else 10)
            except subprocess.TimeoutExpired: self._terminate(force=True); self.p.wait()
        self.reader.join(timeout=1)
        self.p.stdout.close()
        self.log.close()
    def _terminate(self, force):
        if os.name == 'nt':
            # taskkill /T ends the whole tree started for this client.
            subprocess.run(['taskkill', '/PID', str(self.p.pid), '/T'] + (['/F'] if force else []),
                           stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            if force and self.p.poll() is None: self.p.kill()
            return
        import signal
        try: os.killpg(self.p.pid, signal.SIGKILL if force else signal.SIGTERM)
        except ProcessLookupError: pass
    def __enter__(self): return self
    def __exit__(self, *_): self.close()

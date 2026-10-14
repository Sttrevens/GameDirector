#!/usr/bin/env python3
"""Run the command in a copied MCP config with no SDK or endpoint environment."""
import argparse
import json
import os
import queue
import signal
import subprocess
import threading
import time
from pathlib import Path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--config', required=True, type=Path)
    parser.add_argument('--out', required=True, type=Path)
    args = parser.parse_args()
    args.out.mkdir(parents=True, exist_ok=False)
    config = json.loads(args.config.read_text())['mcpServers']['gamedirector']
    assert set(config) == {'command', 'args'} and config['args'] == ['mcp']
    assert Path(config['command']).is_absolute()
    env = dict(os.environ, PATH='', DOTNET_MULTILEVEL_LOOKUP='0')
    for name in ('GAMEDIRECTOR_ENDPOINT', 'GAMEDIRECTOR_DISTRIBUTION_ROOT',
                 'GAMEDIRECTOR_WORKBENCH', 'GAMEDIRECTOR_WORKBENCH_TOKEN'):
        env.pop(name, None)
    with (args.out / 'mcp.log').open('w') as log:
        process = subprocess.Popen([config['command'], *config['args']], env=env,
            stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=log,
            text=True, bufsize=1, start_new_session=os.name != 'nt')
        messages = queue.Queue()
        def read():
            for line in process.stdout:
                try: messages.put(json.loads(line))
                except ValueError: messages.put({'invalidStdout': line})
            messages.put({'eof': True})
        threading.Thread(target=read, daemon=True).start()
        def send(method, params, request_id=None):
            message = dict(jsonrpc='2.0', method=method, params=params)
            if request_id is not None: message['id'] = request_id
            process.stdin.write(json.dumps(message) + '\n'); process.stdin.flush()
            if request_id is None: return
            deadline = time.monotonic() + 60
            while time.monotonic() < deadline:
                result = messages.get(timeout=max(.1, deadline - time.monotonic()))
                assert not result.get('invalidStdout') and not result.get('eof'), result
                if result.get('id') == request_id:
                    assert 'error' not in result, result
                    return result['result']
            raise TimeoutError(method)
        try:
            initialized = send('initialize', dict(protocolVersion='2024-11-05', capabilities={},
                clientInfo=dict(name='copied-config-acceptance', version='1')), 1)
            assert initialized['serverInfo']
            send('notifications/initialized', {})
            names = [tool['name'] for tool in send('tools/list', {}, 2)['tools']]
            assert len(names) == len(set(names)) and {'gd_get_manifest', 'gd_take'} <= set(names)
            process.stdin.close()
            assert process.wait(timeout=30) == 0
            result = dict(passed=True, command=config['command'], tools=len(names),
                          sdkOnPath=False, endpointEnv=False, stdioClean=True, exit=process.returncode)
            (args.out / 'result.json').write_text(json.dumps(result, indent=2) + '\n')
            print(json.dumps(result))
        finally:
            if process.poll() is None:
                if os.name == 'nt':
                    subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                else: os.killpg(process.pid, signal.SIGTERM)
                try: process.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    if os.name != 'nt': os.killpg(process.pid, signal.SIGKILL)
                    else: process.kill()
                    process.wait()

if __name__ == '__main__':
    main()

"""MCP tool failures must reach the model as isError results with actionable text.

Starts the built MCP server (Debug build) on stdio with GAMEDIRECTOR_WORKBENCH
pointing at an unused loopback port, then asserts two failure paths: an
unreachable Workbench names the endpoint, and a director tool with a missing
file names the file — neither may degrade into the SDK's generic
"An error occurred invoking" message.
"""
import json
import os
import socket
import subprocess
import threading
import tempfile
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path

repo = Path(__file__).resolve().parents[1]
server = repo / 'src/GameDirector.Mcp/bin/Debug/net8.0/gamedirector-mcp.dll'
assert server.is_file(), f'MCP server not built: {server}'

with socket.socket() as probe:
    probe.bind(('127.0.0.1', 0))
    port = probe.getsockname()[1]
endpoint = f'http://127.0.0.1:{port}'

env = dict(os.environ, GAMEDIRECTOR_WORKBENCH=endpoint)
env.pop('GAMEDIRECTOR_WORKBENCH_TOKEN', None)
proc = subprocess.Popen(['dotnet', str(server)], stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                        text=True, env=env)
next_id = 0
httpd = None

def send(message):
    proc.stdin.write(json.dumps(message) + '\n')
    proc.stdin.flush()

def request(method, params):
    global next_id
    next_id += 1
    send(dict(jsonrpc='2.0', id=next_id, method=method, params=params))
    while True:
        line = proc.stdout.readline()
        assert line, 'MCP server closed stdout'
        message = json.loads(line)
        if message.get('id') == next_id:
            return message

try:
    reply = request('initialize', dict(protocolVersion='2024-11-05', capabilities={},
                                       clientInfo=dict(name='mcp-errors-test', version='0')))
    assert 'result' in reply, reply
    send(dict(jsonrpc='2.0', method='notifications/initialized'))

    tools = request('tools/list', {})['result']['tools']
    names = {t['name'] for t in tools}
    assert 'gd_studio_state' in names and 'gd_validate_timeline' in names, names

    state = request('tools/call', dict(name='gd_studio_state', arguments={}))['result']
    assert state['isError'] is True, state
    text = state['content'][0]['text']
    assert f'127.0.0.1:{port}' in text, text
    assert 'An error occurred invoking' not in text, text

    missing = request('tools/call', dict(name='gd_validate_timeline',
                                         arguments=dict(timelinePath=str(repo / 'no-such-timeline.json'),
                                                        manifestPath=str(repo / 'no-such-manifest.json'))))['result']
    assert missing['isError'] is True, missing
    text = missing['content'][0]['text']
    assert 'no-such-timeline.json' in text, text
    assert 'An error occurred invoking' not in text, text

    payload, status, authenticated = {}, 503, False
    token = 'a' * 64
    class Failures(BaseHTTPRequestHandler):
        def log_message(self, *_):
            pass
        def do_GET(self):
            answer, code = payload, status
            if authenticated:
                if self.path == '/api/health':
                    answer, code = dict(product='GameDirector', protocolVersion=1, instanceId='owned-fixture'), 200
                elif self.headers.get('Authorization') == 'Bearer ' + token:
                    answer, code = dict(projects=[], jobs=[]), 200
                else:
                    answer, code = dict(error='credentials missing', code='unauthorized', retryable=False), 401
            body = json.dumps(answer).encode()
            self.send_response(code)
            self.send_header('Content-Type', 'application/json')
            self.send_header('Content-Length', str(len(body)))
            self.end_headers()
            self.wfile.write(body)
    httpd = ThreadingHTTPServer(('127.0.0.1', port), Failures)
    threading.Thread(target=httpd.serve_forever, daemon=True).start()
    for payload in [dict(error='engine unavailable', code='engine_unavailable', retryable=True),
                    dict(error='legacy message'), dict(error='code only', code='future_code'),
                    dict(error='retry only', retryable=False)]:
        result = request('tools/call', dict(name='gd_studio_state', arguments={}))['result']
        assert result['isError'] is True, result
        text = result['content'][0]['text']
        assert 'HTTP 503' in text and payload['error'] in text, text
        assert ('code: ' + payload['code'] in text) if 'code' in payload else 'code:' not in text, text
        assert ('retryable: ' + str(payload['retryable']).lower() in text) if 'retryable' in payload else 'retryable:' not in text, text
    status, payload = 401, dict(error='credentials missing', code='unauthorized', retryable=False)
    result = request('tools/call', dict(name='gd_studio_state', arguments={}))['result']
    assert result['isError'] is True, result
    text = result['content'][0]['text']
    assert 'HTTP 401' in text and 'GAMEDIRECTOR_WORKBENCH_TOKEN' in text and '.service.json' in text, text
    assert 'An error occurred invoking' not in text, text
    proc.kill(); proc.wait(timeout=10)
    authenticated = True
    with tempfile.TemporaryDirectory(prefix='gd-mcp-auth-') as home:
        descriptor = Path(home) / 'Workbench/.service.json'
        descriptor.parent.mkdir()
        descriptor.write_text(json.dumps(dict(address=endpoint, instanceId='owned-fixture', token=token)))
        descriptor.chmod(0o600)
        for mode in ('paired-env', 'verified-descriptor'):
            client_env = dict(os.environ, GAMEDIRECTOR_HOME=home)
            client_env.pop('GAMEDIRECTOR_WORKBENCH', None); client_env.pop('GAMEDIRECTOR_WORKBENCH_TOKEN', None)
            if mode == 'paired-env':
                client_env.update(GAMEDIRECTOR_WORKBENCH=endpoint, GAMEDIRECTOR_WORKBENCH_TOKEN=token)
            proc = subprocess.Popen(['dotnet', str(server)], stdin=subprocess.PIPE, stdout=subprocess.PIPE, text=True, env=client_env)
            assert 'result' in request('initialize', dict(protocolVersion='2024-11-05', capabilities={}, clientInfo=dict(name='mcp-auth', version='0')))
            send(dict(jsonrpc='2.0', method='notifications/initialized'))
            result = request('tools/call', dict(name='gd_studio_state', arguments={}))['result']
            assert not result.get('isError'), mode + ': authentication failed'
            assert json.loads(result['content'][0]['text']) == dict(projects=[], jobs=[])
            assert token not in json.dumps(result)
            proc.kill(); proc.wait(timeout=10)
finally:
    proc.kill()
    proc.wait(timeout=10)
    if httpd:
        httpd.shutdown()
        httpd.server_close()
print(f'PASS: 7 MCP error cases + 2 authenticated clients: unreachable ({endpoint}), missing file, typed failure, legacy body, code-only, retryable-only, missing-token 401; paired env and verified descriptor')

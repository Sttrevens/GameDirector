"""Exercise a published native CLI, Workbench and MCP with no SDK in PATH."""
import argparse
import json
import os
import queue
import signal
import subprocess
import threading
import time
import urllib.request
from pathlib import Path


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--distribution', required=True)
    p.add_argument('--out', required=True)
    a = p.parse_args()
    distribution = Path(a.distribution).resolve()
    root = Path(a.out).resolve()
    root.mkdir(parents=True, exist_ok=False)
    layout = json.loads((distribution / 'distribution.json').read_text(encoding='utf-8'))
    app = layout['apps']['cli']
    cli = distribution / app['folder'] / (app['executable'] + ('.exe' if os.name == 'nt' else ''))
    env = dict(os.environ, PATH='', DOTNET_ROOT=str(root / 'no-runtime'), DOTNET_MULTILEVEL_LOOKUP='0')
    # A native self-contained executable must not require a globally installed SDK.
    help_result = subprocess.run([str(cli), '--help'], env=env, capture_output=True, text=True, encoding='utf-8', timeout=30)
    assert help_result.returncode == 0, help_result.stderr
    guide = subprocess.run([str(cli), 'guide'], env=env, capture_output=True, timeout=30)
    assert guide.returncode == 0 and len(guide.stdout) > 100, guide.stderr
    project = root / 'unity-project'
    (project / 'Assets').mkdir(parents=True)
    (project / 'ProjectSettings').mkdir()

    def install(*extra):
        run = subprocess.run([str(cli), 'unity', 'install', '--project', str(project), *extra],
                             env=env, capture_output=True, text=True, encoding='utf-8', timeout=120)
        assert run.returncode == 0, run.stderr
        return json.loads(run.stdout)

    plan = install()
    assert plan['mode'] == 'plan' and plan['writes'] > 0 and not (project / 'Packages').exists()
    installed = install('--apply')
    assert installed['mode'] == 'installed' and installed['writes'] > 0
    installed_files = [path for path in (project / 'Packages').rglob('*') if path.is_file()]
    stamps = {str(path): path.stat().st_mtime_ns for path in installed_files}
    assert install('--apply')['writes'] == 0
    assert stamps == {str(path): path.stat().st_mtime_ns for path in installed_files}
    urllib.request.install_opener(urllib.request.build_opener(urllib.request.ProxyHandler({})))
    log = (root / 'workbench.log').open('w', encoding='utf-8')
    service = subprocess.Popen([str(cli), 'workbench', '--port', '0', '--data', str(root / 'store')],
                               env=env, stdout=log, stderr=log, start_new_session=os.name != 'nt')
    mcp = None
    try:
        descriptor = root / 'store/.service.json'
        for _ in range(300):
            if descriptor.exists():
                break
            assert service.poll() is None, 'Packaged Workbench exited; see workbench.log'
            time.sleep(.1)
        data = json.loads(descriptor.read_text(encoding='utf-8'))
        endpoint = data['address']
        with urllib.request.urlopen(endpoint + '/api/health', timeout=10) as response:
            assert json.load(response)['product'] == 'GameDirector'
        env['GAMEDIRECTOR_WORKBENCH'] = endpoint
        mcp = subprocess.Popen([str(cli), 'mcp'], env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                               stderr=log, text=True, encoding='utf-8', bufsize=1, start_new_session=os.name != 'nt')
        messages = queue.Queue()

        def read_lines():
            for line in mcp.stdout:
                try:
                    messages.put(json.loads(line))
                except ValueError:
                    messages.put({'invalidStdout': line})
            messages.put({'eof': True})

        threading.Thread(target=read_lines, daemon=True).start()

        def send(method, params, request_id=None):
            request = dict(jsonrpc='2.0', method=method, params=params)
            if request_id is not None:
                request['id'] = request_id
            mcp.stdin.write(json.dumps(request) + '\n')
            mcp.stdin.flush()
            if request_id is None:
                return
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                reply = messages.get(timeout=max(.1, deadline - time.monotonic()))
                assert not reply.get('invalidStdout') and not reply.get('eof'), reply
                if reply.get('id') == request_id:
                    assert 'error' not in reply, reply
                    return reply['result']
            raise TimeoutError(method)

        initialized = send('initialize', dict(protocolVersion='2024-11-05', capabilities={},
                           clientInfo=dict(name='native-package-smoke', version='1')), 1)
        assert initialized['serverInfo']
        send('notifications/initialized', {})
        tools = send('tools/list', {}, 2)['tools']
        names = [tool['name'] for tool in tools]
        assert 'gd_studio_state' in names and 'gd_studio_save_document' in names, names
        result = send('tools/call', dict(name='gd_studio_state', arguments={}), 3)
        assert not result.get('isError'), result
        state = json.loads(next(block['text'] for block in result['content'] if block['type'] == 'text'))
        assert state['projects'] == [], state
        mcp.stdin.close()
        assert mcp.wait(timeout=20) == 0, 'MCP wrapper did not propagate normal stdio shutdown'
        evidence = dict(passed=True, distribution=str(distribution), sdkOnPath=False,
                        nativeHelp=True, embeddedGuide=True, nativeWorkbench=True, nativeMcp=True,
                        stdioClean=True, sharedStore=True, unityDryRun=True,
                        unityInstall=True, repeatedInstallZeroWrites=True, tools=names)
        (root / 'result.json').write_text(json.dumps(evidence, indent=2) + '\n', encoding='utf-8')
        print(json.dumps(evidence))
    finally:
        for process in (mcp, service):
            if process is not None and process.poll() is None:
                # Kill the wrapper's owned process tree on Windows, where
                # terminating a parent alone leaves the companion running.
                if os.name == 'nt':
                    subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
                else:
                    os.killpg(process.pid, signal.SIGTERM)
                try:
                    process.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait()
        log.close()


if __name__ == '__main__':
    main()

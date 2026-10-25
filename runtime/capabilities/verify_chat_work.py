"""Isolated Chat Work backend contract regression; no external model calls.

Uses the real ledger and artifact collection with an in-process deterministic
Pi RPC fixture. It does not claim native Pi inference or model quality.
"""
import asyncio
import contextlib
import hashlib
import io
import json
from pathlib import Path
import sys
import time
import types
import uuid
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parent))
from bridge import Host, ROOT
from pi_provider import PiProvider


class RpcFixture:
    def __init__(self, workspace, scenario):
        self.workspace, self.scenario = Path(workspace), scenario
        self.process = types.SimpleNamespace(returncode=0)
        self.events = asyncio.Queue()
        self.confirmed = []
        self.closed = False

    async def request(self, command, **args):
        assert command == 'prompt'
        self.task = args['message']
        if self.scenario == 'hold':
            return {}
        if self.scenario == 'browser':
            title, message = '读取外部网址（将发送此 URL）', json.dumps({'url': 'https://example.test/page'})
        else:
            title = '写入工作区文件'
            message = json.dumps({'tool': 'edit' if self.scenario == 'edit' else 'write',
                'path': str(self.workspace / 'result.txt'), 'bytes': 42})
        await self.events.put({'type': 'extension_ui_request', 'method': 'confirm', 'id': 'fixture-confirm',
                               'title': title, 'message': message})
        return {}

    async def next_event(self):
        return await self.events.get()

    async def send(self, args):
        self.confirmed.append(args.get('confirmed'))
        if args.get('confirmed') and self.scenario != 'browser':
            (self.workspace / 'result.txt').write_text('fixture result ' + self.scenario, encoding='utf8')
        await self.events.put({'type': 'message_end', 'message': {'role': 'assistant', 'stopReason': 'stop',
            'content': [{'type': 'text', 'text': '真实夹具完成；未请求外部模型。'}]}})
        await self.events.put({'type': 'agent_settled'})

    async def close(self):
        self.closed = True

    async def detach(self):
        self.closed = True


def install_fixture(host):
    host.pi.description = lambda: {'installed': True, 'configured': True}
    host.pi.config = {'baseUrl': 'https://fixture.invalid', 'model': 'fixture', 'key': 'fixture-secret-value'}
    clients, events = [], []
    host.fixture_scenario = 'write'
    host.fixture_manual_allow = False
    host.codex_probe = {'id': 'codex', 'installed': False, 'authenticated': False}
    host.codex_probe_at = time.monotonic()
    original_emit = host.emit

    def emit(event, **data):
        original_emit(event, **data)
        events.append({'event': event, **host.events.get(data.get('workId'), [{}])[-1]})
        if event == 'permission.requested':
            asyncio.create_task(host.invoke('permission_response', {'requestId': data['requestId'],
                                                                       'allow': host.fixture_manual_allow}))

    async def start(workspace, **kwargs):
        client = RpcFixture(workspace, host.fixture_scenario)
        clients.append(client)
        return client, {'tools': ['read', 'ls', 'write', 'edit']}

    host.emit, host.pi.start = emit, start
    return clients, events


async def settle(host):
    async with asyncio.timeout(15):
        while host.jobs:
            await asyncio.sleep(.01)
    await asyncio.sleep(0)


async def rejected(host, op, args):
    try:
        await host.invoke(op, args)
    except ValueError:
        return
    raise AssertionError('Invalid request was accepted: ' + op)


async def verify_configuration(host):
    """Exercise real validation/start bootstrap with transport replaced only."""
    saved_description, saved_config = host.pi.description, host.pi.config
    bootstraps = []

    class BootstrapCapture:
        def __init__(self, *args, **kwargs):
            pass

        async def start(self):
            pass

        async def send(self, message):
            bootstraps.append(message)

        async def next_event(self):
            return {'type': 'atri_ready'}

        async def close(self):
            pass

    a = {'baseUrl': 'https://provider-a.invalid/v1', 'model': 'model-a', 'key': 'synthetic-a-secret'}
    c = {'baseUrl': 'https://provider-c.invalid/v1/chat/completions', 'model': 'c' * 200, 'key': 'synthetic-c-secret'}
    workspace = host.root / 'data/workspaces/configuration-fixture'
    workspace.mkdir(parents=True)
    host.pi.description = lambda: {'installed': True, 'configured': bool(host.pi.config)}
    try:
        with patch('pi_provider.PiRpcClient', BootstrapCapture):
            host.pi.configure(a)
            client, _ = await PiProvider.start(host.pi, workspace)
            await client.close()
            assert bootstraps[-1]['baseUrl'] == a['baseUrl'] and bootstraps[-1]['key'] == a['key']
            invalid = [
                {**a, 'baseUrl': 'https://provider-b.invalid/v1?api-version=2026-01-01'},
                {**a, 'baseUrl': 'https://provider-b.invalid/v1#fragment'},
                {**a, 'baseUrl': 'https://'},
                {**a, 'baseUrl': 'https://provider-b.invalid:invalid/v1'},
                {**a, 'baseUrl': 'https://provider-b.invalid:99999/v1'},
                {**a, 'baseUrl': 'https://user:password@provider-b.invalid/v1'},
                {**a, 'baseUrl': 'http://provider-b.invalid/v1'},
                {**a, 'model': 'b' * 201},
                {**a, 'key': 'synthetic\ninvalid'},
                {**a, 'key': 'b' * 8193},
            ]
            for index, replacement in enumerate(invalid):
                host.pi.configure(a)
                try:
                    host.pi.configure(replacement)
                except ValueError:
                    pass
                else:
                    raise AssertionError('Invalid replacement was accepted')
                assert host.pi.config is None
                snapshot = await host.invoke('chat_work_snapshot', {})
                assert not next(x for x in snapshot['providers'] if x['id'] == 'pi')['ready']
                before = len(bootstraps), len(host.store.list_work_items(limit=100))
                try:
                    await PiProvider.start(host.pi, workspace)
                except ValueError:
                    pass
                else:
                    raise AssertionError('Rejected configuration started a provider')
                await rejected(host, 'chat_work_dispatch', {'intent': 'create', 'instruction': '离线配置失败夹具。',
                    'requestId': 'chat-invalid-config-' + str(index)})
                assert before == (len(bootstraps), len(host.store.list_work_items(limit=100)))
            for empty in ({**a, 'baseUrl': ''}, {**a, 'model': ''}):
                host.pi.configure(a)
                assert not host.pi.configure(empty)['configured'] and host.pi.config is None
            host.pi.configure(c)
            assert next(x for x in (await host.chat_snapshot())['providers'] if x['id'] == 'pi')['ready']
            client, _ = await PiProvider.start(host.pi, workspace)
            await client.close()
            assert len(bootstraps) == 2 and bootstraps[-1]['baseUrl'] == 'https://provider-c.invalid/v1'
            assert bootstraps[-1]['model'] == c['model'] and bootstraps[-1]['key'] == c['key']
            for base in ('http://localhost:1234/v1', 'http://127.0.0.1:1234/v1', 'http://[::1]:1234/v1'):
                assert host.pi.configure({**c, 'baseUrl': base})['configured']
    finally:
        host.pi.description, host.pi.config = saved_description, saved_config


async def verify():
    run_root = ROOT / 'data/verification/chat-work' / uuid.uuid4().hex
    host = Host(root=run_root, state_dir=run_root / 'data/capabilities')
    # The only persona source is the product's registered asset, read unchanged.
    # The isolated root receives a test copy; no production assets are edited.
    persona_bytes = (ROOT / 'assets/derived/persona.json').read_bytes()
    persona_prompt = json.loads(persona_bytes)['systemPrompt']
    persona_fixture = run_root / 'assets/derived/persona.json'
    persona_fixture.parent.mkdir(parents=True)
    persona_fixture.write_bytes(persona_bytes)
    clients, events = install_fixture(host)
    checks = []
    try:
        await verify_configuration(host)
        checks.append('real Pi A-to-invalid-B replacement revokes old endpoint/model/key and readiness without dispatch or bootstrap; valid C and supported local HTTP recover')
        empty = await host.invoke('chat_work_snapshot', {})
        assert not empty['tasks'] and len(empty['providers']) == 3 and not clients
        missing = await host.invoke('chat_work_status', {'requestId': 'missing-request'})
        assert missing['status'] == 'not_found' and missing['stopConfirmed']
        checks.append('snapshot and request-id status lookup do not create Work or start a provider')

        class IdentityProcess:
            def __init__(self, *, blocked=False):
                self.returncode, self.blocked, self.killed = None, blocked, False

            async def communicate(self):
                if self.blocked:
                    await asyncio.Event().wait()
                self.returncode = 0
                return b'identity fixture output is never published', b''

            def kill(self):
                self.killed, self.returncode = True, -9

            async def wait(self):
                return self.returncode

        identity_calls, identity_processes = [], []

        async def identity_spawn(executable, *arguments, **kwargs):
            identity_calls.append(arguments)
            process = IdentityProcess(blocked=host.fixture_scenario == 'identity-timeout')
            identity_processes.append(process)
            return process

        with patch('bridge.shutil.which', return_value='codex-fixture.exe'), patch('bridge.asyncio.create_subprocess_exec', identity_spawn):
            host.codex_probe = None
            identity_snapshot = await host.invoke('chat_work_snapshot', {})
            assert next(x for x in identity_snapshot['providers'] if x['id'] == 'codex')['ready']
            await host.invoke('chat_work_snapshot', {})
            assert identity_calls == [('login', 'status')]
            host.codex_probe_at -= 601
            await host.invoke('chat_work_snapshot', {})
            assert identity_calls == [('login', 'status'), ('login', 'status')]
            host.fixture_scenario = 'identity-timeout'
            failure_identity = await host.codex_identity(refresh=True, timeout=.02)
            assert not failure_identity['authenticated'] and failure_identity['probeError'] and identity_processes[-1].killed
            failed_snapshot = await host.invoke('chat_work_snapshot', {})
            assert not next(x for x in failed_snapshot['providers'] if x['id'] == 'codex')['ready'] and len(identity_calls) == 3
            assert not clients and 'identity fixture output' not in json.dumps(failed_snapshot)
        host.fixture_scenario = 'write'
        checks.append('snapshot probes only local Codex login identity, caches success/failure ten minutes and terminates timed-out probes without model calls')

        before = len(host.store.list_work_items(limit=100))
        persona_fixture.write_text('{}', encoding='utf8')
        await rejected(host, 'chat_work_dispatch', {'intent': 'create', 'instruction': '缺人格夹具。', 'requestId': 'missing-persona-001'})
        persona_fixture.write_text(json.dumps({'systemPrompt': 'x' * 16001}), encoding='utf8')
        await rejected(host, 'chat_work_dispatch', {'intent': 'create', 'instruction': '人格过长夹具。', 'requestId': 'oversized-persona-001'})
        assert len(host.store.list_work_items(limit=100)) == before and not clients
        persona_fixture.write_bytes(persona_bytes)
        checks.append('missing or oversized registered persona is rejected before creating Work or starting a provider')

        create = {'intent': 'create', 'instruction': '写一段中文介绍并保存文件。', 'summary': '聊天新任务', 'requestId': 'chat-create-001'}
        entered, release = asyncio.Event(), asyncio.Event()
        original_finalize = host.registry.finalize_attempt

        async def held_finalize(attempt, item):
            entered.set()
            await release.wait()
            return await original_finalize(attempt, item)

        host.registry.finalize_attempt = held_finalize
        first = await host.invoke('chat_work_dispatch', create)
        await asyncio.wait_for(entered.wait(), 10)
        running = await host.invoke('chat_work_status', first)
        assert running['status'] == 'running' and running['busy'] and not running['stopConfirmed']
        release.set()
        await settle(host)
        host.registry.finalize_attempt = original_finalize
        done = await host.invoke('chat_work_status', first)
        item = host.item(first['workId'])
        attempt = host.store.get_attempt(first['attemptId'])
        assert done['status'] == 'succeeded' and done['artifacts'] and done['stopConfirmed']
        assert Path(item.workspace_path).parent == run_root / 'data/workspaces'
        assert Path(item.workspace_path).name.startswith('chat-')
        assert '亚托莉' in attempt.task and attempt.metadata['autoWriteScope'] == item.workspace_path
        assert persona_prompt in attempt.task and 'only for prose and creative' in attempt.task
        assert 'exact technical meaning take priority' in attempt.task and 'Never invent a prior relationship' in attempt.task
        assert any(x.capability == 'provider.pi.tool' and x.status == 'allowed'
                   for x in host.store.list_permission_requests(item.work_item_id))
        assert not any(x['event'] == 'permission.requested' for x in events)
        assert all(x.get('chatRequestId') == create['requestId'] and x.get('attemptId') == first['attemptId'] for x in events)
        checks.append('chat creates a dedicated workspace, grants scoped writes with ledger evidence and identifies early events')
        checks.append('chat execution uses the actual registered persona with explicit user-format, technical accuracy and authority boundaries')
        checks.append('terminal success is visible only after real artifact collection finishes')

        replay = await host.invoke('chat_work_dispatch', create)
        recovered = await host.invoke('chat_work_status', {'requestId': create['requestId']})
        assert replay['replayed'] and replay['attemptId'] == recovered['attemptId'] == first['attemptId']
        assert len(clients) == 1 and len(host.store.list_work_items(limit=100)) == 1
        await rejected(host, 'chat_work_dispatch', {**create, 'instruction': 'different'})
        checks.append('duplicate dispatch and lost-receipt request lookup recover the original Attempt without resubmission')

        host.fixture_scenario = 'edit'
        continued = await host.invoke('chat_work_dispatch', {'intent': 'continue', 'workId': first['workId'],
            'instruction': '修改上一份介绍。', 'requestId': 'chat-continue-001'})
        await settle(host)
        continuation = host.store.get_attempt(continued['attemptId'])
        assert continued['workId'] == first['workId'] and continued['attemptId'] != first['attemptId']
        assert continuation.provider == 'pi' and 'Previous provider result' in continuation.task
        assert (Path(item.workspace_path) / 'result.txt').read_text() == 'fixture result edit'
        await rejected(host, 'chat_work_status', {'workId': first['workId'], 'attemptId': 'unknown-attempt'})
        checks.append('continue preserves Work identity and provider while creating a new exact Attempt with prior result context')

        # The Work panel may start a new Attempt before the chat's next poll.
        # Old status, request recovery and late cancellation still own only the
        # original Attempt, while panel Work-level cancel remains supported.
        host.fixture_scenario = 'hold'
        panel_live = await host.invoke('run_pi', {'workId': first['workId'], 'approved': True, 'allowWrite': True})
        await asyncio.sleep(.05)
        old_status = await host.invoke('chat_work_status', first)
        old_lookup = await host.invoke('chat_work_status', {'requestId': create['requestId']})
        old_replay = await host.invoke('chat_work_dispatch', create)
        assert all(x['status'] == 'succeeded' and not x['busy'] and x['stopConfirmed'] for x in (old_status, old_lookup, old_replay))
        current_status = await host.invoke('chat_work_status', panel_live)
        assert current_status['status'] == 'running' and current_status['busy'] and not current_status['stopConfirmed']
        old_cancel = await host.invoke('cancel', {'workId': first['workId'], 'attemptId': first['attemptId']})
        assert not old_cancel['cancelRequested'] and host.store.get_attempt(panel_live['attemptId']).execution_status == 'running'
        await rejected(host, 'cancel', {'workId': first['workId'], 'attemptId': 'unknown-attempt'})
        await rejected(host, 'cancel', {'workId': first['workId'], 'attemptId': ''})
        await rejected(host, 'cancel', {'workId': first['workId'], 'attemptId': None})
        exact_cancel = await host.invoke('cancel', {'workId': first['workId'], 'attemptId': panel_live['attemptId']})
        assert exact_cancel['cancelRequested']
        await settle(host)
        assert (await host.invoke('chat_work_status', panel_live))['status'] == 'cancelled'
        another_panel = await host.invoke('run_pi', {'workId': first['workId'], 'approved': True})
        await asyncio.sleep(.05)
        assert (await host.invoke('cancel', {'workId': first['workId']}))['cancelRequested']
        await settle(host)
        assert (await host.invoke('chat_work_status', another_panel))['status'] == 'cancelled'
        checks.append('same-Work old Attempt status/recovery/replay remain stopped while new panel Attempt runs; late old cancel cannot stop new, exact current and panel Work-level cancel do')

        host.fixture_scenario = 'hold'
        hold = await host.invoke('chat_work_dispatch', {'intent': 'continue', 'workId': first['workId'],
            'instruction': '长任务夹具。', 'requestId': 'chat-hold-001'})
        await asyncio.sleep(.05)
        count = len(clients)
        report = await host.invoke('chat_work_dispatch', {'intent': 'report', 'workId': first['workId'], 'requestId': 'chat-report-001'})
        assert report['attemptId'] == hold['attemptId'] and report['status'] == 'running' and len(clients) == count
        cancel = {'intent': 'cancel', 'workId': first['workId'], 'requestId': 'chat-cancel-001'}
        await host.invoke('chat_work_dispatch', cancel)
        await settle(host)
        stopped = await host.invoke('chat_work_status', hold)
        assert stopped['status'] == 'cancelled' and stopped['stopConfirmed'] and clients[-1].closed
        next_hold = await host.invoke('chat_work_dispatch', {'intent': 'continue', 'workId': first['workId'],
            'instruction': '另一个长任务夹具。', 'requestId': 'chat-hold-002'})
        await asyncio.sleep(.05)
        replay_cancel = await host.invoke('chat_work_dispatch', cancel)
        assert replay_cancel['attemptId'] == hold['attemptId'] and host.store.get_attempt(next_hold['attemptId']).execution_status == 'running'
        await host.invoke('chat_work_dispatch', {**cancel, 'requestId': 'chat-cancel-002'})
        await settle(host)
        checks.append('report queries without restarting; cancellation stops the owned run and replay cannot cancel a newer Attempt')

        host.fixture_scenario = 'write'
        panel = await host.invoke('run_pi', {'workId': first['workId'], 'approved': False})
        assert panel['permissionRequired'] and panel['attempt'].execution_status == 'cancelled'
        before_events = len(events)
        panel_run = await host.invoke('run_pi', {'workId': first['workId'], 'approved': True, 'allowWrite': True})
        await settle(host)
        assert any(x['event'] == 'permission.requested' and 'chatRequestId' not in x for x in events[before_events:])
        assert any(x.capability == 'provider.pi.tool' and x.status == 'denied'
                   for x in host.store.list_permission_requests(first['workId']))
        assert host.store.get_attempt(panel_run['attemptId']).execution_status == 'succeeded'
        checks.append('original panel provider approval and per-write allow/deny remain separate from chat grants')

        host.fixture_scenario = 'browser'
        host.fixture_manual_allow = True
        browser = await host.invoke('chat_work_dispatch', {'intent': 'continue', 'workId': first['workId'],
            'instruction': '读取显式网址。', 'browserUrl': 'https://example.test/page', 'requestId': 'chat-browser-001'})
        await settle(host)
        assert any(x.capability == 'browser.navigation' and x.status == 'allowed'
                   for x in host.store.list_permission_requests(first['workId']))
        assert any(x['event'] == 'permission.requested' and x.get('chatRequestId') == 'chat-browser-001' and x.get('attemptId') == browser['attemptId'] for x in events)
        checks.append('chat browser navigation still requires an identified one-time user permission')

        host.fixture_scenario = 'write'
        boundary_url = 'https://example.test/' + 'x' * (4096 - len('https://example.test/'))
        boundary = await host.invoke('chat_work_dispatch', {'intent': 'create', 'instruction': '网址长度边界夹具。',
            'summary': '概' * 240, 'browserUrl': boundary_url, 'requestId': 'chat-url-boundary'})
        await settle(host)
        boundary_attempt = host.store.get_attempt(boundary['attemptId'])
        assert boundary_attempt.metadata['browser_origin'] == 'https://example.test' and boundary_url in boundary_attempt.task
        await rejected(host, 'chat_work_dispatch', {'intent': 'create', 'instruction': '网址超限夹具。',
            'browserUrl': boundary_url + 'x', 'requestId': 'chat-url-oversized'})
        checks.append('new local-provider tasks are rejected; 240-character summaries and 4096-character explicit browser URLs match the Core boundary')

        local_project = await host.invoke('register_project', {'path': 'data/workspaces/legacy-local'})
        legacy = await host.invoke('create_work', {'projectId': local_project.project_id, 'goal': 'Local fixture'})
        await host.invoke('run_local', {'workId': legacy.work_item_id})
        await settle(host)
        before = len(clients)
        local_continued = await host.invoke('chat_work_dispatch', {'intent': 'continue', 'workId': legacy.work_item_id,
            'instruction': '再次列出实际文件。', 'requestId': 'chat-local-continue'})
        await settle(host)
        assert host.store.get_attempt(local_continued['attemptId']).provider == 'local' and len(clients) == before
        await rejected(host, 'chat_work_status', {'workId': legacy.work_item_id, 'attemptId': first['attemptId']})
        await rejected(host, 'cancel', {'workId': legacy.work_item_id, 'attemptId': first['attemptId']})
        checks.append('legacy local Work continues with its original local provider; cross-Work Attempt lookup is rejected')

        no_attempt = host.store.create_work_item(local_project.project_id, title='No Attempt cancellation fixture', goal='Synthetic bounded task')
        empty_cancel_request = {'intent': 'cancel', 'workId': no_attempt.work_item_id, 'requestId': 'chat-cancel-no-attempt'}
        empty_cancel = await host.invoke('chat_work_dispatch', empty_cancel_request)
        assert empty_cancel['status'] == 'not_started' and not empty_cancel['attemptId'] and empty_cancel['stopConfirmed']
        host.fixture_scenario = 'hold'
        later_panel = await host.invoke('run_pi', {'workId': no_attempt.work_item_id, 'approved': True})
        await asyncio.sleep(.05)
        empty_recovered = await host.invoke('chat_work_status', {'requestId': empty_cancel_request['requestId']})
        empty_replay = await host.invoke('chat_work_dispatch', empty_cancel_request)
        assert all(status['workId'] == no_attempt.work_item_id and status['status'] == 'not_started'
                   and not status['attemptId'] and not status['busy'] and status['stopConfirmed']
                   and not status['result'] and not status['artifacts'] for status in (empty_recovered, empty_replay))
        assert host.store.get_attempt(later_panel['attemptId']).execution_status == 'running'
        assert host.execution_attempts[no_attempt.work_item_id] == later_panel['attemptId']
        latest_report = await host.invoke('chat_work_dispatch', {'intent': 'report', 'workId': no_attempt.work_item_id, 'requestId': 'chat-empty-cancel-report'})
        assert latest_report['attemptId'] == later_panel['attemptId'] and latest_report['busy']
        assert (await host.invoke('cancel', later_panel))['cancelRequested']
        await settle(host)
        host.fixture_scenario = 'write'
        checks.append('lost receipt and replay of a no-Attempt cancellation remain fixed at not_started while a later panel Attempt keeps running; report still reads latest')

        await rejected(host, 'chat_work_dispatch', {**create, 'requestId': 'bad-intent-001', 'intent': 'shell'})
        await rejected(host, 'chat_work_dispatch', {**create, 'requestId': 'bad-provider-001', 'provider': 'unknown'})
        await rejected(host, 'chat_work_dispatch', {**create, 'requestId': 'local-create-denied', 'provider': 'local'})
        await rejected(host, 'chat_work_dispatch', {**create, 'requestId': 'bad-path-001', 'path': '../outside'})
        await rejected(host, 'chat_work_dispatch', {**create, 'requestId': 'bad-url-001', 'browserUrl': 'file:///outside'})
        check_attempt = host.store.get_attempt(first['attemptId'])
        assert not host.pi.chat_write_decision(item, check_attempt, {'title': '写入工作区文件',
            'message': json.dumps({'tool': 'write', 'path': str(run_root / 'escaped.txt')})})
        assert not host.pi.chat_write_decision(item, check_attempt, {'title': '写入工作区文件',
            'message': json.dumps({'tool': 'write', 'path': str(Path(item.workspace_path) / '.env')})})
        checks.append('invalid intents/providers/paths/browser schemes and escaped or credential-file automatic writes fail closed')

        host.fixture_scenario = 'write'

        async def failed_finalize(attempt, item):
            raise ValueError('Fixture artifact collection failure')

        host.registry.finalize_attempt = failed_finalize
        collect_failure = await host.invoke('chat_work_dispatch', {'intent': 'create', 'instruction': '收集失败夹具。',
            'requestId': 'chat-collect-failure'})
        await settle(host)
        host.registry.finalize_attempt = original_finalize
        failed_status = await host.invoke('chat_work_status', collect_failure)
        assert failed_status['status'] == 'failed' and 'collection failure' in failed_status['error'] and failed_status['stopConfirmed']
        retried = await host.invoke('chat_work_dispatch', {'intent': 'retry', 'workId': collect_failure['workId'],
            'requestId': 'chat-retry-001'})
        await settle(host)
        assert retried['attemptId'] != collect_failure['attemptId'] and host.store.get_attempt(retried['attemptId']).provider == 'pi'
        assert (await host.invoke('chat_work_status', retried))['status'] == 'succeeded'
        checks.append('artifact collection failure remains terminal failure; explicit retry creates a new Attempt with the same provider')

        immediate = await host.invoke('chat_work_dispatch', {'intent': 'create', 'instruction': '首指令前取消夹具。',
            'requestId': 'chat-immediate-001'})
        await host.invoke('chat_work_dispatch', {'intent': 'cancel', 'workId': immediate['workId'], 'requestId': 'chat-immediate-cancel'})
        await settle(host)
        assert (await host.invoke('chat_work_status', immediate))['status'] == 'cancelled'
        checks.append('cancellation before the coroutine starts still commits a terminal state and releases the queue')

        description = host.pi.description
        host.pi.description = lambda: {'installed': True, 'configured': False}
        before = len(host.store.list_work_items(limit=100))
        await rejected(host, 'chat_work_dispatch', {'intent': 'create', 'instruction': 'Unavailable provider fixture',
            'requestId': 'chat-no-provider-001'})
        assert len(host.store.list_work_items(limit=100)) == before
        host.pi.description = description
        checks.append('unconfigured default Pi is rejected before creating a task or allocating an execution')

        # A cleanup failure is terminal failure with retained ownership, never a
        # signal that normal chat may silently resume while a writer survives.
        host.fixture_scenario = 'hold'
        failure = await host.invoke('chat_work_dispatch', {'intent': 'continue', 'workId': first['workId'],
            'instruction': '停止失败夹具。', 'requestId': 'chat-stop-failure'})
        await asyncio.sleep(.05)
        live = clients[-1].process
        live.returncode = None
        original_kill = host.kill

        async def fail_kill(work_id):
            host.tree_stop_failures.add(work_id)
            raise RuntimeError('Fixture child-tree shutdown was not confirmed')

        host.kill = fail_kill
        await host.invoke('chat_work_dispatch', {**cancel, 'requestId': 'chat-cancel-failure'})
        await settle(host)
        failure_status = await host.invoke('chat_work_status', failure)
        assert failure_status['status'] == 'failed' and failure_status['busy'] and not failure_status['stopConfirmed']
        host.kill = original_kill
        live.returncode = 0
        host.tree_stop_failures.clear()
        checks.append('unconfirmed child-tree stop retains busy ownership even when Attempt status is failed')

        # Simulate a host exit after durable acceptance but before scheduling.
        interrupted = {'intent': 'continue', 'workId': first['workId'], 'instruction': '重启夹具。', 'requestId': 'chat-restart-001'}
        restart_id = 'attempt_chat_' + hashlib.sha256(interrupted['requestId'].encode()).hexdigest()[:32]
        fingerprint = hashlib.sha256(json.dumps(interrupted, sort_keys=True, ensure_ascii=False).encode()).hexdigest()
        host.store.create_operation_attempt(first['workId'], intent='amend', instruction=interrupted['instruction'],
            provider='pi', task=interrupted['instruction'], attempt_id=restart_id,
            attempt_metadata={'source': 'atri-chat-work', 'chat_request_id': interrupted['requestId'], 'chat_fingerprint': fingerprint})
        await host.close()
        host = Host(root=run_root, state_dir=run_root / 'data/capabilities')
        clients, events = install_fixture(host)
        restored = await host.invoke('chat_work_dispatch', interrupted)
        assert restored['attemptId'] == restart_id and restored['status'] == 'orphaned' and not clients and restored['stopConfirmed']
        old = await host.invoke('chat_work_dispatch', create)
        assert old['attemptId'] == first['attemptId'] and old['status'] == 'succeeded' and not clients
        empty_after_restart = await host.invoke('chat_work_status', {'requestId': empty_cancel_request['requestId']})
        empty_replayed_after_restart = await host.invoke('chat_work_dispatch', empty_cancel_request)
        assert all(status['status'] == 'not_started' and not status['attemptId'] and status['stopConfirmed']
                   for status in (empty_after_restart, empty_replayed_after_restart))
        checks.append('restart orphans accepted in-flight attempts and duplicate requests never automatically rerun them')
        checks.append('restart preserves no-Attempt cancellation scope without borrowing the later Attempt identity')

        for number in range(62):
            host.store.create_work_item(local_project.project_id, title='safe summary',
                goal='Input D:\\private\\sample.txt api_key=example-secret')
        snapshot = await host.invoke('chat_work_snapshot', {})
        encoded = json.dumps(snapshot)
        assert len(snapshot['tasks']) == 60 and 'D:\\\\private' not in encoded and 'example-secret' not in encoded
        assert not any(key in encoded for key in ('workspace_path', 'metadata', 'result', 'baseUrl'))
        checks.append('snapshot is limited to sixty safe summaries and excludes paths, credentials, metadata and provider outputs')

        result = {'passed': True, 'verifiedAtUtc': time.strftime('%Y-%m-%dT%H:%M:%SZ', time.gmtime()),
            'checks': checks, 'externalModelCalls': 0, 'paidCalls': 0,
            'scope': 'real isolated ledger/artifacts plus deterministic in-process Pi RPC fixture; native Pi/model inference not tested'}
        (run_root / 'report.json').write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf8')
        return result
    finally:
        await host.close()


if __name__ == '__main__':
    with contextlib.redirect_stdout(io.StringIO()):
        result = asyncio.run(verify())
    print(json.dumps(result, ensure_ascii=False, indent=2))

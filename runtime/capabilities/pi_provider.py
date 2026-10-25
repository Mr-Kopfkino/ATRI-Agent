"""Configured Chat API -> pinned Pi native RPC; no original settings import."""
import asyncio
import json
import os
from pathlib import Path
from urllib.parse import urlsplit, urlunsplit
from agent_host.pi_rpc import PiRpcClient


class PiProvider:
    def __init__(self, host):
        self.host = host
        self.config = None
        self.clients = {}
        self.permissions = {}
        self.node = host.root / 'runtime/capabilities/node/node.exe'
        self.entry = host.root / 'runtime/capabilities/node/pi-entry.mjs'
        cache=host.root/'data/cache/browser'
        candidates=list(cache.glob('chromium-*/chrome-win64/chrome.exe'))+list(cache.glob('chromium-*/chrome.exe'))
        self.browser=max(candidates,key=lambda p:int(p.parent.parent.name.split('-')[-1]) if p.parent.name=='chrome-win64' else int(p.parent.name.split('-')[-1]),default=cache/'chromium-missing/chrome.exe')

    def configure(self, args):
        # A rejected replacement must revoke the previous endpoint and key.
        # Only a completely validated current configuration may become ready.
        self.config = None
        base = str(args.get('baseUrl') or '').strip().rstrip('/')
        model, key = str(args.get('model') or '').strip(), str(args.get('key') or '')
        if not base or not model:
            return {'configured': False}
        url = urlsplit(base)
        if (not url.hostname or (url.scheme != 'https' and not (url.scheme == 'http' and url.hostname in ('localhost','127.0.0.1','::1')))
                or url.username or url.password or url.query or url.fragment):
            raise ValueError('Chat API must use HTTPS or local HTTP, without URL credentials/query/fragment')
        _ = url.port  # Reject malformed/out-of-range ports before committing.
        if not model or len(model) > 200 or not key or len(key) > 8192 or any(x in key for x in '\r\n'):
            raise ValueError('Chat API model and valid credential are required')
        if base.endswith('/chat/completions'):
            base = base[:-len('/chat/completions')]
        self.config = {'baseUrl':base,'model':model,'key':key}
        return {'configured':True,'provider':'pi','credentialStorage':'process-memory','inferenceVerified':False}

    def description(self):
        return {'id':'pi','default':True,'installed':self.node.is_file() and self.entry.is_file() and (self.entry.parent/'node_modules/@earendil-works/pi-coding-agent/package.json').is_file(),
                'configured':bool(self.config),'version':'0.86.1','transport':'native-rpc','credentialSource':'current Chat API; process memory only','inferenceVerified':False}

    def redacted(self, value):
        text = str(value or '')
        if self.config:
            text = text.replace(self.config['key'],'[redacted]')
        return text

    async def start(self, workspace, *, access='read', browser_origin=''):
        if not self.config:
            raise ValueError('Configure Chat API address, model and key in SYSTEM before running Pi Work')
        if not self.description()['installed']:
            raise ValueError('Pinned Pi runtime is unavailable; run scripts/setup-capabilities.ps1 -InstallProviders')
        workspace = self.host.within(workspace)
        agent = self.host.state/'pi'
        agent.mkdir(exist_ok=True)
        temp = self.host.root/'data/temp/capability-browser'
        temp.mkdir(parents=True,exist_ok=True)
        env = {k:v for k,v in os.environ.items() if not any(word in k.upper() for word in ('API_KEY','SECRET','TOKEN','PASSWORD'))}
        env.update({'PI_CODING_AGENT_DIR':str(agent),'PI_OFFLINE':'1','PI_SKIP_VERSION_CHECK':'1','PI_TELEMETRY':'0',
                    'TEMP':str(temp),'TMP':str(temp),'TMPDIR':str(temp)})
        client = PiRpcClient([str(self.node),str(self.entry)],cwd=str(workspace),env=env,timeout=25)
        await client.start()
        try:
            await client.send({'type':'atri_bootstrap',**self.config,'workspace':str(workspace),'agentDir':str(agent),
                               'access':access,'browserOrigin':browser_origin,'browserExecutable':str(self.browser),'browserTemp':str(temp)})
            ready = await asyncio.wait_for(client.next_event(),25)
            if ready.get('type') != 'atri_ready':
                raise ValueError('Pi startup rejected the isolated configuration')
            return client,ready
        except BaseException:
            await client.close()
            raise

    async def probe(self):
        result = self.description()
        if not result['configured'] or not result['installed']:
            return result
        client, ready = await self.start(self.host.root/'data/workspaces/default')
        try:
            state = await client.request('get_state')
            return {**result,'rpcReady':True,'authenticatedLocally':True,'remoteAuthenticationVerified':False,
                    'model':state.get('model',{}).get('id'),'tools':ready.get('tools',[]),'streaming':state.get('isStreaming',False)}
        finally:
            await client.close()

    async def respond(self, args):
        key = str(args.get('requestId',''))
        pending = self.permissions.get(key)
        if pending is None:
            raise ValueError('Permission request is unavailable or expired')
        future, permission = pending
        allowed = args.get('allow') is True
        self.host.store.resolve_permission_request(permission.request_id,'allowed' if allowed else 'denied')
        if not future.done():
            future.set_result(allowed)
        return {'resolved':True,'allowed':allowed}

    def chat_write_decision(self, item, attempt, event):
        """Only a policy-checked write/edit in this chat's assigned workspace.

        Pi's extension runs toolPolicy before requesting confirmation. Keep a
        Host-side exact-scope check too; navigation always retains its own UI
        decision and panel Attempts never receive the chat grant.
        """
        if attempt.metadata.get('source') != 'atri-chat-work' or event.get('title') != '写入工作区文件':
            return None
        try:
            details = json.loads(event.get('message') or '')
            scope = self.host.within(attempt.metadata.get('autoWriteScope', ''))
            workspace = self.host.within(item.workspace_path)
            path = self.host.within(details['path'])
            if (attempt.metadata.get('access') != 'write' or scope != workspace
                    or not scope.is_relative_to(self.host.root/'data/workspaces')
                    or not path.is_relative_to(scope) or details.get('tool') not in ('write', 'edit')):
                return False
            for part in path.relative_to(scope).parts:
                name = part.lower()
                if (name in ('.git', '.env', 'auth.json', 'credentials.json', 'secrets.json')
                        or name.startswith('.env.') or name.endswith('.dpapi')):
                    return False
            return True
        except (KeyError, TypeError, ValueError, OSError):
            return False

    async def run(self,item,attempt):
        origin = attempt.metadata.get('browser_origin','')
        client,ready = await self.start(item.workspace_path,access=attempt.metadata.get('access','read'),browser_origin=origin)
        self.clients[item.work_item_id] = client
        self.host.processes[item.work_item_id] = client.process
        text,final,stop = '','',''
        try:
            self.host.emit('provider.ready',workId=item.work_item_id,attemptId=attempt.attempt_id,provider='pi',tools=ready.get('tools',[]))
            await client.request('prompt',message=attempt.task)
            async with asyncio.timeout(900):
                while True:
                    event = await client.next_event()
                    kind = event.get('type')
                    if kind == 'message_update':
                        delta = event.get('assistantMessageEvent') or {}
                        if delta.get('type') == 'text_delta':
                            fragment = self.redacted(delta.get('delta',''))
                            text = (text+fragment)[-24000:]
                            self.host.emit('provider.progress',workId=item.work_item_id,attemptId=attempt.attempt_id,provider='pi',nativeType=kind,text=fragment[:8000])
                    elif kind == 'message_end':
                        message=event.get('message') or {}
                        if message.get('role')=='assistant':
                            final=''.join(b.get('text','') for b in message.get('content',[]) if b.get('type')=='text')
                            stop=str(message.get('stopReason',''))
                    elif kind == 'extension_ui_request':
                        native_id=str(event.get('id',''))
                        if event.get('method')!='confirm':
                            await client.send({'type':'extension_ui_response','id':native_id,'cancelled':True})
                            continue
                        reason=self.redacted(str(event.get('title',''))+'\n'+str(event.get('message','')))[:2000]
                        browser_permission=str(event.get('title','')).startswith('读取外部')
                        permission=self.host.store.create_permission_request(item.work_item_id,attempt_id=attempt.attempt_id,
                            capability='browser.navigation' if browser_permission else 'provider.pi.tool',action='GET' if browser_permission else 'write-file',scope_paths=[] if browser_permission else [item.workspace_path],reason=reason,options=['allow_once','deny'])
                        automatic = self.chat_write_decision(item, attempt, event)
                        if automatic is not None:
                            self.host.store.resolve_permission_request(permission.request_id, 'allowed' if automatic else 'denied')
                            self.host.emit('permission.resolved', workId=item.work_item_id, attemptId=attempt.attempt_id,
                                requestId=permission.request_id, allowed=automatic, automatic=True, reason=reason)
                            await client.send({'type':'extension_ui_response','id':native_id,'confirmed':automatic})
                            continue
                        future=asyncio.get_running_loop().create_future()
                        self.permissions[permission.request_id]=(future,permission)
                        self.host.emit('permission.requested',workId=item.work_item_id,attemptId=attempt.attempt_id,requestId=permission.request_id,reason=reason)
                        try:
                            allowed=await asyncio.wait_for(future,120)
                        except TimeoutError:
                            self.host.store.resolve_permission_request(permission.request_id,'denied')
                            allowed=False
                        finally:
                            self.permissions.pop(permission.request_id,None)
                        await client.send({'type':'extension_ui_response','id':native_id,'confirmed':allowed})
                    elif kind in ('tool_execution_start','tool_execution_end'):
                        self.host.emit('provider.tool',workId=item.work_item_id,attemptId=attempt.attempt_id,provider='pi',tool=str(event.get('toolName',''))[:80],success=not event.get('isError',False))
                    elif kind=='extension_error':
                        raise ValueError('Pi tool extension failed; inspect the workspace before retrying')
                    elif kind=='agent_settled':
                        if stop in ('error','aborted'):
                            raise ValueError('Pi request failed; check API authentication, model and tool-call/streaming compatibility')
                        if not (final or text).strip():
                            raise ValueError('Pi settled without a final assistant response')
                        return self.redacted(final or text)
        finally:
            for key,(future,permission) in list(self.permissions.items()):
                if permission.attempt_id==attempt.attempt_id:
                    self.host.store.resolve_permission_request(key,'denied')
                    if not future.done():future.set_result(False)
                    self.permissions.pop(key,None)
            self.clients.pop(item.work_item_id,None)
            try:
                await self.host.kill(item.work_item_id)
            except BaseException:
                # A failed tree stop retains the process owner in Host. Stop
                # our readers without substituting parent-only termination.
                await client.detach()
                raise
            await client.close()

    async def browser_read(self,args):
        from agent_host.browser_request_contract import normalize_web_address
        url = normalize_web_address(str(args.get('url','')))
        if not url:
            raise ValueError('An explicit HTTP(S) page address is required')
        parsed = urlsplit(url)
        if parsed.scheme not in ('http','https') or parsed.username or parsed.password:
            raise ValueError('Only an explicit HTTP(S) page without embedded credentials is accepted')
        origin=urlunsplit((parsed.scheme,parsed.netloc,'','',''))
        item=self.host.item(args['workId'])
        if not Path(item.workspace_path).is_relative_to(self.host.root/'data/workspaces'):
            raise ValueError('Browser artifacts require a dedicated Work directory')
        operation=self.host.store.create_operation(item.work_item_id,intent='execute',instruction='Read and screenshot explicitly selected URL: '+url)
        attempt=self.host.store.create_attempt(item.work_item_id,provider='browser',task='Read '+url,operation_id=operation.operation_id,mode='inspect')
        permission=self.host.store.create_permission_request(item.work_item_id,attempt_id=attempt.attempt_id,capability='browser.read',action='read-screenshot',scope_paths=[],reason='Open exact user-selected URL: '+url,options=['allow','deny'])
        allowed=args.get('approved') is True
        self.host.store.resolve_permission_request(permission.request_id,'allowed' if allowed else 'denied')
        if not allowed:
            self.host.store.update_attempt(attempt.attempt_id,execution_status='cancelled',error='Browser read was not approved')
            return {'permissionRequired':True}
        self.host.store.update_attempt(attempt.attempt_id,execution_status='running')
        temp=self.host.root/'data/temp/capability-browser'
        temp.mkdir(parents=True,exist_ok=True)
        process=await asyncio.create_subprocess_exec(str(self.node),str(self.entry.parent/'browser.mjs'),cwd=str(self.entry.parent),
            stdin=asyncio.subprocess.PIPE,stdout=asyncio.subprocess.PIPE,stderr=asyncio.subprocess.PIPE,creationflags=getattr(__import__('subprocess'),'CREATE_NO_WINDOW',0))
        process_key='browser-'+attempt.attempt_id
        self.host.processes[process_key]=process
        try:
            data=json.dumps({'url':url,'origin':origin,'executable':str(self.browser),'tempRoot':str(temp)}).encode()
            stdout,_=await asyncio.wait_for(process.communicate(data),35)
            if len(stdout)>2*1024*1024:raise ValueError('Browser output exceeds budget')
            response=json.loads(stdout)
            if not response.get('ok'):raise ValueError('Managed browser could not read the page')
            result=response['result']
            import base64,hashlib
            png=base64.b64decode(result.pop('screenshot'),validate=True)
            path=self.host.within(Path(item.workspace_path)/('browser-'+__import__('uuid').uuid4().hex+'.png'))
            path.write_bytes(png)
            artifact=self.host.store.register_artifact(item.work_item_id,attempt_id=attempt.attempt_id,kind='business.file',title='Managed browser screenshot',path=path,location='workspace',sha256=hashlib.sha256(png).hexdigest(),size_bytes=len(png))
            self.host.store.update_attempt(attempt.attempt_id,execution_status='succeeded',result=json.dumps(result,ensure_ascii=False)[:24000])
            self.host.store.set_work_item_state(item.work_item_id,'review_ready')
            return {**result,'screenshotPath':str(path),'artifact':artifact}
        except BaseException:
            self.host.store.update_attempt(attempt.attempt_id,execution_status='failed',error='Managed browser read failed or was interrupted')
            raise
        finally:
            if process.returncode is None:
                await self.host.kill(process_key)
            self.host.processes.pop(process_key,None)

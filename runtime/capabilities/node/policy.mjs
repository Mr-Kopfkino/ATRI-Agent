// Workspace checks derived from Amadeus pi_runtime/amadeus.ts (AGPL-3.0).
import fs from 'node:fs';
import path from 'node:path';
export function checkedPath(root, value) {
  const absolute = path.resolve(root, String(value || '.'));
  const relative = path.relative(root, absolute);
  if (path.isAbsolute(relative) || relative === '..' || relative.startsWith(`..${path.sep}`)) throw new Error('Path is outside the approved workspace');
  let current = absolute;
  while (current !== root) {
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) throw new Error('Links and junctions are not permitted');
    const name = path.basename(current).toLowerCase();
    if (name === '.git' || name === '.env' || name.startsWith('.env.') || name.endsWith('.dpapi') || ['auth.json','credentials.json','secrets.json'].includes(name)) throw new Error('Credential files and Git internals are not Work inputs');
    current = path.dirname(current);
  }
  return absolute;
}
export function toolPolicy(root, access, name, input) {
  const readers = new Set(['read','ls']);
  const writers = new Set(['write','edit']);
  if (!readers.has(name) && !writers.has(name)) return {allowed:false, reason:'Shell, arbitrary extensions and unknown tools are disabled'};
  if (access !== 'write' && writers.has(name)) return {allowed:false, reason:'This attempt has read-only permission'};
  try {
    if(JSON.stringify(input).length>256*1024)throw new Error('Tool arguments exceed 256KiB');
    const target=checkedPath(root,input.path);
    if(name==='read'&&fs.statSync(target).size>2*1024*1024)throw new Error('Read exceeds the 2MiB file budget');
    return {allowed:true, path:target, confirm:writers.has(name)};
  }
  catch(e) { return {allowed:false, reason:e.message}; }
}

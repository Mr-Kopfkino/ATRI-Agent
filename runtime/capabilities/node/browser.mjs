import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
export function approvedUrl(value,origin) {
  const url=new URL(value), allowed=new URL(origin);
  if(!['http:','https:'].includes(url.protocol)||url.username||url.password||url.origin!==allowed.origin)throw new Error('URL is outside the exact user-approved HTTP(S) origin');
  return url;
}
export async function readPage(value,{origin,executable,tempRoot}) {
  const url=approvedUrl(value,origin);
  if(!fs.existsSync(executable))throw new Error('Managed Chromium executable is unavailable');
  fs.mkdirSync(tempRoot,{recursive:true});
  // Playwright uses os.tmpdir for its ephemeral user profile; only this child changes TEMP.
  process.env.TEMP=tempRoot;process.env.TMP=tempRoot;process.env.TMPDIR=tempRoot;
  const browser=await chromium.launch({executablePath:executable,headless:true,downloadsPath:tempRoot,
    args:['--disable-background-networking','--disable-sync','--disable-component-update','--no-first-run']});
  try {
    const context=await browser.newContext({viewport:{width:1200,height:800},acceptDownloads:false,serviceWorkers:'block'});
    await context.routeWebSocket('**/*',socket=>socket.close());
    await context.route('**/*',async route=>{
      const request=route.request();
      try { approvedUrl(request.url(),origin);if(!['GET','HEAD'].includes(request.method()))throw new Error('Data sending is disabled');await route.continue(); }
      catch { await route.abort('blockedbyclient'); }
    });
    const page=await context.newPage();
    const response=await page.goto(url.href,{waitUntil:'domcontentloaded',timeout:25000});
    await page.waitForTimeout(100);
    approvedUrl(page.url(),origin);
    const text=(await page.locator('body').innerText({timeout:5000})).slice(0,24000);
    const screenshot=await page.screenshot({type:'png',fullPage:false,timeout:10000});
    if(screenshot.length>1024*1024)throw new Error('Screenshot exceeds the 1MiB browser budget');
    const links=await page.locator('a[href]').evaluateAll(elements=>elements.slice(0,60).map(x=>({text:x.innerText.slice(0,120),url:x.href})));
    return {url:page.url(),title:(await page.title()).slice(0,240),text,links,status:response?.status()??null,screenshot:screenshot.toString('base64'),contentIsExternal:true,readOnly:true};
  } finally { await browser.close(); }
}
if(process.argv[1] && path.resolve(process.argv[1])===path.resolve(fileURLToPath(import.meta.url))) {
  let input='';process.stdin.setEncoding('utf8');process.stdin.on('data',x=>{input+=x;if(input.length>65536)process.exit(1);});
  process.stdin.on('end',async()=>{try{const args=JSON.parse(input);const result=await readPage(args.url,args);process.stdout.write(JSON.stringify({ok:true,result}));}catch{process.stdout.write(JSON.stringify({ok:false,error:'Managed browser read failed; check exact origin, page access and runtime'}));process.exitCode=1;}});
}

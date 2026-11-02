let page='dashboard',items=[],sources=[],events=[];
let metricDays='7',metricItemId='';
const $=x=>document.querySelector(x), el=(tag,text,cls)=>{const e=document.createElement(tag);if(text!==undefined)e.textContent=String(text);if(cls)e.className=cls;return e};
const esc=s=>String(s??'').replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
function toast(s){$('#toast').textContent=s;$('#toast').style.display='block';setTimeout(()=>$('#toast').style.display='none',5500)}
async function api(path,method='GET',data){const r=await fetch(path,{method,headers:{'Content-Type':'application/json'},body:data?JSON.stringify(data):undefined});if(r.status===401)throw Error('Login required');if(!r.ok)throw Error((await r.text()).slice(0,250));const responseText=await r.text();return responseText.trim()?JSON.parse(responseText):null}
async function load(){try{[items,sources,events]=await Promise.all([api('/api/items'),api('/api/sources'),api('/api/events?limit=150')]);render()}catch(e){if(String(e).includes('Login required'))login();else $('#main').innerHTML=`<section class="card"><h2>Cannot connect</h2><p>${esc(e.message)}</p></section>`}}
function login(){$('#main').innerHTML=`<section class="card" style="max-width:480px;margin:70px auto"><h1>Sign in</h1><p>Enter the StockSentinel API key configured on the Pi.</p><form id="loginForm"><label><span>API key</span><input name="key" type="password" required autocomplete="current-password"></label><button class="primary">Sign in</button></form></section>`;$('#loginForm').onsubmit=async e=>{e.preventDefault();const key=e.target.key.value;try{const r=await fetch('/login',{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify({key})});if(!r.ok)throw Error('Invalid key');await load()}catch(err){toast(err.message)}}}
function pill(st){return `<span class="pill ${esc(st)}">${esc(st)}</span>`}
function button(label,fn,cls=''){let b=el('button',label,cls);b.onclick=fn;return b}
function wrap(html){$('#main').innerHTML=html}
function ago(s){if(!s)return 'Never';let d=new Date(s);return d.toLocaleString()}
function render(){document.querySelectorAll('nav button').forEach(b=>b.classList.toggle('active',b.dataset.page===page));
if(page==='dashboard'){let enabled=sources.filter(s=>s.enabled).length,good=sources.filter(s=>s.status==='Available').length;wrap(`<div class="topline"><div><h1>Dashboard</h1><p>Live checks and availability. Unknown is never treated as out of stock.</p></div></div><div class="grid"><div class="card"><div class="muted">Monitored products</div><div class="stat">${items.filter(i=>i.enabled).length}</div></div><div class="card"><div class="muted">Enabled listings</div><div class="stat">${enabled}</div></div><div class="card"><div class="muted">Available listings</div><div class="stat">${good}</div></div></div><div class="card"><h2>Listings</h2><div id="listing" class="list"></div></div><div class="card"><h2>Recent activity</h2><div id="activityPreview"></div></div>`);sourceEntries($('#listing'));$('#activityPreview').innerHTML=events.slice(0,8).map(e=>`<p><small>${esc(ago(e.atUtc))} · ${esc(e.level)}</small> — ${esc(e.message)}</p>`).join('')||'<p>No checks yet.</p>'}
if(page==='products'){wrap('<div class="topline"><div><h1>Products</h1><p>Manage target products and maximum prices.</p></div><div id="add"></div></div><div class="card"><div id="productList" class="list"></div></div>');$('#add').append(button('+ Add product',()=>productEditor(), 'primary'));for(let i of items){let row=el('div',undefined,'entry');row.innerHTML=`<div><h3>${esc(i.name)}</h3><p>Maximum £${Number(i.maxPriceGbp).toFixed(2)} · ${i.enabled?'Enabled':'Disabled'}</p></div>`;row.append(button('Edit',()=>productEditor(i)));$('#productList').append(row)}}
if(page==='sources'){wrap('<div class="topline"><div><h1>Sources</h1><p>Each source is a product listing with its own polling rules.</p></div><div id="add"></div></div><div class="card"><div id="listing" class="list"></div></div>');$('#add').append(button('Import Template',importTemplate),button('+ Add source',()=>sourceEditor(), 'primary'));sourceEntries($('#listing'))}
if(page==='activity'){wrap(`<div class="topline"><div><h1>Activity log</h1><p>Most recent 150 events (database retains up to 5,000).</p></div></div><div class="card" style="overflow:auto"><table><thead><tr><th>When</th><th>Level</th><th>Event</th></tr></thead><tbody>${events.map(e=>`<tr><td>${esc(ago(e.atUtc))}</td><td>${esc(e.level)}</td><td>${esc(e.message)}</td></tr>`).join('')}</tbody></table></div>`)}
if(page==='metrics'){
wrap(`<div class="topline"><div><h1>Metrics & stock patterns</h1><p>Monitoring health and observed restock history.</p></div><label>Product <select id="metricProduct"><option value="">All products</option>${items.map(i=>`<option value="${i.id}">${esc(i.name)}</option>`).join('')}</select></label><label>Period <select id="metricDays"><option value="1">24 hours</option><option value="7" selected>7 days</option><option value="30">30 days</option><option value="90">90 days</option></select></label></div><div id="metricBody" class="card">Loading metrics…</div>`);
$('#metricDays').value=metricDays;$('#metricProduct').value=metricItemId;
const body=$('#metricBody');let requestId=0;
const show=async()=>{const request=++requestId;metricDays=$('#metricDays').value;metricItemId=$('#metricProduct').value;try{const m=await api('/api/metrics?days='+metricDays+(metricItemId?'&itemId='+encodeURIComponent(metricItemId):''));if(!body.isConnected||request!==requestId)return;
const success=m.totalChecks?Math.round(100*m.successfulChecks/m.totalChecks):0;
const bars=(data,labels)=>`<div class="metric-bars">${data.map((v,i)=>`<div class="metric-col" title="${esc(labels[i])}: ${v}"><div class="metric-bar" style="height:${Math.max(2,Math.round(95*v/Math.max(1,...data)))}px"></div><small>${esc(labels[i])}</small></div>`).join('')}</div>`;
const sourceName=id=>sources.find(s=>s.id===id)?.name??`Source ${id}`;
$('#metricBody').innerHTML=`<div class="stats"><div class="card"><div class="muted">Checks</div><div class="stat">${m.totalChecks}</div></div><div class="card"><div class="muted">Confirmed checks</div><div class="stat">${success}%</div></div><div class="card"><div class="muted">Observed restocks</div><div class="stat">${m.restockCount}</div></div><div class="card"><div class="muted">Push alerts accepted</div><div class="stat">${m.pushAlerts}</div></div></div><p class="footnote">Average response time: ${m.averageResponseMs} ms · Failed/unknown checks: ${m.failedChecks}</p><h2>Restocks by hour (UK time)</h2>${bars(m.byHour,Array.from({length:24},(_,i)=>String(i).padStart(2,'0')))}<h2>Restocks by weekday</h2>${bars([1,2,3,4,5,6,0].map(i=>m.byWeekday[i]),['Mon','Tue','Wed','Thu','Fri','Sat','Sun'])}<h2>Recent stock windows</h2><div style="overflow:auto"><table><thead><tr><th>Detected</th><th>Source</th><th>Confirmed span</th></tr></thead><tbody>${m.restocks.map(x=>`<tr><td>${esc(ago(x.atUtc))}</td><td>${esc(sourceName(x.sourceId))}</td><td>${x.confirmedMinutes} min${x.historical?" (historical)":""}</td></tr>`).join('')||'<tr><td colspan="3">No observed restocks yet</td></tr>'}</tbody></table></div><p class="footnote">Stock spans are minimum observed intervals, not exact retailer availability durations. Unknown and failed checks are excluded from confirmed-check percentages. Restock analysis also includes retained historical events. Detailed check statistics begin with the update and are retained for 90 days. Product metrics use current source assignments; history for deleted or unidentifiable sources is included only in All products.</p>`;
}catch(e){if(body.isConnected&&request===requestId)body.textContent=e.message;}};
$('#metricDays').onchange=show;$('#metricProduct').onchange=show;show();return;
}
if(page==='notifications'){
wrap(`<h1>Notifications</h1><div class="card"><h2>Web Push</h2><p>On iPhone: open StockSentinel in Safari, Share → Add to Home Screen, then open the Home Screen app and subscribe here. Notifications can arrive anywhere, even when you're away from home.</p><p id="pushState">Checking support…</p><div id="pushControls" class="actions"></div></div>`);
setupPush().catch(e=>{ $('#pushState').textContent=e.message; });
return;
}

}
function sourceEntries(where){if(!sources.length){where.append(el('p','No sources yet. Add a source with verified selectors and leave it disabled until tested.'));return}for(let s of sources){let row=el('div',undefined,'entry'),i=items.find(x=>x.id===s.itemId);let left=el('div');left.style.flex='1';left.style.minWidth='230px';left.innerHTML=`<h3>${esc(s.name)} ${pill(s.enabled?s.status:'Disabled')}</h3><p>${esc(i?.name??'Unknown product')} · every ${s.intervalMinutes} min · Last ${esc(ago(s.lastCheckedUtc))}</p><p>${esc(s.lastDetail??'Not checked yet')}</p><p><a href="${esc(s.url)}" target="_blank" rel="noreferrer noopener">View listing</a></p>`;let acts=el('div',undefined,'actions');acts.append(button('Check now',async()=>{try{toast('Checking listing…');const r=await api(`/api/sources/${s.id}/check`,'POST');toast(`${s.name}: ${r.status} — ${r.detail}`);await load()}catch(e){toast(e.message)}}));acts.append(button('Export Template',()=>exportTemplate(s)));acts.append(button('Edit',()=>sourceEditor(s)));acts.append(button('Delete',async()=>{if(confirm(`Delete ${s.name}?`)){try{await api(`/api/sources/${s.id}`,'DELETE');await load()}catch(e){toast(e.message)}}},'danger'));row.append(left,acts);where.append(row)}}
function field(name,label,type,value,extra=''){let id='f_'+name;return `<label><span>${esc(label)}</span><input id="${id}" name="${esc(name)}" type="${type}" ${type==='checkbox'?(value?'checked':''):`value="${esc(value??'')}"`} ${extra}></label>`}
function modal(title,html,collect,onSave,saveLabel='Save'){$('#editTitle').textContent=title;$('#editForm button[type=submit]').textContent=saveLabel;$('#formFields').innerHTML=html;$('#editor').showModal();$('#editForm').onsubmit=async e=>{e.preventDefault();try{await onSave(collect(new FormData(e.target)));$('#editor').close();await load();toast('Saved')}catch(err){toast(err.message)}}}
function productEditor(i){modal(i?'Edit product':'Add product',field('name','Name','text',i?.name,'required maxlength="120"')+field('maxPriceGbp','Maximum price (£)','number',i?.maxPriceGbp??55,'step="0.01" min="0.01" required')+field('enabled','Enabled','checkbox',i?.enabled??true),d=>({name:d.get('name'),maxPriceGbp:Number(d.get('maxPriceGbp')),enabled:d.has('enabled')}),x=>api(i?`/api/items/${i.id}`:'/api/items',i?'PUT':'POST',x))}
function sourceEditor(s,importing=false,duplicates=[]){
 if(!items.length&&!importing){toast('Create a product first');return}
 const options=(importing?'<option value="new">Create a new product</option>':'')+items.map(i=>`<option value="${i.id}" ${s?.itemId===i.id?'selected':''}>${esc(i.name)}</option>`).join('');
 const defaultRules={idPath:'$.data.products[0].id',expectedId:'000000000010004546',availablePath:'$.data.products[0].inventory.orderable',confirmationPath:'$.data.products[0].c_availabilityModel.orderable',preorderPath:'$.data.products[0].inventory.preorderable',pricePath:'$.data.products[0].price.value',currencyPath:'$.data.products[0].price.currencyCode',currency:'GBP',purchaseUrl:'https://store.nintendo.com/en-gb/pokemon-go-plus-000000000010004546'};
 let rules=s?.jsonConfig??JSON.stringify(defaultRules,null,2);
 let html=`<label><span>Product</span><select name="itemId">${options}</select></label>`+(importing?`<div id="templateProductFields">${field('productName','New product name','text',s.name,'maxlength="120"')}${field('productPrice','Maximum price (£)','number',55,'step="0.01" min="0.01" max="100000"')}</div><p>Review these settings before importing. The new source will be disabled; use Check now before enabling it.</p>${duplicates.length?`<p>Possible duplicates: ${duplicates.map(esc).join(', ')}. Import creates another source and never overwrites one.</p>`:''}`:'')+field('name','Retailer / source name','text',s?.name,'required maxlength="150"')+field('url','HTTPS request URL','url',s?.url,'required')+field('intervalMinutes','Polling interval (minutes, minimum 3)','number',s?.intervalMinutes??15,'min="3" max="1440" required')+`<label><span>Detector</span><select name="adapter" id="detector"><option value="html">HTML / CSS</option><option value="rest-json">REST / JSON (GET)</option></select></label>`+
 `<div id="htmlFields">`+field('availableSelector','Available CSS selector','text',s?.availableSelector)+field('availabilityText','Optional available text','text',s?.availabilityText)+field('unavailableSelector','Unavailable CSS selector','text',s?.unavailableSelector)+field('unavailabilityText','Optional unavailable text','text',s?.unavailabilityText)+field('priceSelector','Price CSS selector','text',s?.priceSelector)+field('pricePattern','GBP price regex with (?<price>...)','text',s?.pricePattern??'£\\s*(?<price>[0-9,]+(?:\\.[0-9]{2})?)')+`</div>`+
 `<div id="jsonFields"><label><span>REST/JSON rules (editable JSON)</span><textarea name="jsonConfig" rows="13" spellcheck="false">${esc(rules)}</textarea></label><p class="footnote">Use GET and a public HTTPS JSON endpoint. Paths support properties, [index] and quoted keys like [\"SKU/A\"]. Optional itemsPath selects an array (or [*] suffix); labelPath identifies each item; matchMode is any or all. Boolean flags are the default; optional availableValue matches a string. Price/currency paths are optional for stock-only checks. Use the retailer product page in purchaseUrl.</p></div>`+
 (importing?'':field('enabled','Enable automatic checks','checkbox',s?.enabled??false))+`<p class="footnote">Use Check now before enabling monitoring. Missing/conflicting data produces Unknown, never out-of-stock.</p>`;
 const collect=d=>({itemId:Number(d.get('itemId')),name:d.get('name'),url:d.get('url'),intervalMinutes:Number(d.get('intervalMinutes')),enabled:d.has('enabled'),adapter:d.get('adapter'),availableSelector:d.get('availableSelector')||null,availabilityText:d.get('availabilityText')||null,unavailableSelector:d.get('unavailableSelector')||null,unavailabilityText:d.get('unavailabilityText')||null,priceSelector:d.get('priceSelector')||null,pricePattern:d.get('pricePattern')||null,jsonConfig:d.get('adapter')==='rest-json'?d.get('jsonConfig'):null});
 let saving=false,createdProductId=null;
 modal(importing?'Import source template — preview':s?'Edit source':'Add source',html,collect,async x=>{
  if(!importing)return api(s?`/api/sources/${s.id}`:'/api/sources',s?'PUT':'POST',x);
  if(saving)throw Error('Import already in progress');saving=true;
  try{
   const {itemId,enabled,...settings}=x;
   settings.jsonConfig=x.adapter==='rest-json'?JSON.parse(x.jsonConfig):undefined;
   const template={format:'stocksentinel-source',version:1,source:settings};
   const preview=await api('/api/source-templates/preview','POST',template);
   if(preview.duplicates.length&&!confirm('Possible duplicate sources: '+preview.duplicates.join(', ')+'. Import another disabled source?'))throw Error('Import cancelled');
   const product=$('#editForm select[name=itemId]').value;
   let targetId=Number(product);
   if(product==='new'){
    if(!createdProductId){const result=await api('/api/items','POST',{name:$('#f_productName').value,maxPriceGbp:Number($('#f_productPrice').value),enabled:true});createdProductId=result.id;}
    targetId=createdProductId;
   }
   return await api('/api/source-templates/import','POST',{template,itemId:targetId});
  }finally{saving=false;}
 },importing?'Import template':'Save');
 if(importing){const product=$('#editForm select[name=itemId]');const toggleProduct=()=>{const fresh=product.value==='new';$('#templateProductFields').hidden=!fresh;$('#f_productName').required=fresh;$('#f_productPrice').required=fresh;$('#f_productName').disabled=!fresh;$('#f_productPrice').disabled=!fresh;};product.onchange=toggleProduct;toggleProduct();}
 const selector=$('#detector');selector.value=s?.adapter??'html';const toggle=()=>{$('#htmlFields').style.display=selector.value==='html'?'block':'none';$('#jsonFields').style.display=selector.value==='rest-json'?'block':'none'};selector.onchange=toggle;toggle();
}

async function exportTemplate(source){
 try{
  const response=await fetch(`/api/sources/${source.id}/template`);
  if(!response.ok)throw Error((await response.text()).slice(0,250)||'Export failed');
  const url=URL.createObjectURL(await response.blob()),link=el('a');
  link.href=url;link.download='stocksentinel-source-template.json';document.body.append(link);link.click();link.remove();setTimeout(()=>URL.revokeObjectURL(url),1000);
 }catch(e){toast(e.message)}
}
function importTemplate(){
 const input=el('input');input.type='file';input.accept='.json,application/json';
 input.onchange=async()=>{try{
  const file=input.files[0];if(!file)return;
  if(file.size>65536)throw Error('Template must be at most 64 KiB');
  const template=JSON.parse(await file.text());
  const preview=await api('/api/source-templates/preview','POST',template);
  sourceEditor({...preview.source,itemId:items[0]?.id},true,preview.duplicates);
 }catch(e){toast(e.message)}};input.click();
}

document.querySelectorAll('nav button').forEach(b=>b.onclick=()=>{page=b.dataset.page;render()});$('#refresh').onclick=load;$('#cancel').onclick=()=>$('#editor').close();load();setInterval(()=>{if(!$('#editor').open)load()},60000);

function base64UrlToBytes(value){const pad='='.repeat((4-value.length%4)%4);const binary=atob((value+pad).replace(/-/g,'+').replace(/_/g,'/'));return Uint8Array.from(binary,c=>c.charCodeAt(0));}
async function setupPush(){
 const state=$('#pushState'),buttons=$('#pushControls');
 if(!('serviceWorker' in navigator)||!('PushManager' in window)||!('Notification' in window)){state.textContent='Web Push unsupported in this browser.';return;}
 if(!window.isSecureContext){state.textContent='A trusted HTTPS origin is required for Web Push.';return;}
 const info=await api('/api/push/key');
 const reg=await navigator.serviceWorker.register('/sw.js');await navigator.serviceWorker.ready;
 const current=await reg.pushManager.getSubscription();
 state.textContent=`Permission: ${Notification.permission}. This browser: ${current?'subscribed':'not subscribed'}. Stored subscriptions: ${info.subscriptions}.`;
 buttons.replaceChildren();
 buttons.append(button('Enable push on this device',async()=>{
   try{
    const permission=await Notification.requestPermission();if(permission!=='granted')throw Error('Notification permission not granted');
    const subscription=await reg.pushManager.subscribe({userVisibleOnly:true,applicationServerKey:base64UrlToBytes(info.publicKey)});
    await api('/api/push/subscribe','POST',subscription.toJSON());toast('Push subscription saved');await setupPush();
   }catch(e){toast('Subscription failed: '+e.message)}
 },'primary'));
 buttons.append(button('Send test notification',async()=>{try{const result=await api('/api/push/test','POST');toast(`Push service accepted ${result.accepted} notification(s)`)}catch(e){toast('Push test failed: '+e.message)}}));
 if(current)buttons.append(button('Disable this device',async()=>{try{await api('/api/push/unsubscribe','POST',{endpoint:current.endpoint});await current.unsubscribe();await setupPush()}catch(e){toast(e.message)}}));
}

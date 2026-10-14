import {responseResult} from './document-state.mjs';
import {t} from './ui-catalog.mjs';
export const sessionRequired=t('sessionRequired',{},'en');
// The browser sees only a short-lived, single-use code, never the instance token.
// Strip it before the first await, even when exchange fails; keep film selection.
export async function browserSession(href,history,fetch){
  const url=new URL(href),code=url.searchParams.get('code');
  if(url.searchParams.has('code')){url.searchParams.delete('code');history.replaceState(null,'',url);}
  const response=await fetch(code===null?'/api/state':'/api/session',code===null?{credentials:'same-origin'}:
    {method:'POST',credentials:'same-origin',headers:{'Content-Type':'application/json'},body:JSON.stringify({code})});
  try{return await responseResult(response);}
  catch(error){if(error.status===401)error.message=t('sessionRequired');throw error;}
}

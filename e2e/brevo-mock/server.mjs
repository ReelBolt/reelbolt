// Local fake Brevo for QA. Records every call. Never talks to the internet.
import http from 'node:http';
const PORT = Number(process.env.PORT || 19741);
const KEY = 'qa-fake-not-a-key';
const st = {
  calls: [], lists: { 10: new Set(), 11: new Set() }, camps: {}, nextId: 100,
  mode: { importFail: false, sendNow: 'ok' /* ok | 500 | 400 */, campStatus: 'sent', stats: null, sendDelayMs: 0, getCampDelayMs: 0, sendTest: 'ok', getCamp: 'ok' /* ok|500|404 */, createFail: null /* 500|badbody */, processStatus: 'completed', sendHold: false, importDelayMs: 0 },
};
const send = (res, code, obj) => { res.writeHead(code, { 'content-type': 'application/json' }); res.end(obj === undefined ? '' : JSON.stringify(obj)); };
const handler = (req, res) => {
  const chunks = [];
  req.on('data', (c) => chunks.push(c));
  req.on('end', async () => {
    const body = Buffer.concat(chunks).toString('utf8');
    const u = new URL(req.url, 'http://x');
    const p = u.pathname;
    let j = null; try { j = body ? JSON.parse(body) : null; } catch {}
    if (p.startsWith('/__qa/')) {
      if (p === '/__qa/calls') return send(res, 200, st.calls);
      if (p === '/__qa/reset') { st.calls.length = 0; return send(res, 200, {}); }
      if (p === '/__qa/state') return send(res, 200, { lists: Object.fromEntries(Object.entries(st.lists).map(([k, v]) => [k, [...v]])), mode: st.mode, camps: st.camps });
      if (p === '/__qa/mode') { Object.assign(st.mode, j || {}); return send(res, 200, st.mode); }
      return send(res, 404, {});
    }
    st.calls.push({ t: new Date().toISOString(), method: req.method, path: p + u.search, keyOk: req.headers['api-key'] === KEY, body: j });
    if (req.headers['api-key'] !== KEY) return send(res, 401, { code: 'unauthorized', message: 'Key not found' });
    let m;
    const path = p.replace(/^\/v3\//, '');
    if (req.method === 'POST' && (m = path.match(/^contacts\/lists\/(\d+)\/contacts\/remove$/))) { st.lists[m[1]]?.clear(); return send(res, 201, { contacts: { processId: ++st.nextId } }); }
    if (req.method === 'POST' && path === 'contacts/import') {
      if (st.mode.importDelayMs) await new Promise((r) => setTimeout(r, st.mode.importDelayMs));
      if (st.mode.importFail) return send(res, 500, { code: 'internal_error', message: 'boom' });
      for (const c of j.jsonBody || []) for (const l of j.listIds || []) (st.lists[l] ??= new Set()).add(c.email);
      return send(res, 202, { processId: ++st.nextId });
    }
    if (req.method === 'GET' && (m = path.match(/^processes\/(\d+)$/))) return send(res, 200, { id: Number(m[1]), status: st.mode.processStatus });
    if (req.method === 'GET' && (m = path.match(/^contacts\/lists\/(\d+)\/contacts$/))) {
      const all = [...(st.lists[m[1]] || [])].sort(); const off = Number(u.searchParams.get('offset') || 0), lim = Number(u.searchParams.get('limit') || 50);
      return send(res, 200, { contacts: all.slice(off, off + lim).map((email) => ({ email, emailBlacklisted: false })), count: all.length });
    }
    if (req.method === 'GET' && (m = path.match(/^contacts\/lists\/(\d+)$/))) return send(res, 200, { id: Number(m[1]), totalSubscribers: (st.lists[m[1]] || new Set()).size });
    if ((m = path.match(/^contacts\/([^/]+)$/))) {
      const email = decodeURIComponent(m[1]);
      const inL = Object.entries(st.lists).filter(([, s]) => s.has(email)).map(([k]) => Number(k));
      if (req.method === 'GET') return inL.length ? send(res, 200, { email, emailBlacklisted: false, listIds: inL, listUnsubscribed: [] }) : send(res, 404, { code: 'document_not_found' });
      if (req.method === 'DELETE') { for (const s of Object.values(st.lists)) s.delete(email); return inL.length ? send(res, 204) : send(res, 404, { code: 'document_not_found' }); }
    }
    if (req.method === 'POST' && path === 'emailCampaigns') {
      if (st.mode.createFail === '500') return send(res, 500, { code: 'internal_error' });
      if (st.mode.createFail === 'badbody') return send(res, 201, { nope: 1 });
      const id = ++st.nextId; st.camps[id] = { ...j, id }; return send(res, 201, { id }); }
    if (req.method === 'POST' && (m = path.match(/^emailCampaigns\/(\d+)\/sendNow$/))) {
      while (st.mode.sendHold) await new Promise((r) => setTimeout(r, 100));
      if (st.mode.sendDelayMs) await new Promise((r) => setTimeout(r, st.mode.sendDelayMs));
      if (st.mode.sendNow === '500') return send(res, 500, { code: 'internal_error' });
      if (st.mode.sendNow === '400') return send(res, 400, { code: 'campaign_sent', message: 'already sent' });
      return send(res, 204);
    }
    if (req.method === 'POST' && (m = path.match(/^emailCampaigns\/(\d+)\/sendTest$/))) return st.mode.sendTest === '500' ? send(res, 500, { code: 'internal_error' }) : send(res, 204);
    if (req.method === 'GET' && (m = path.match(/^emailCampaigns\/(\d+)$/))) {
      if (st.mode.getCamp === '500') return send(res, 500, { code: 'internal_error' });
      if (st.mode.getCamp === '404') return send(res, 404, { code: 'document_not_found' });
      if (st.mode.getCampDelayMs) await new Promise((r) => setTimeout(r, st.mode.getCampDelayMs));
      const s = st.mode.stats || { sent: 3, delivered: 3, uniqueViews: 2, uniqueClicks: 1, hardBounces: 0, softBounces: 1, complaints: 0, unsubscriptions: 1 };
      return send(res, 200, { id: Number(m[1]), status: st.mode.campStatus, statistics: { globalStats: s } });
    }
    if (req.method === 'DELETE' && path.startsWith('emailCampaigns/')) return send(res, 204);
    return send(res, 404, { code: 'not_found', path });
  });
};
// Loopback plus the docker bridge gateway (FAKE_BIND_ADDRS, comma separated, set by run-phase.sh): the container reaches the
// gateway address, the rest of the network does not. Never 0.0.0.0, because /__qa/* is open.
const ADDRS = ['127.0.0.1', ...(process.env.FAKE_BIND_ADDRS || '').split(',').map((a) => a.trim()).filter((a) => a && a !== '0.0.0.0')];
for (const addr of new Set(ADDRS)) http.createServer(handler).listen(PORT, addr, () => console.log('fake brevo on', addr, PORT));

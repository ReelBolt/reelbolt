// The fake `/api/**`. `respond(method, pathname, search, ctx)` returns
//   { status, json }            a JSON answer,
//   { media: <clip index> }     a video (served with Range support by the harness),
//   { image: 'picture-0.png' }  a fixture picture,
//   { sse: [{event,data}] }     the first answer of an SSE stream (later calls are held open),
//   null                        unmatched (the harness logs it and answers 404 {}).
// `ctx.scenario` is the shot's optional "scenario" (a variation of the data, e.g. the Trial plan).
// To fake more of the API, add a branch here (data goes in data.mjs). Keep answers fictional.
import * as d from './data.mjs';

const json = (body, status = 200) => ({ status, json: body });
const OUTPUT_IDS = ['r4', 'sr-out-1', 'sr-out-2', 'sr-out-3', 'sr-out-4', 'sr-out-5', 'sr-out-6', 'sr-spring-1'];
const clipFor = (id) => ({ media: Math.max(0, OUTPUT_IDS.indexOf(id)) % d.MEDIA_CLIPS });
const allProjects = [...d.projects, d.springProject];

export function respond(method, pathname, search, ctx = {}) {
  const qs = new URLSearchParams(search);
  const p = pathname;
  const scenario = ctx.scenario;
  if (method === 'POST' && p === '/api/v1/assistant/threads') return json({ id: 'th-1' });
  if (method !== 'GET') return json({ ok: true });

  if (p === '/api/v1/orgs') return json(d.orgs);
  if (p === `/api/v1/orgs/${d.ORG_ID}/members`) return json(d.members);
  if (p === '/api/v1/billing/summary') return json(scenario === 'trial' ? d.trialSummary : d.billingSummary);
  if (p === '/api/v1/billing/usage') {
    const usage = d.usageFor(qs);
    return json(qs.get('groupBy') === 'kind' ? { ...usage, groups: d.usageByKind } : usage);
  }
  if (p === '/api/v1/billing/promotions/active') return json({ items: [] });
  if (p === '/api/v1/projects') return json(scenario === 'two-projects' ? d.projects.slice(0, 2) : d.projects);
  if (p === '/api/v1/dashboard/overview') return json(d.dashboardOverview);
  if (p === '/api/v1/inference-providers/selectable') return json(d.selectableProviders);
  if (p === '/api/v1/inference-providers/status') return json({ capability: 'SpeechSynthesis', providerName: 'Studio voices', lastTestOk: true, lastTestAt: d.ago(30), healthy: true, usingFallback: false, message: null });
  if (p === '/api/v1/assistant/activity') return json({ writing: [], unread: [], pendingWatches: 0 });
  if (p === '/api/v1/assistant/preferences') return json({ approvalMode: 'ask' });
  if (p === '/api/v1/assistant/threads') return json([]);
  if (p === '/api/v1/assistant/threads/th-1') return json({ id: 'th-1', title: 'New chat', projectId: null, messages: [] });
  if (p === '/api/v1/workflows/events') return { sse: [{ event: 'connected', data: {} }] };
  if (p === '/api/v1/storage/usage') {
    return json({ usedBytes: 46_000_000_000, limitBytes: 50 * 1024 ** 3, byProject: d.projects.slice(0, 5).map((pr, i) => ({ projectId: pr.id, name: pr.name, usedBytes: [18, 15, 6, 4, 2][i] * 1_000_000_000 })) });
  }
  if (p === '/api/v1/marketing/consent') return json({ status: 'none', wordingVersion: null, consentedAt: null, confirmedAt: null, withdrawnAt: null });
  if (p === '/api/v1/marketing/wording') return json({ version: '2026-01', text: 'Send me product news and offers by email. I can withdraw at any time.' });
  if (p === '/api/v1/agents') return json(d.agents);
  if (p === '/api/v1/runners') return json(scenario === 'one-runner' ? d.oneRunner : d.runners);
  if (p === '/api/v1/org/runner-llm') return json({ devices: [] });
  if (/^\/api\/v1\/runners\/pair\/[^/]+$/.test(p)) return json(d.pairing);
  if (p === '/api/v1/cloud-storage/providers') return json([{ provider: 'google-drive', displayName: 'Google Drive', configured: true }, { provider: 'onedrive', displayName: 'OneDrive', configured: true }, { provider: 'dropbox', displayName: 'Dropbox', configured: true }]);
  if (p === '/api/v1/cloud-storage/connections') return json([]);
  if (p === '/api/v1/org/agent-overrides' || p === '/api/v1/org/agent-models') return json([]);
  if (p === '/api/v1/editor/preferences') return json({});

  const proj = /^\/api\/v1\/projects\/([^/]+)(\/.*)?$/.exec(p);
  if (proj) {
    const [, pid, rest = ''] = proj;
    const found = allProjects.find((x) => x.id === pid);
    if (rest === '' && found) return json(found);
    const dl = /^\/outputs\/([^/]+)\/download$/.exec(rest);
    if (dl) return clipFor(dl[1]);

    if (pid === d.SPRING) {
      if (rest === '/files') return json(d.springFiles);
      if (rest === '/outputs') return json(d.springOutputs);
      if (rest === '/workflows') return json([]);
    }
    if (pid === d.P) {
      if (rest === '/files') return json(d.files);
      if (rest === '/outputs') return json(d.outputs);
      if (rest === '/workflows') return json(d.workflows);
      if (rest === '/workflows/templates') return json(d.templates);
      if (rest === '/workflow-apps') return json(d.apps);
      if (rest === `/workflows/${d.PROMO}`) return json(d.promoWorkflow);
      if (rest === `/workflows/${d.PROMO}/app`) return json(d.promoManifest);
      if (rest === `/workflows/${d.PROMO}/executions`) return json([]);
      if (rest === `/workflows/${d.PROMO}/estimate`) return json(d.estimate);
      if (rest === `/workflows/${d.PROMO}/lint`) return json({ findings: [], errors: 0, warnings: 0, infos: 0 });
      if (rest === `/workflows/${d.W}`) return json(d.workflows[0]);
      if (rest === `/workflows/${d.W}/app`) return json(d.appManifest);
      // "ready": no run yet, so the app page shows its form and the Run button.
      if (rest === `/workflows/${d.W}/executions`) return json(scenario === 'ready' ? [] : d.executionList);
      if (rest === `/workflows/${d.W}/estimate`) return json(d.estimate);
      if (rest === `/workflows/${d.W}/lint`) return json({ findings: [], errors: 0, warnings: 0, infos: 0 });
      const ex = /^\/executions\/([^/]+)$/.exec(rest);
      if (ex && d.executions[ex[1]]) return json(d.executions[ex[1]]);
      const exs = /^\/executions\/([^/]+)\/(change-requests|change-capabilities|revisions)$/.exec(rest);
      if (exs) {
        const finished = exs[1] !== d.LIVE_EXEC;
        if (exs[2] === 'change-requests') return json({ requests: [] });
        if (exs[2] === 'change-capabilities') return json({ executionId: exs[1], runFinished: finished, steps: [] });
        // The real API answers 404 while a run has no finished video to revise.
        return finished ? json(d.revisionHistory) : json({}, 404);
      }
      const sres = new RegExp(`^/executions/${d.LIVE_EXEC}/step-results/(.+)$`).exec(rest);
      if (sres) {
        const hit = d.liveResults.find((r) => r.id === sres[1]);
        return hit ? json(hit) : json({}, 404);
      }
      // The editor: timelines, the media pool, pictures.
      if (rest === '/edit-timelines') return json(qs.get('state') === 'deleted' ? [] : [d.timelineSummary]);
      if (rest === `/edit-timelines/${d.TL}`) return json({ ...d.timelineSummary, projectId: d.P, document: d.timelineDoc, canEdit: true });
      if (rest === `/edit-timelines/${d.TL}/versions`) return json({ versions: [] });
      if (rest === `/edit-timelines/${d.TL}/comments`) return json([]);
      if (rest === `/edit-timelines/${d.TL}/renders`) return json([]);
      if (rest === `/edit-timelines/${d.TL}/review-links`) return json([]);
      const dv = /^\/media\/([^/]+)\/derivatives$/.exec(rest);
      if (dv) return json(d.derivativesOf(decodeURIComponent(dv[1])));
      const dc = /^\/media\/derivatives\/dv-(\d)\/content$/.exec(rest);
      if (dc) return { media: (Number(dc[1]) - 1) % d.MEDIA_CLIPS };
      if (/^\/media\/[^/]+\/filmstrip$/.test(rest)) return json({ filmstrip: { densities: [] } });
      if (/^\/image-documents\/[^/]+\/exports$/.test(rest)) return json([]);
      if (/^\/image-documents\/[^/]+\/graphics$/.test(rest)) return json({ graphics: [], estimate: null, available: false, unavailableReason: null });
      if (rest === '/timelines') return json(d.timelineRenders);
      const fm = /^\/files\/c1a0000(\d)-[^/]+\/media$/.exec(rest);
      if (fm) return { media: (Number(fm[1]) - 1) % d.MEDIA_CLIPS };
      if (rest === '/media') return json(d.mediaPool);
      if (rest === '/media/bins') return json([]);
      if (rest === '/media/annotations') return json([]);
      if (rest === '/image-documents') return json(d.pictureDocs);
      if (rest === '/image-documents/pd-1') return json(d.pictureDoc);
      if (/^\/image-documents\/pd-1\/layers\/[^/]+\/image$/.test(rest)) return { image: 'picture-0.png' };
      if (['/voices', '/folder-links', '/cloud-links'].includes(rest)) return json([]);
      if (rest === '/pronunciation') return json({ entries: [], max: 200 });
      if (rest === '/github') return json({}, 404);
      if (rest === '/design-system') return json({ design: null, productMap: null });
    }
    // Any other project: it exists but is quiet.
    if (found) {
      if (['/files', '/outputs', '/workflows', '/voices', '/folder-links', '/cloud-links', '/image-documents', '/media', '/media/bins', '/timelines', '/edit-timelines'].includes(rest)) return json([]);
      if (rest === '/workflows/templates') return json(d.templates);
      if (rest === '/workflow-apps') return json({ apps: [], recipes: d.apps.recipes });
      if (rest === '/pronunciation') return json({ entries: [], max: 200 });
      if (rest === '/github') return json({}, 404);
      if (rest === '/design-system') return json({ design: null, productMap: null });
    }
  }

  if (new RegExp(`^/api/v1/workflows/executions/${d.LIVE_EXEC}/events$`).test(p)) return { sse: d.liveEvents };
  if (/^\/api\/v1\/workflows\/executions\/[^/]+\/events$/.test(p)) return { sse: [{ event: 'connected', data: {} }] };
  return null;
}

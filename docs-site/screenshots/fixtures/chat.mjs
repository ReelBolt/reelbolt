// Canned assistant replies, as UI-message-stream frames. `hold: true` leaves the reply running, which is
// how a permission card stays waiting for its OK. Everything is fictional.
const text = (id, delta) => [
  { type: 'text-start', id },
  { type: 'text-delta', id, delta },
  { type: 'text-end', id },
];

const SCENARIOS = {
  // assistant-basics-01 and -03: a plain question and answer.
  'which-footage': {
    hold: false,
    frames: text('t1',
      'Start with the two drone shots and the ridgeline run: they are steady and well lit. ' +
      'The creek crossing is shaky in the first half, so I would trim it or leave it out.'),
  },
  // assistant-basics-03: the same question on a phone, a shorter answer so the whole exchange fits the sheet.
  'which-footage-short': {
    hold: false,
    frames: text('t1', 'Start with the two drone shots and the ridgeline run. They are steady and well lit.'),
  },
  // assistant-basics-02: a change that needs an OK.
  'create-project': {
    hold: true,
    frames: [
      ...text('t1', 'I can set that up. Check the card below and press Allow.'),
      {
        type: 'data-assistantAction',
        data: {
          id: 'act-1', state: 'pending', mode: 'ask', kind: 'project', risk: 'normal',
          title: 'Create a project called Spring Teaser',
          summary: 'A new, empty project in Northwind Studio.',
          changes: [{ kind: 'add', text: 'Project: Spring Teaser' }],
        },
      },
    ],
  },
};

export function chatScenario(name) {
  const s = SCENARIOS[name];
  if (!s) throw new Error(`unknown chat scenario "${name}" (have ${Object.keys(SCENARIOS).join(', ')})`);
  return s;
}

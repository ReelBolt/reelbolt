// Local ESLint rule: no string literal as the title/message of a notification in migrated areas.
// (eslint-plugin-i18next's no-literal-string only looks inside JSX.)
const NOTIFY = new Set(['show', 'update']);
const FIELDS = new Set(['title', 'message']);

const isLiteralText = (node) =>
  (node.type === 'Literal' && typeof node.value === 'string' && /\p{L}/u.test(node.value)) ||
  (node.type === 'TemplateLiteral' && node.quasis.some((q) => /\p{L}/u.test(q.value.raw)));

const rule = {
  meta: {
    type: 'problem',
    schema: [],
    messages: { literal: 'Notification {{field}} is a hard-coded string. Use useT()/getT() (see docs/i18n.md).' },
  },
  create(context) {
    return {
      CallExpression(node) {
        const c = node.callee;
        if (c.type !== 'MemberExpression' || c.object.type !== 'Identifier' || c.object.name !== 'notifications') return;
        if (c.property.type !== 'Identifier' || !NOTIFY.has(c.property.name)) return;
        const arg = node.arguments[0];
        if (!arg || arg.type !== 'ObjectExpression') return;
        for (const p of arg.properties) {
          if (p.type !== 'Property' || p.computed) continue;
          const name = p.key.type === 'Identifier' ? p.key.name : p.key.value;
          if (FIELDS.has(name) && isLiteralText(p.value)) {
            context.report({ node: p.value, messageId: 'literal', data: { field: name } });
          }
        }
      },
    };
  },
};

export default { rules: { 'no-literal-notification': rule } };

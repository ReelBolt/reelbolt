'use client';

import { ActionIcon, CopyButton, Tooltip } from '@mantine/core';
import { IconCheck, IconCopy } from '@tabler/icons-react';
import { MarkdownTextPrimitive, type CodeHeaderProps } from '@assistant-ui/react-markdown';
import { useT } from '@/i18n';
import remarkGfm from 'remark-gfm';
import classes from './AssistantChatPanel.module.css';

function CodeHeader({ language, code }: CodeHeaderProps) {
  const t = useT();
  return (
    <div className={classes.codeHeader}>
      <span>{language || 'text'}</span>
      <CopyButton value={code} timeout={1500}>
        {({ copied, copy }) => (
          <Tooltip label={t(copied ? 'assistant.code.copied' : 'assistant.code.copy')} withArrow position="left">
            <ActionIcon
              variant="subtle"
              color="gray"
              size="sm"
              onClick={copy}
              aria-label={t(copied ? 'assistant.code.copied' : 'assistant.code.copy')}
            >
              {copied ? <IconCheck size={14} /> : <IconCopy size={14} />}
            </ActionIcon>
          </Tooltip>
        )}
      </CopyButton>
    </div>
  );
}

/**
 * Markdown image: never rendered as an <img>, whatever the source. Any loaded image is a request made
 * without a click: to a remote host it can carry data out (`![](https://host/?d=...)`); to our own
 * origin it carries the user's cookies (e.g. an injected `/oauth/authorize?...` URL). An absolute
 * http(s) source becomes a plain link the user may choose to open; anything else is just its alt text.
 */
function MarkdownImage({ node, src, alt }: React.ComponentProps<'img'> & { node?: unknown }) {
  void node; // react-markdown's hast node — not a DOM attribute
  const label = alt || (typeof src === 'string' ? src : '');
  if (typeof src === 'string' && /^https?:\/\//i.test(src.trim())) {
    return (
      <a href={src} target="_blank" rel="noopener noreferrer">
        {label}
      </a>
    );
  }
  return <>{label}</>;
}

export const markdownComponents = {
  CodeHeader,
  // No markdown image is ever fetched automatically (see MarkdownImage).
  img: MarkdownImage,
  // Links the model writes open in a new tab: the assistant lives in a drawer over unsaved work.
  a: ({ node, ...props }: React.ComponentProps<'a'> & { node?: unknown }) => {
    void node; // react-markdown's hast node — not a DOM attribute
    return <a {...props} target="_blank" rel="noopener noreferrer" />;
  },
};

/** Renders an assistant text part as GitHub-flavoured markdown (lists, tables, code blocks). */
export function MarkdownText() {
  return <MarkdownTextPrimitive remarkPlugins={[remarkGfm]} className={classes.markdown} components={markdownComponents} />;
}

'use client';

import { ActionIcon, Badge, Card, Group, Menu, Stack, Table, Text, VisuallyHidden } from '@mantine/core';
import {
  IconArchive,
  IconArchiveOff,
  IconArrowBarToDown,
  IconCopy,
  IconDots,
  IconLink,
  IconPencil,
} from '@tabler/icons-react';
import { useFormat, useT, type Format, type T } from '@/i18n';
import type { FunnelAccountSteps, MarketingLink, MarketingLinkFunnelItem } from '@/lib/types/marketing-links';
import {
  ACCOUNT_STEPS,
  STEP_LABELS,
  STOP_LABELS,
  biggestDrop,
  channelLabel,
  costFormatOptions,
  costPerSignupMinor,
  fromMinorUnits,
  funnelCells,
  mediumLabel,
  personaLabel,
  type FunnelCell,
  type FunnelTotals,
} from './link-model';
import classes from './links.module.css';

export interface LinkRow {
  link: MarketingLink;
  /** Null when the funnel response carries no row for this link (or could not be read). */
  funnel: MarketingLinkFunnelItem | null;
  /**
   * The period shown reaches back to the day the link was created, so its signups in the period
   * are all of its signups and cost per signup can be worked out. Left out means it does.
   */
  coversLife?: boolean;
}

interface LinksFunnelTableProps {
  rows: readonly LinkRow[];
  /** The totals of `rows`, or null while there are no funnel numbers. */
  totals: FunnelTotals | null;
  /** The accounts of the period that carry no link, or null while there are no funnel numbers. */
  unattributed: FunnelAccountSteps | null;
  /** Phone layout: one card per link with the funnel as a list, instead of the wide table. */
  compact: boolean;
  onCopy: (link: MarketingLink, kind: 'short' | 'long') => void;
  onEdit: (link: MarketingLink) => void;
  onToggleArchive: (link: MarketingLink) => void;
}

/**
 * A body row's header cell. The theme styles every `th` as a column heading (mono, upper case,
 * dimmed) through inline styles; a link's code is case-sensitive and its note is prose, so the row
 * header takes the body's own type back. Inline, because only a `style` outranks the theme's.
 */
const ROW_HEADER_STYLE: React.CSSProperties = {
  fontFamily: 'inherit',
  fontSize: 'var(--mantine-font-size-sm)',
  fontWeight: 400,
  letterSpacing: 'normal',
  textTransform: 'none',
  color: 'inherit',
};

/** A share as a percentage: one decimal under 10 %, so "0.4%" does not read as "0%". */
function percent(f: Format, value: number): string {
  return f.percent(value, { maximumFractionDigits: value > 0 && value < 0.1 ? 1 : 0 });
}

/** An amount stored as minor units, shown with the same fixed decimals it was written with. */
function money(f: Format, minor: number, currency: string): string {
  return f.currency(fromMinorUnits(minor, currency), currency, costFormatOptions(currency));
}

/** "—" for the eye, a sentence for a screen reader. */
function Missing({ label }: { label: string }) {
  return (
    <>
      <span aria-hidden="true">—</span>
      <VisuallyHidden>{label}</VisuallyHidden>
    </>
  );
}

function cellLabel(cell: FunnelCell, t: T, tr: T): string {
  if (cell.key === 'clicks') return t('clicks');
  if (cell.key === 'visitors') return t('visitors');
  return tr.msg(STEP_LABELS[cell.key]);
}

/** The count, its share and its bar: the inside of a table cell and of a phone list line alike. */
function CellFigure({ cell, botHits }: { cell: FunnelCell; botHits?: number }) {
  const t = useT('adminLinks.table');
  const f = useFormat();
  if (cell.count === null) return null;
  return (
    <>
      <span className={classes.count}>{f.number(cell.count)}</span>
      {cell.share !== null && cell.shareOf ? (
        <div className={classes.sub}>
          {percent(f, cell.share)}
          <VisuallyHidden> {cell.shareOf === 'clicks' ? t('ofClicks') : t('ofPrevious')}</VisuallyHidden>
        </div>
      ) : null}
      {cell.key === 'clicks' && botHits ? <div className={classes.sub}>{t('botHits', { count: f.number(botHits) })}</div> : null}
      {cell.bar !== null ? (
        <div className={classes.bar} aria-hidden="true">
          <div
            className={cell.marked ? `${classes.barFill} ${classes.barFillMarked}` : classes.barFill}
            style={{ width: `${Math.round(cell.bar * 100)}%` }}
          />
        </div>
      ) : null}
      {cell.marked ? <VisuallyHidden>{t('stopMark')}</VisuallyHidden> : null}
    </>
  );
}

function FunnelTds({ cells, botHits, missing }: { cells: FunnelCell[]; botHits?: number; missing: string }) {
  return (
    <>
      {cells.map((cell) => (
        <Table.Td
          key={cell.key}
          className={cell.marked ? `${classes.numberCell} ${classes.marked}` : classes.numberCell}
          data-testid={`cell-${cell.key}`}
          data-marked={cell.marked ? 'true' : undefined}
        >
          {cell.count === null ? <Missing label={missing} /> : <CellFigure cell={cell} botHits={botHits} />}
        </Table.Td>
      ))}
    </>
  );
}

/** The sentence under a link: where most of its accounts stop, and how many that is. */
function StopLine({ steps }: { steps: FunnelAccountSteps | null }) {
  const t = useT('adminLinks.table');
  const tr = useT();
  const f = useFormat();
  const drop = steps ? biggestDrop(steps) : null;
  if (!drop) return null;
  return (
    <Group gap={6} wrap="nowrap" align="flex-start" mt={4} data-testid="stop-line">
      <IconArrowBarToDown size={14} color="var(--mantine-color-orange-6)" aria-hidden="true" style={{ flexShrink: 0, marginTop: 2 }} />
      <Text size="xs" fw={600}>
        {tr.msg(STOP_LABELS[drop.step])}
        <span className={classes.sub}> · {t('stopLost', { lost: f.number(drop.lost), of: f.number(drop.of) })}</span>
      </Text>
    </Group>
  );
}

/** Who the link is: code, placement, channel, campaign, landing page, audience. */
function LinkIdentity({ link }: { link: MarketingLink }) {
  const t = useT('adminLinks.table');
  const tr = useT();
  const channel = channelLabel(link.channel);
  const medium = mediumLabel(link.medium);
  const persona = link.persona ? personaLabel(link.persona) : null;
  return (
    <Stack gap={2}>
      <Group gap="xs" wrap="wrap">
        <span className={classes.code}>{link.code}</span>
        {link.archivedAt ? (
          <Badge size="sm" variant="outline" color="gray">
            {t('archived')}
          </Badge>
        ) : null}
      </Group>
      <Text size="sm" style={{ wordBreak: 'break-word' }}>
        {link.placement}
      </Text>
      <div className={classes.sub}>
        {t('channel', {
          channel: channel ? tr.msg(channel) : link.channel,
          medium: medium ? tr.msg(medium) : link.medium,
        })}
      </div>
      <div className={classes.sub}>{t('campaign', { campaign: link.campaign })}</div>
      <div className={classes.sub} style={{ wordBreak: 'break-all' }}>
        {t('landing', { path: link.destinationPath })}
      </div>
      {link.persona ? (
        <div className={classes.sub}>{t('persona', { persona: persona ? tr.msg(persona) : link.persona })}</div>
      ) : null}
    </Stack>
  );
}

function RowMenu({
  link,
  onCopy,
  onEdit,
  onToggleArchive,
  size,
}: Pick<LinksFunnelTableProps, 'onCopy' | 'onEdit' | 'onToggleArchive'> & {
  link: MarketingLink;
  /** 44 on a phone (a touch target), 36 in the table. */
  size: number;
}) {
  const t = useT('adminLinks');
  return (
    <Menu position="bottom-end" withinPortal>
      <Menu.Target>
        <ActionIcon
          variant="default"
          size={size}
          aria-label={t('table.actionsFor', { code: link.code })}
          data-testid={`link-actions-${link.code}`}
        >
          <IconDots size={18} />
        </ActionIcon>
      </Menu.Target>
      <Menu.Dropdown>
        <Menu.Item leftSection={<IconLink size={16} />} onClick={() => onCopy(link, 'short')}>
          {t('actions.copyShort')}
        </Menu.Item>
        <Menu.Item leftSection={<IconCopy size={16} />} onClick={() => onCopy(link, 'long')}>
          {t('actions.copyLong')}
        </Menu.Item>
        <Menu.Item leftSection={<IconPencil size={16} />} onClick={() => onEdit(link)}>
          {t('actions.edit')}
        </Menu.Item>
        <Menu.Divider />
        <Menu.Item
          leftSection={link.archivedAt ? <IconArchiveOff size={16} /> : <IconArchive size={16} />}
          onClick={() => onToggleArchive(link)}
        >
          {link.archivedAt ? t('actions.unarchive') : t('actions.archive')}
        </Menu.Item>
      </Menu.Dropdown>
    </Menu>
  );
}

/**
 * What a link's cost columns show: the link's own cost, and that cost over all of its signups.
 * `perSignup` is null when it cannot be worked out; `periodTooShort` says the reason is the period
 * (the link is older than the period shown), which the cell then says instead of a bare dash.
 */
function linkCost(row: LinkRow): { minor: number; perSignup: number | null; periodTooShort: boolean; currency: string } | null {
  const { costMinor, costCurrency } = row.link;
  if (costMinor === null || !costCurrency) return null;
  const coversLife = row.coversLife !== false;
  return {
    minor: costMinor,
    perSignup: row.funnel ? costPerSignupMinor(costMinor, row.funnel.signups, coversLife) : null,
    periodTooShort: !coversLife && row.funnel !== null && row.funnel.signups > 0,
    currency: costCurrency,
  };
}

const cellsOfRow = (row: LinkRow): FunnelCell[] =>
  funnelCells(
    row.funnel
      ? { clicks: row.funnel.clicks, visitors: row.funnel.visitors, steps: row.funnel }
      : { clicks: null, visitors: null, steps: null },
  );

/**
 * Every link with its funnel, then the totals of the links shown and the accounts that carry no link.
 *
 * The header groups the nine figures by who they count (everyone, people who accepted cookies,
 * accounts), because the three are different populations and must not be read as one funnel.
 */
export function LinksFunnelTable(props: LinksFunnelTableProps) {
  const { rows, totals, unattributed, compact } = props;
  const t = useT('adminLinks.table');
  const tr = useT();
  const f = useFormat();

  const totalCells = totals ? funnelCells({ clicks: totals.clicks, visitors: totals.visitors, steps: totals }) : null;
  const unattributedCells = unattributed ? funnelCells({ clicks: null, visitors: null, steps: unattributed }) : null;
  const totalCost = totals && totals.costs.length > 0 ? totals.costs.map((cost) => money(f, cost.minor, cost.currency)).join(' + ') : null;

  if (compact) {
    const list = (cells: FunnelCell[], botHits: number | undefined, missing: string) => (
      <Stack gap={2} component="ul" style={{ listStyle: 'none', margin: 0, padding: 0 }}>
        {cells.map((cell) => (
          <li
            key={cell.key}
            className={cell.marked ? `${classes.stepRow} ${classes.marked}` : classes.stepRow}
            data-testid={`cell-${cell.key}`}
            data-marked={cell.marked ? 'true' : undefined}
          >
            <Group justify="space-between" align="flex-start" wrap="nowrap" gap="sm">
              <Text size="sm">{cellLabel(cell, t, tr)}</Text>
              <div style={{ textAlign: 'right', minWidth: 96 }}>
                {cell.count === null ? <Missing label={missing} /> : <CellFigure cell={cell} botHits={botHits} />}
              </div>
            </Group>
          </li>
        ))}
      </Stack>
    );
    return (
      <Stack gap="md" data-testid="links-cards">
        {rows.map((row) => {
          const cost = linkCost(row);
          return (
            <Card key={row.link.id} withBorder radius="md" padding="md" component="article" data-testid={`link-row-${row.link.code}`}>
              <Group justify="space-between" align="flex-start" wrap="nowrap" mb="sm">
                <div style={{ minWidth: 0 }}>
                  <LinkIdentity link={row.link} />
                  <StopLine steps={row.funnel} />
                </div>
                <RowMenu size={44} link={row.link} onCopy={props.onCopy} onEdit={props.onEdit} onToggleArchive={props.onToggleArchive} />
              </Group>
              {list(cellsOfRow(row), row.funnel?.botHits, t('noFigure'))}
              {cost ? (
                <Group justify="space-between" mt="sm" gap="sm">
                  <Text size="sm">
                    {t('cost')}: {money(f, cost.minor, cost.currency)}
                  </Text>
                  {cost.perSignup !== null ? (
                    <Text size="sm">
                      {t('costPerSignup')}: {money(f, cost.perSignup, cost.currency)}
                    </Text>
                  ) : cost.periodTooShort ? (
                    <Text size="sm" className={classes.sub} data-testid="cost-per-signup-period">
                      {t('costPerSignup')}: {t('periodTooShort')}
                    </Text>
                  ) : null}
                </Group>
              ) : null}
            </Card>
          );
        })}
        {totals && totalCells ? (
          <Card withBorder radius="md" padding="md" component="article" data-testid="link-row-total">
            <Text fw={600} mb="xs">
              {t('total', { count: totals.links })}
            </Text>
            <StopLine steps={totals} />
            {list(totalCells, totals.botHits, t('noFigure'))}
            {totalCost ? (
              <Group justify="space-between" mt="sm" gap="sm">
                <Text size="sm">
                  {t('cost')}: {totalCost}
                </Text>
                {totals.costPerSignup ? (
                  <Text size="sm">
                    {t('costPerSignup')}: {money(f, totals.costPerSignup.minor, totals.costPerSignup.currency)}{' '}
                    <span className={classes.sub}>({t('costedLinksOnly')})</span>
                  </Text>
                ) : null}
              </Group>
            ) : null}
          </Card>
        ) : null}
        {unattributed && unattributedCells ? (
          <Card withBorder radius="md" padding="md" component="article" data-testid="link-row-unattributed">
            <Text fw={600}>{t('unattributed')}</Text>
            <div className={classes.sub}>{t('unattributedHint')}</div>
            <StopLine steps={unattributed} />
            {list(
              unattributedCells.filter((cell) => cell.key !== 'clicks' && cell.key !== 'visitors'),
              undefined,
              t('notApplicable'),
            )}
          </Card>
        ) : null}
      </Stack>
    );
  }

  return (
    <Table.ScrollContainer minWidth={1060}>
      <Table withTableBorder withColumnBorders verticalSpacing="sm" aria-label={t('caption')} data-testid="links-table">
        <Table.Thead>
          <Table.Tr>
            <Table.Th scope="col" rowSpan={2} className={classes.linkCell}>
              {t('link')}
            </Table.Th>
            <Table.Th scope="colgroup" ta="center">
              {t('groupEveryone')}
            </Table.Th>
            <Table.Th scope="colgroup" ta="center">
              {t('groupConsented')}
            </Table.Th>
            <Table.Th scope="colgroup" colSpan={ACCOUNT_STEPS.length} ta="center">
              {t('groupAccounts')}
            </Table.Th>
            <Table.Th scope="col" rowSpan={2} ta="right">
              {t('cost')}
            </Table.Th>
            <Table.Th scope="col" rowSpan={2} ta="right">
              {t('costPerSignup')}
            </Table.Th>
            <Table.Th scope="col" rowSpan={2} w={60}>
              <VisuallyHidden>{t('actions')}</VisuallyHidden>
            </Table.Th>
          </Table.Tr>
          <Table.Tr>
            <Table.Th scope="col" ta="right">
              {t('clicks')}
            </Table.Th>
            <Table.Th scope="col" ta="right">
              {t('visitors')}
            </Table.Th>
            {ACCOUNT_STEPS.map((step) => (
              <Table.Th key={step} scope="col" ta="right" style={{ whiteSpace: 'normal' }}>
                {tr.msg(STEP_LABELS[step])}
              </Table.Th>
            ))}
          </Table.Tr>
        </Table.Thead>
        <Table.Tbody>
          {rows.map((row) => {
            const cost = linkCost(row);
            return (
              <Table.Tr key={row.link.id} data-testid={`link-row-${row.link.code}`}>
                <Table.Th scope="row" className={classes.linkCell} style={ROW_HEADER_STYLE}>
                  <LinkIdentity link={row.link} />
                  <StopLine steps={row.funnel} />
                </Table.Th>
                <FunnelTds cells={cellsOfRow(row)} botHits={row.funnel?.botHits} missing={t('noFigure')} />
                <Table.Td className={classes.numberCell} data-testid="cell-cost">
                  {cost ? money(f, cost.minor, cost.currency) : <Missing label={t('notApplicable')} />}
                </Table.Td>
                <Table.Td className={classes.numberCell} data-testid="cell-cost-per-signup">
                  {cost && cost.perSignup !== null ? (
                    money(f, cost.perSignup, cost.currency)
                  ) : cost?.periodTooShort ? (
                    <span className={classes.sub} data-testid="cost-per-signup-period">{t('periodTooShort')}</span>
                  ) : (
                    <Missing label={t('notApplicable')} />
                  )}
                </Table.Td>
                <Table.Td style={{ verticalAlign: 'top' }}>
                  <RowMenu size={36} link={row.link} onCopy={props.onCopy} onEdit={props.onEdit} onToggleArchive={props.onToggleArchive} />
                </Table.Td>
              </Table.Tr>
            );
          })}
        </Table.Tbody>
        {totalCells || unattributedCells ? (
          <Table.Tfoot>
            {totals && totalCells ? (
              <Table.Tr className={classes.footRow} data-testid="link-row-total">
                <Table.Th scope="row" className={classes.linkCell} style={ROW_HEADER_STYLE}>
                  <Text size="sm" fw={600}>
                    {t('total', { count: totals.links })}
                  </Text>
                  <StopLine steps={totals} />
                </Table.Th>
                <FunnelTds cells={totalCells} botHits={totals.botHits} missing={t('noFigure')} />
                <Table.Td className={classes.numberCell} data-testid="cell-cost">
                  {totalCost ?? <Missing label={t('notApplicable')} />}
                </Table.Td>
                <Table.Td className={classes.numberCell} data-testid="cell-cost-per-signup">
                  {totals.costPerSignup ? (
                    <>
                      {money(f, totals.costPerSignup.minor, totals.costPerSignup.currency)}
                      <div className={classes.sub}>{t('costedLinksOnly')}</div>
                    </>
                  ) : (
                    <Missing label={t('notApplicable')} />
                  )}
                </Table.Td>
                <Table.Td />
              </Table.Tr>
            ) : null}
            {unattributed && unattributedCells ? (
              <Table.Tr className={totals ? undefined : classes.footRow} data-testid="link-row-unattributed">
                <Table.Th scope="row" className={classes.linkCell} style={ROW_HEADER_STYLE}>
                  <Text size="sm" fw={600}>
                    {t('unattributed')}
                  </Text>
                  <div className={classes.sub}>{t('unattributedHint')}</div>
                  <StopLine steps={unattributed} />
                </Table.Th>
                <FunnelTds cells={unattributedCells} missing={t('notApplicable')} />
                <Table.Td className={classes.numberCell}>
                  <Missing label={t('notApplicable')} />
                </Table.Td>
                <Table.Td className={classes.numberCell}>
                  <Missing label={t('notApplicable')} />
                </Table.Td>
                <Table.Td />
              </Table.Tr>
            ) : null}
          </Table.Tfoot>
        ) : null}
      </Table>
    </Table.ScrollContainer>
  );
}

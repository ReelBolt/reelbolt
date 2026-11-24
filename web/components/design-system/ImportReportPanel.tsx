import { Badge, Stack, Table, Text } from '@mantine/core';
import type { ImportCapabilitySupport, ImportReport } from '@/lib/types/design-system';
import {
  IMPORT_REPORT_AXIS_COLUMN,
  IMPORT_REPORT_DROPPED_COLUMN,
  IMPORT_REPORT_DROPPED_BECAUSE_COLUMN,
  IMPORT_REPORT_IMPORTED_COLUMN,
  IMPORT_REPORT_NOTHING_DROPPED,
  IMPORT_REPORT_SUPPORT_COLUMN,
  IMPORT_REPORT_SUPPORT_LABELS,
  IMPORT_REPORT_TITLE,
  importAxisLabel,
  importDropSentence,
} from './copy';
import { useT } from '@/i18n';

/** Green/yellow/grey, always paired with the word, so colour is never the only signal. */
const SUPPORT_COLORS: Record<ImportCapabilitySupport, string> = {
  Full: 'green',
  Partial: 'yellow',
  None: 'gray',
};

/**
 * What one import carried and dropped, one row per axis (T-102; SPEC §17). The summary the customer
 * reads first: support, imported/dropped counts and a plain-language sentence per reason code. The
 * raw diagnostics list stays below it for detail. Renders nothing when the API sent no report.
 *
 * Reason messages are shown as text — imported markup is never rendered and nothing here is HTML.
 */
export function ImportReportPanel({ report }: { report?: ImportReport | null }) {
  const tr = useT();
  if (!report) return null;

  return (
    <Stack gap="xs" data-testid="import-report">
      <Text fw={600}>{tr.msg(IMPORT_REPORT_TITLE)}</Text>
      <Table.ScrollContainer minWidth={640}>
        <Table
          aria-label={tr.msg(IMPORT_REPORT_TITLE)}
          striped
          withTableBorder
          verticalSpacing={4}
          horizontalSpacing={6}
        >
          <Table.Thead>
            <Table.Tr>
              <Table.Th scope="col">{tr.msg(IMPORT_REPORT_AXIS_COLUMN)}</Table.Th>
              <Table.Th scope="col">{tr.msg(IMPORT_REPORT_SUPPORT_COLUMN)}</Table.Th>
              <Table.Th scope="col">{tr.msg(IMPORT_REPORT_IMPORTED_COLUMN)}</Table.Th>
              <Table.Th scope="col">{tr.msg(IMPORT_REPORT_DROPPED_COLUMN)}</Table.Th>
              <Table.Th scope="col">{tr.msg(IMPORT_REPORT_DROPPED_BECAUSE_COLUMN)}</Table.Th>
            </Table.Tr>
          </Table.Thead>
          <Table.Tbody>
            {report.axes.map((axis) => (
              <Table.Tr key={axis.axis} data-testid={`import-report-row-${axis.axis}`}>
                <Table.Th scope="row">{tr.msg(importAxisLabel(axis.axis))}</Table.Th>
                <Table.Td>
                  <Badge color={SUPPORT_COLORS[axis.support] ?? 'gray'} variant="light" size="sm">
                    {(IMPORT_REPORT_SUPPORT_LABELS[axis.support] ? tr.msg(IMPORT_REPORT_SUPPORT_LABELS[axis.support]) : axis.support)}
                  </Badge>
                </Table.Td>
                <Table.Td data-testid={`import-report-imported-${axis.axis}`}>
                  {axis.imported}
                </Table.Td>
                <Table.Td data-testid={`import-report-dropped-${axis.axis}`}>
                  {axis.dropped}
                </Table.Td>
                <Table.Td>
                  {axis.dropped === 0 ? (
                    <Text
                      size="xs"
                      c="dimmed"
                      data-testid={`import-report-nothing-dropped-${axis.axis}`}
                    >
                      {tr.msg(IMPORT_REPORT_NOTHING_DROPPED)}
                    </Text>
                  ) : (
                    <Stack gap={2}>
                      {axis.reasons.map((reason) => (
                        <Stack
                          key={reason.code}
                          gap={0}
                          data-testid={`import-report-reason-${axis.axis}-${reason.code}`}
                        >
                          <Text size="xs">{tr.msg(importDropSentence(reason.code, reason.message))}</Text>
                          <Text size="xs" c="dimmed">
                            {reason.code} · {reason.count}
                          </Text>
                        </Stack>
                      ))}
                    </Stack>
                  )}
                </Table.Td>
              </Table.Tr>
            ))}
          </Table.Tbody>
        </Table>
      </Table.ScrollContainer>
    </Stack>
  );
}

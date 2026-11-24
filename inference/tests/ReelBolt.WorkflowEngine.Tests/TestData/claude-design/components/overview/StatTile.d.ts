/** @startingPoint section="Overview" subtitle="Clickable KPI tile from the Overview dashboard" viewport="300x110" */
export interface StatTileProps {
  label: string;
  value: number | string;
  /** Outlined Material icon (edit_note, check_circle, hourglass_top, folder_open, verified, cancel). */
  icon: string;
  iconColor?: 'primary' | 'info' | 'success' | 'warning' | 'error' | 'secondary';
  /** When set the tile is clickable: primary-tinted surface, chevron, lift on hover, press-shrink. */
  onClick?: () => void;
  /** Shows a skeleton in place of the value. */
  loading?: boolean;
}
export declare function StatTile(props: StatTileProps): JSX.Element;

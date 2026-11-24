export interface ButtonProps {
  children?: React.ReactNode;
  /** filled = MudButton Variant.Filled · outlined · text · gradient. */
  variant?: 'filled' | 'outlined' | 'text' | 'gradient';
  color?: 'primary' | 'secondary' | 'tertiary' | 'info' | 'success' | 'warning' | 'error' | 'dark' | 'default';
  size?: 'small' | 'medium' | 'large';
  /** Material icon name shown before the label. */
  startIcon?: string;
  endIcon?: string;
  iconVariant?: 'filled' | 'outlined' | 'round';
  fullWidth?: boolean;
  disabled?: boolean;
  /** tall = the Overview quick-action shape: 44px high, 12px radius, weight 600. */
  shape?: 'default' | 'tall';
  onClick?: (e: React.MouseEvent) => void;
  style?: React.CSSProperties;
}
export declare function Button(props: ButtonProps): JSX.Element;

export type AppLogoId =
  | 'rocket'
  | 'globe'
  | 'code'
  | 'layers'
  | 'spark'
  | 'box'
  | 'bolt'
  | 'cloud';

export interface AppLogoDefinition {
  id: AppLogoId;
  label: string;
}

export const APP_LOGO_IDS: AppLogoId[] = [
  'rocket',
  'globe',
  'code',
  'layers',
  'spark',
  'box',
  'bolt',
  'cloud',
];

export const APP_LOGOS: AppLogoDefinition[] = [
  { id: 'rocket', label: 'Rocket' },
  { id: 'globe', label: 'Globe' },
  { id: 'code', label: 'Code' },
  { id: 'layers', label: 'Layers' },
  { id: 'spark', label: 'Spark' },
  { id: 'box', label: 'Box' },
  { id: 'bolt', label: 'Bolt' },
  { id: 'cloud', label: 'Cloud' },
];

export function appLogoAssetUrl(logoKey: string): string {
  return `/app-logos/default/${logoKey}.svg`;
}

export function isAppLogoId(value: string | null | undefined): value is AppLogoId {
  return !!value && APP_LOGO_IDS.includes(value as AppLogoId);
}

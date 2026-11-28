/** The colour of time not tied to a listed app: neutral, so it never looks like one of the apps. */
export const OTHER_COLOR = 'hsl(220 9% 60%)';

function appHash(appKey: string): number {
  let hash = 0x811c9dc5;
  for (let i = 0; i < appKey.length; i++) {
    hash ^= appKey.charCodeAt(i);
    hash = Math.imul(hash, 0x01000193);
  }
  return hash >>> 0;
}

/** A deterministic colour for an app or category, independent of which other items are shown. */
export function appColor(appKey: string): string {
  const hash = appHash(appKey);
  const hue = hash % 360;
  const saturation = 55 + ((hash >>> 9) % 301) / 10;
  const lightness = 40 + ((hash >>> 18) % 201) / 10;
  return `hsl(${hue} ${saturation}% ${lightness}%)`;
}

/**
 * Returns stable colours for a set of apps or categories. A color is determined only by its key,
 * so it stays the same when the displayed set or order changes.
 */
export function appColors(keys: readonly string[]): (key: string) => string {
  const knownKeys = new Set(keys);
  return (key) => {
    if (!knownKeys.has(key)) {
      throw new Error(`No colour assigned for ${key}.`);
    }
    return appColor(key);
  };
}

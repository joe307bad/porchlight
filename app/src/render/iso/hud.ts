// The explore view's street panel (top right): the name of the street you're
// on, and while auto-walking, turn-by-turn directions to where you're
// heading, the step you're on first. Redrawn only when what it says changes.

import { formatDistance, type Step, type StepKind } from './directions';

const ICONS: Record<StepKind, string> = {
  start: '↑',
  join: '↗',
  straight: '↑',
  'slight-left': '↖',
  'slight-right': '↗',
  left: '↰',
  right: '↱',
  uturn: '⤺',
  cross: '⇅',
  arrive: '⚑',
};

/** Steps listed at once; the rest are summed up as "and N more". */
const MAX_STEPS = 8;

export class StreetHud {
  readonly root = document.createElement('div');
  private readonly street = document.createElement('div');
  private readonly list = document.createElement('ol');
  private shown = '';

  constructor(host: HTMLElement) {
    this.root.className = 'street-hud';
    this.street.className = 'street-hud-name';
    this.list.className = 'street-hud-steps';
    this.root.append(this.street, this.list);
    host.appendChild(this.root);
    this.root.hidden = true;
  }

  /** Show the street you're on (null hides it) and, while auto-walking, the directions (null hides them). */
  update(street: string | null, steps: Step[] | null): void {
    const key = `${street}|${steps?.map((s) => `${s.text}:${formatDistance(s.distance)}`).join('|') ?? ''}`;
    if (key === this.shown) return;
    this.shown = key;
    this.root.hidden = !street && !steps;
    this.street.textContent = street ?? (steps ? 'Making your way to the street' : '');
    this.street.hidden = !street && !steps;
    this.list.replaceChildren();
    this.list.hidden = !steps;
    if (!steps) return;
    steps.slice(0, MAX_STEPS).forEach((s, i) => {
      const li = document.createElement('li');
      if (i === 0) li.className = 'current';
      const icon = document.createElement('span');
      icon.className = 'icon';
      icon.textContent = ICONS[s.kind];
      const text = document.createElement('span');
      text.className = 'text';
      text.textContent = s.text;
      li.append(icon, text);
      const d = formatDistance(s.distance);
      if (d && s.kind !== 'arrive') {
        const dist = document.createElement('span');
        dist.className = 'dist';
        dist.textContent = d;
        li.append(dist);
      }
      this.list.append(li);
    });
    if (steps.length > MAX_STEPS) {
      const more = document.createElement('li');
      more.className = 'more';
      more.textContent = `and ${steps.length - MAX_STEPS} more`;
      this.list.append(more);
    }
  }
}

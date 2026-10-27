// An Xbox-style controller, read through the browser's Gamepad API with the
// "standard" button layout. Firefox on macOS numbers a Bluetooth Xbox One S
// pad its own way (and drops its triggers), so that one is remapped here.
// Chrome 156 on macOS doesn't see that pad at all unless it's started with
// --disable-features=XboxUseGameControllerDataFetcherMac (see README.md).
// Poll it once a frame: sticks and triggers come back as levels, buttons as
// presses (down since the last poll).

import type { Vec2 } from '../../model/types';

/** Standard-mapping button indices. */
export const Button = {
  A: 0,
  B: 1,
  X: 2,
  Y: 3,
  LB: 4,
  RB: 5,
  LT: 6,
  RT: 7,
  View: 8,
  Menu: 9,
  LeftStick: 10,
  RightStick: 11,
  Up: 12,
  Down: 13,
  Left: 14,
  Right: 15,
} as const;

/**
 * Firefox on macOS reports the Xbox One S pad over Bluetooth (id
 * "045e-02fd-…") under the "standard" label but with its own button numbers:
 * standard index → Firefox's, as captured on 2026-10-26. It gives some pairs
 * of buttons the same number (Y and LB, RB and View, Menu and the right
 * stick click), so only one of each pair is kept, and it has no triggers.
 */
const FIREFOX_XBOX_BT: Partial<Record<number, number>> = {
  [Button.A]: 1,
  [Button.B]: 2,
  [Button.X]: 3,
  [Button.Y]: 5,
  [Button.View]: 8,
  [Button.Menu]: 16,
  [Button.LeftStick]: 11,
  [Button.Up]: 12,
  [Button.Down]: 13,
  [Button.Left]: 14,
  [Button.Right]: 15,
};
const STANDARD_BUTTONS = 17;

/** Stick travel ignored round the centre (they rarely rest at exactly zero). */
const DEADZONE = 0.2;
/** Trigger travel ignored at rest. */
const TRIGGER_DEADZONE = 0.08;

export interface PadState {
  /** Left and right sticks, x right and y down, 0..1 long after the deadzone. */
  move: Vec2;
  look: Vec2;
  /** Triggers, 0..1. */
  lt: number;
  rt: number;
  /** Buttons held now, and those pressed since the last poll. */
  held: (b: number) => boolean;
  pressed: (b: number) => boolean;
}

export const IDLE_PAD: PadState = {
  move: { x: 0, y: 0 },
  look: { x: 0, y: 0 },
  lt: 0,
  rt: 0,
  held: () => false,
  pressed: () => false,
};

export class GamepadInput {
  private was: boolean[] = [];

  /** Whether a controller has been seen (browsers only show one after its first button press). */
  connected = false;

  /** Read the first connected controller; an idle state if there's none. */
  poll(): PadState {
    const pad = typeof navigator !== 'undefined' && navigator.getGamepads ? [...navigator.getGamepads()].find((g) => g?.connected) : null;
    this.connected = !!pad;
    if (!pad) {
      this.was = [];
      return IDLE_PAD;
    }
    const remap = pad.id.startsWith('045e-02fd-') ? FIREFOX_XBOX_BT : null;
    const button = (i: number): GamepadButton | undefined => {
      if (!remap) return pad.buttons[i];
      const j = remap[i];
      return j === undefined ? undefined : pad.buttons[j];
    };
    const now = Array.from({ length: STANDARD_BUTTONS }, (_, i) => !!button(i)?.pressed);
    const was = this.was;
    this.was = now;
    const level = (i: number) => {
      const v = button(i)?.value ?? 0;
      return v > TRIGGER_DEADZONE ? (v - TRIGGER_DEADZONE) / (1 - TRIGGER_DEADZONE) : 0;
    };
    return {
      move: stick(pad.axes[0] ?? 0, pad.axes[1] ?? 0),
      look: stick(pad.axes[2] ?? 0, pad.axes[3] ?? 0),
      lt: level(Button.LT),
      rt: level(Button.RT),
      held: (b) => !!now[b],
      pressed: (b) => !!now[b] && !was[b],
    };
  }
}

/** A stick with a round deadzone, rescaled so it still runs smoothly from 0 to 1. */
function stick(x: number, y: number): Vec2 {
  const len = Math.hypot(x, y);
  if (len < DEADZONE) return { x: 0, y: 0 };
  const k = Math.min(1, (len - DEADZONE) / (1 - DEADZONE)) / len;
  return { x: x * k, y: y * k };
}

/**
 * Turns a stick axis into one-off flicks: fires once as it's pushed past
 * `FIRE`, then not again until it's come back under `REARM`.
 */
export class Flick {
  private armed = true;

  constructor(
    private readonly fire = 0.7,
    private readonly rearm = 0.3,
  ) {}

  /** -1 or +1 the moment the value is pushed far enough one way, else 0. */
  update(v: number): number {
    if (Math.abs(v) < this.rearm) this.armed = true;
    if (!this.armed || Math.abs(v) < this.fire) return 0;
    this.armed = false;
    return Math.sign(v);
  }
}

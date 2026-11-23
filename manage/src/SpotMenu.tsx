// The map's right-click menu: for the spot clicked, each of your characters
// playing right now can be sent directions there, or taken straight there
// (POST /mp/goto, which the server passes on to the game playing them).

import { useEffect, useRef, useState } from 'react';
import { playingNow, sendTo, type CountyMap, type Playing } from './api.ts';
import { nearestStreet, townAt } from './streets.ts';

export interface SpotClick {
  clientX: number;
  clientY: number;
  /** The spot in the county's tiles. */
  x: number;
  y: number;
}

/** What the menu says the spot is: "near Elm Street", or "in Lanternport". */
export function spotLabel(map: CountyMap, x: number, y: number): string | null {
  const at = townAt(map.towns, x, y);
  if (!at) return null;
  const street = nearestStreet(at.town, at.x, at.y);
  return street ? `near ${street}` : `in ${at.town.name}`;
}

export function SpotMenu({ at, map, onClose }: { at: SpotClick; map: CountyMap; onClose: () => void }) {
  const [playing, setPlaying] = useState<Playing[] | null>(null);
  const [said, setSaid] = useState<string | null>(null);
  const box = useRef<HTMLDivElement>(null);
  const spot = townAt(map.towns, at.x, at.y);
  const label = spotLabel(map, at.x, at.y);

  useEffect(() => {
    let live = true;
    void playingNow().then((p) => live && setPlaying(p));
    const key = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    const away = (e: PointerEvent) => !box.current?.contains(e.target as Node) && onClose();
    window.addEventListener('keydown', key);
    window.addEventListener('pointerdown', away, true);
    return () => {
      live = false;
      window.removeEventListener('keydown', key);
      window.removeEventListener('pointerdown', away, true);
    };
  }, [onClose]);

  const go = async (p: Playing, how: 'directions' | 'transport') => {
    if (!spot || !label) return;
    const err = await sendTo(p.id, spot.town.key, spot.x, spot.y, how, label);
    setSaid(err ?? (how === 'directions' ? `Directions sent to ${p.name}` : `${p.name} is on the way`));
    if (!err) window.setTimeout(onClose, 1200);
  };

  // Kept on the page, however near its edge the click was.
  const left = Math.min(at.clientX, window.innerWidth - 260);
  const top = Math.min(at.clientY, window.innerHeight - 180);
  return (
    <div ref={box} className="spot-menu" role="menu" style={{ left, top }} onContextMenu={(e) => e.preventDefault()}>
      <div className="spot-title">{spot ? `${spot.town.name}, ${label}` : 'Not in a town'}</div>
      {said && <div className="spot-said">{said}</div>}
      {!said && spot && playing === null && <div className="spot-none">…</div>}
      {!said && spot && playing?.length === 0 && <div className="spot-none">None of your characters is playing</div>}
      {!said &&
        spot &&
        playing?.map((p) => (
          <div key={p.id} className="spot-who">
            <button role="menuitem" onClick={() => void go(p, 'directions')}>
              Directions here for {p.name}
            </button>
            <button role="menuitem" onClick={() => void go(p, 'transport')}>
              Take {p.name} here
            </button>
          </div>
        ))}
    </div>
  );
}

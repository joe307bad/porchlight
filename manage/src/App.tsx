// Manage Porchlight (/manage), for now its traffic view: sign-in (the game's own), then the county map with the
// live feed on it, and the panel beside it. The map draws itself every
// animation frame from the World (render.ts); React only draws the panel, a
// few times a second, from the same World.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { buildingLife, county, inDesktopApp, me, signInUrl, townLife, type CountyMap, type Me } from './api.ts';
import type { BuildingLife, TownLife } from './life.ts';
import { Feed, type FeedStatus } from './feed.ts';
import { Panel } from './Panel.tsx';
import { backToApp, backToGame, embedded } from './embed.ts';
import { DEFAULT_LAYERS, MapRenderer, type Hover, type Layers } from './render.ts';
import { World } from './world.ts';

type Phase = { state: 'loading' } | { state: 'signedOut' } | { state: 'error'; why: string } | { state: 'ready'; who: Me; map: CountyMap };

const LAYERS_KEY = 'porchlight.manage.traffic.layers';
/** How often each town's life is asked for (ms). */
const LIFE_MS = 5_000;

function savedLayers(): Layers {
  try {
    const raw = localStorage.getItem(LAYERS_KEY);
    return raw ? { ...DEFAULT_LAYERS, ...(JSON.parse(raw) as Partial<Layers>) } : DEFAULT_LAYERS;
  } catch {
    return DEFAULT_LAYERS;
  }
}

export function App() {
  const [phase, setPhase] = useState<Phase>({ state: 'loading' });

  const load = useCallback(async () => {
    setPhase({ state: 'loading' });
    try {
      const who = await me();
      if (!who) return setPhase({ state: 'signedOut' });
      setPhase({ state: 'ready', who, map: await county() });
    } catch (err) {
      setPhase({ state: 'error', why: (err as Error).message });
    }
  }, []);

  useEffect(() => {
    void load();
  }, [load]);
  const signedOut = useCallback(() => setPhase({ state: 'signedOut' }), []);

  if (phase.state === 'loading') return <Card title="Manage Porchlight">Loading the county…</Card>;
  if (phase.state === 'signedOut') return <SignIn />;
  if (phase.state === 'error')
    return (
      <Card title="Manage Porchlight">
        <p>{phase.why}</p>
        <button className="btn" onClick={() => void load()}>
          Try again
        </button>
      </Card>
    );
  return <Live who={phase.who} map={phase.map} onSignedOut={signedOut} />;
}

function Card({ title, children }: { title: string; children: React.ReactNode }) {
  return (
    <main className="card-page">
      <section className="card">
        <h1>{title}</h1>
        {children}
      </section>
    </main>
  );
}

function SignIn() {
  if (inDesktopApp())
    return (
      <Card title="Manage Porchlight">
        <p>Sign in in the game first, then open Manage from its sidebar again.</p>
      </Card>
    );
  return (
    <Card title="Manage Porchlight">
      <p>Sign in with the same account you play with to watch the county live.</p>
      <a className="btn btn-primary" href={signInUrl()}>
        Sign in
      </a>
    </Card>
  );
}

function Live({ who, map, onSignedOut }: { who: Me; map: CountyMap; onSignedOut: () => void }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const world = useMemo(() => new World(), []);
  const [renderer, setRenderer] = useState<MapRenderer | null>(null);
  const [status, setStatus] = useState<FeedStatus>({ state: 'connecting' });
  const [layers, setLayers] = useState<Layers>(savedLayers);
  const [selected, setSelected] = useState<string | null>(null);
  const [following, setFollowing] = useState<string | null>(null);
  const [hover, setHover] = useState<Hover | null>(null);
  const [feed, setFeed] = useState<Feed | null>(null);
  const [lives, setLives] = useState<Map<string, TownLife>>(new Map());
  const [building, setBuilding] = useState<BuildingLife | null>(null);

  useEffect(() => {
    const r = new MapRenderer(canvas.current!, world, map);
    r.onSelect = (sid) => {
      setSelected(sid);
      setFollowing(null);
      r.follow(null);
      r.select(sid);
    };
    r.onFollowEnd = () => setFollowing(null);
    r.onHover = setHover;
    r.onBuilding = (b) => {
      if (!b) return setBuilding(null);
      void buildingLife(b.key, b.i).then(setBuilding);
    };
    setRenderer(r);
    const f = new Feed(world, (s) => {
      setStatus(s);
      if (s.state === 'signedOut') onSignedOut();
    });
    f.start();
    setFeed(f);
    // Which towns are looked at closely, said as it changes: those come ten times a second.
    const focus = setInterval(() => f.focus(r.townsInView()), 500);
    // Each town's life (who's indoors, the day so far), every few seconds: it changes slowly.
    let stopped = false;
    const lifeOnce = async () => {
      const got = new Map<string, TownLife>();
      for (const t of map.towns) {
        const life = await townLife(t.key).catch(() => null);
        if (stopped) return;
        if (life) {
          got.set(t.key, life);
          r.setLife(t.key, life);
        }
      }
      setLives(got);
    };
    void lifeOnce();
    const lifeTimer = setInterval(() => void lifeOnce(), LIFE_MS);
    return () => {
      stopped = true;
      clearInterval(lifeTimer);
      clearInterval(focus);
      f.stop();
      r.dispose();
    };
  }, [world, map, onSignedOut]);

  useEffect(() => {
    if (!renderer) return;
    renderer.layers = layers;
    try {
      localStorage.setItem(LAYERS_KEY, JSON.stringify(layers));
    } catch {
      // No storage (a private window): the layers just aren't remembered.
    }
  }, [renderer, layers]);

  const follow = useCallback(
    (sid: string | null) => {
      setFollowing(sid);
      setSelected(sid);
      renderer?.follow(sid);
    },
    [renderer],
  );

  return (
    <div className="app">
      <div className="map-wrap">
        <canvas ref={canvas} className="map" aria-label="The county map, live" />
        {hover && (
          <div className="tip" style={{ left: hover.x + 14, top: hover.y + 14 }}>
            {hover.text}
          </div>
        )}
        <div className="map-buttons">
          {!embedded && inDesktopApp() && (
            <button className="btn btn-primary" onClick={() => void backToApp()} title="Back to the Porchlight app (Manage stays open here)">
              ← Back to Porchlight
            </button>
          )}
          {embedded && (
            <button className="btn btn-primary" onClick={backToGame} title="Back to the game (Esc)">
              ← Back to the game
            </button>
          )}
          <button className="btn" onClick={() => { follow(null); renderer?.fitAll(); }} title="The whole county">
            Whole county
          </button>
        </div>
      </div>
      <Panel
        who={who}
        world={world}
        map={map}
        status={status}
        feed={feed}
        renderer={renderer}
        layers={layers}
        onLayers={setLayers}
        selected={selected}
        lives={lives}
        building={building}
        onCloseBuilding={() => setBuilding(null)}
        following={following}
        onFollow={follow}
        onTown={(t) => {
          follow(null);
          renderer?.fit(t.x0, t.y0, t.width, t.height);
        }}
      />
    </div>
  );
}

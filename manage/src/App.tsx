// Manage Porchlight (/manage), for now its traffic view: sign-in (the game's own), then the county map with the
// live feed on it, and the panel beside it. The map draws itself every
// animation frame from the World (render.ts); React only draws the panel, a
// few times a second, from the same World.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { county, inDesktopApp, me, signInUrl, type CountyMap, type Me } from './api.ts';
import { Feed, type FeedStatus } from './feed.ts';
import { Panel } from './Panel.tsx';
import { DEFAULT_LAYERS, MapRenderer, type Hover, type Layers } from './render.ts';
import { World } from './world.ts';

type Phase = { state: 'loading' } | { state: 'signedOut' } | { state: 'error'; why: string } | { state: 'ready'; who: Me; map: CountyMap };

const LAYERS_KEY = 'porchlight.manage.traffic.layers';

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
    setRenderer(r);
    const f = new Feed(world, (s) => {
      setStatus(s);
      if (s.state === 'signedOut') onSignedOut();
    });
    f.start();
    setFeed(f);
    return () => {
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

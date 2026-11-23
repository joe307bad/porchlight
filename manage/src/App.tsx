// Manage Porchlight (/manage), for now its traffic view: sign-in (the game's own), then the county map with the
// live feed on it, and the panel beside it. The map draws itself every
// animation frame from the World (render.ts); React only draws the panel, a
// few times a second, from the same World.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { briefOn, buildingLife, county, focusOn, signalsOf, inDesktopApp, me, signInUrl, townLife, type CountyMap, type Me } from './api.ts';
import { isFocus, type BuildingLife, type CongestedStreet, type EntityFocus, type FocusAnswer, type FocusTarget, type TownLife } from './life.ts';
import { Feed, type FeedStatus } from './feed.ts';
import { Panel } from './Panel.tsx';
import { COUNTY, Memory, backTo, intoTown, onTo, onToBuilding, type Crumb } from './crumbs.ts';
import { SpotMenu, type SpotClick } from './SpotMenu.tsx';
import { backToApp, backToGame, embedded } from './embed.ts';
import { DEFAULT_LAYERS, MapRenderer, type Hover, type Layers } from './render.ts';
import { World } from './world.ts';

type Phase = { state: 'loading' } | { state: 'signedOut' } | { state: 'error'; why: string } | { state: 'ready'; who: Me; map: CountyMap };

const LAYERS_KEY = 'porchlight.manage.traffic.layers';
const HIDDEN_KEY = 'porchlight.manage.traffic.hidden';
/** How often each town's life is asked for (ms). */
const LIFE_MS = 5_000;
/** How often the traffic lights in view are asked for (ms). */
const SIGNALS_MS = 1_000;
/** How often the car or person focused on is asked about again (ms). */
const FOCUS_MS = 1_000;

/** What to say when a click finds nobody with a day to explain. */
function nobody(a: FocusAnswer | null): string {
  if (a && 'wanderer' in a) return 'A wanderer: they stroll the pavements with no day of their own, so there are no decisions to show.';
  return "Nobody's there now (or the server isn't running that town).";
}

function savedLayers(): Layers {
  try {
    const raw = localStorage.getItem(LAYERS_KEY);
    return raw ? { ...DEFAULT_LAYERS, ...(JSON.parse(raw) as Partial<Layers>) } : DEFAULT_LAYERS;
  } catch {
    return DEFAULT_LAYERS;
  }
}

/** The kinds of car and person hidden on the map, as last left. */
function savedHidden(): Set<string> {
  try {
    const raw = localStorage.getItem(HIDDEN_KEY);
    return new Set(raw ? (JSON.parse(raw) as string[]) : []);
  } catch {
    return new Set();
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
  /** The right-click menu's spot, while it's open. */
  const [spot, setSpot] = useState<SpotClick | null>(null);
  const closeSpot = useCallback(() => setSpot(null), []);
  const [hidden, setHidden] = useState<Set<string>>(savedHidden);
  const [, setSelected] = useState<string | null>(null);
  const [, setFollowing] = useState<string | null>(null);
  const [hover, setHover] = useState<Hover | null>(null);
  const [lives, setLives] = useState<Map<string, TownLife>>(new Map());
  /** Whether the towns' life has been asked for at least once (so "none running" means it, not "not asked yet"). */
  const [livesAsked, setLivesAsked] = useState(false);
  const [building, setBuilding] = useState<BuildingLife | null>(null);
  // The car or townsperson focused on: who (followed by id), what the simulation last said, and whether the map follows them.
  const [target, setTarget] = useState<FocusTarget | null>(null);
  const [focus, setFocus] = useState<EntityFocus | null>(null);
  /** People's names heard from their focus ("town|id"), for their crumbs. */
  const names = useRef(new Map<string, string>());
  const [focusNote, setFocusNote] = useState<string | null>(null);
  const [followingFocus, setFollowingFocus] = useState(false);
  /** Where the sidebar is (crumbs.ts), and what each crumb was left showing. */
  const [crumbs, setCrumbs] = useState<Crumb[]>([COUNTY]);
  const memory = useMemo(() => new Memory(), []);
  /** The map's clicks, as the latest render has them (the renderer's set up once). */
  const clicks = useRef<{ entity: (e: { key: string; kind: 'car' | 'person'; slot: number }) => void; building: (b: { key: string; i: number } | null) => void }>({
    entity: () => {},
    building: () => {},
  });

  useEffect(() => {
    const r = new MapRenderer(canvas.current!, world, map);
    r.onSelect = (sid) => {
      setSelected(sid);
      setFollowing(null);
      r.follow(null);
      r.select(sid);
    };
    r.onFollowEnd = () => setFollowing(null);
    r.onFocusFollowEnd = () => setFollowingFocus(false);
    r.onEntity = (e) => clicks.current.entity(e);
    // Hovering a car or townsperson: the map's own line straight away, then the simulation's (who, what they're
    // doing, where in their day) once it answers, if the pointer's still on them.
    let hovered = '';
    const briefs = new Map<string, { line: string | null; at: number }>();
    r.onHover = (h) => {
      if (!h) hovered = '';
      setHover((was) => (h && hovered && was && was.text.includes('click for why') && was.key === hovered ? { ...was, x: h.x, y: h.y } : h));
    };
    r.onHoverEntity = (e) => {
      const k = e ? `${e.key}|${e.kind}|${e.slot}` : '';
      if (k === hovered) return;
      hovered = k;
      if (!e) return;
      const show = (line: string | null) => {
        if (hovered === k && line) setHover({ x: e.x, y: e.y, text: `${line} · click for why`, key: k });
      };
      const kept = briefs.get(k);
      if (kept && performance.now() - kept.at < 2000) return show(kept.line);
      void briefOn(e.key, e.kind, e.slot).then((line) => {
        briefs.set(k, { line, at: performance.now() });
        show(line);
      });
    };
    r.onContext = (c) => setSpot(c);
    r.onBuilding = (b) => clicks.current.building(b);
    setRenderer(r);
    const f = new Feed(world, (s) => {
      setStatus(s);
      if (s.state === 'signedOut') onSignedOut();
    });
    f.start();
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
      setLivesAsked(true);
    };
    void lifeOnce();
    const lifeTimer = setInterval(() => void lifeOnce(), LIFE_MS);
    // The traffic lights of the towns in view, close in: a light changes every half minute (or early, for a car
    // waiting the other way), so once a second.
    const signalTimer = setInterval(() => {
      if (!r.showsSignals()) return;
      // A town's stop signs come with its lights the first time (they don't change).
      for (const key of r.townsInView()) void signalsOf(key, !r.hasStops(key)).then((s) => !stopped && r.setSignals(key, s));
    }, SIGNALS_MS);
    return () => {
      stopped = true;
      clearInterval(lifeTimer);
      clearInterval(signalTimer);
      clearInterval(focus);
      f.stop();
      r.dispose();
    };
  }, [world, map, onSignedOut]);

  useEffect(() => {
    if (!renderer) return;
    renderer.hidden = hidden;
    try {
      localStorage.setItem(HIDDEN_KEY, JSON.stringify([...hidden]));
    } catch {
      // No storage: not remembered.
    }
  }, [renderer, hidden]);

  useEffect(() => {
    if (!renderer) return;
    renderer.layers = layers;
    try {
      localStorage.setItem(LAYERS_KEY, JSON.stringify(layers));
    } catch {
      // No storage (a private window): the layers just aren't remembered.
    }
  }, [renderer, layers]);

  // The focus, asked about again every second while there is one.
  useEffect(() => {
    if (!target) return;
    let stopped = false;
    const once = async () => {
      const a = await focusOn(target.town, target.kind, 'id', target.id).catch(() => null);
      if (stopped) return;
      if (isFocus(a)) {
        // Each person's name, as heard: their crumb says it.
        if (a.kind === 'person' && a.name) names.current.set(`${a.town}|${a.id}`, a.name);
        if (a.kind === 'car' && a.ownerName && a.owner !== undefined && a.owner >= 0) names.current.set(`${a.town}|${a.owner}`, a.ownerName);
        setFocus(a);
        setFocusNote(null);
      } else setFocusNote("Lost track of them: the server isn't running that town now.");
    };
    void once();
    const t = setInterval(() => void once(), FOCUS_MS);
    return () => {
      stopped = true;
      clearInterval(t);
    };
  }, [target]);

  useEffect(() => {
    renderer?.setFocus(focus);
  }, [renderer, focus]);

  /** Focus on someone by who they are, and follow them there: a crumb on (from the map, `fresh`: the county, their town, them). */
  const focusTo = useCallback(
    (t: FocusTarget, fresh = false) => {
      setCrumbs((c) => onTo(c, t, fresh));
      setBuilding(null);
      setFocus(null);
      setFocusNote(null);
      setTarget(t);
      setFollowing(null);
      renderer?.follow(null);
      setFollowingFocus(true);
      renderer?.followFocus(true);
    },
    [renderer],
  );

  const congestion = useCallback((key: string | null, streets: CongestedStreet[] | null) => renderer?.setCongestion(key, streets), [renderer]);
  const street = useCallback(
    (key: string, points: number[]) => {
      follow(null);
      renderer?.fitStreet(key, points);
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [renderer],
  );

  /** Focus on whoever's drawn in a town's car or person record now (from a kind's list), and follow them there. */
  const focusSlot = useCallback(
    (key: string, kind: 'car' | 'person', slot: number) => {
      void focusOn(key, kind, 'slot', slot).then((a) => {
        if (!isFocus(a)) {
          setTarget(null);
          setFocus(null);
          setFocusNote(nobody(a));
          return;
        }
        focusTo({ town: key, kind: a.kind, id: a.id });
      });
    },
    [focusTo],
  );

  // A car or person clicked on the map: followed, the county › their town › them.
  clicks.current.entity = (e) => {
    void focusOn(e.key, e.kind, 'slot', e.slot).then((a) => {
      if (!isFocus(a)) {
        setFocusNote(nobody(a));
        return;
      }
      focusTo({ town: e.key, kind: a.kind, id: a.id }, true);
    });
  };
  // A building clicked on the map: the county › its town › who's inside.
  clicks.current.building = (b) => {
    if (!b) return;
    closeFocus();
    setCrumbs(onToBuilding(b.key, b.i));
    setBuilding(null);
    void buildingLife(b.key, b.i)
      .catch(() => null)
      .then((got) => {
        setBuilding(got);
        if (!got) setFocusNote("Can't tell who's in it: the server's simulation isn't running that town.");
      });
  };

  const fitTown = useCallback(
    (key: string) => {
      const t = map.towns.find((x) => x.key === key);
      if (t) renderer?.fit(t.x0, t.y0, t.width, t.height);
    },
    [map, renderer],
  );

  /** Into a town from the county: its crumb, and the map on it. */
  const openTown = useCallback(
    (key: string) => {
      memory.town = key;
      closeFocus();
      setBuilding(null);
      setCrumbs(intoTown(key));
      follow(null);
      fitTown(key);
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [memory, fitTown],
  );

  /** Back to crumb i, as it was left: the county (the whole of it), a town (on it), or whoever was followed there (again). */
  const goCrumb = useCallback(
    (i: number) => {
      const stack = backTo(crumbs, i);
      const top = stack[stack.length - 1];
      setCrumbs(stack);
      if (top.at === 'entity') return focusTo(top.target);
      closeFocus();
      if (top.at === 'building') {
        clicks.current.building({ key: top.town, i: top.building });
        return;
      }
      setBuilding(null);
      follow(null);
      if (top.at === 'town') fitTown(top.town);
      else renderer?.fitAll();
    },
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [crumbs, focusTo, fitTown, renderer],
  );

  const followFocus = useCallback(
    (on: boolean) => {
      setFollowingFocus(on);
      if (on) setFollowing(null);
      renderer?.followFocus(on);
    },
    [renderer],
  );

  const closeFocus = useCallback(() => {
    setTarget(null);
    setFocus(null);
    setFocusNote(null);
    setFollowingFocus(false);
    renderer?.setFocus(null);
  }, [renderer]);

  const follow = useCallback(
    (sid: string | null) => {
      setFollowing(sid);
      setSelected(sid);
      if (sid) {
        setFollowingFocus(false);
        renderer?.followFocus(false);
      }
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
        {spot && <SpotMenu at={spot} map={map} onClose={closeSpot} />}
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
          <button className="btn" onClick={() => goCrumb(0)} title="The whole county">
            Whole county
          </button>
        </div>
      </div>
      <Panel
        personName={(town, id) => names.current.get(`${town}|${id}`)}
        who={who}
        map={map}
        status={status}
        layers={layers}
        onLayers={setLayers}
        hidden={hidden}
        onHidden={setHidden}
        lives={lives}
        livesAsked={livesAsked}
        crumbs={crumbs}
        memory={memory}
        onCrumb={goCrumb}
        onTown={openTown}
        onPick={focusSlot}
        onFocus={focusTo}
        building={building}
        focus={focus}
        focusNote={focusNote}
        followingFocus={followingFocus}
        onFollowFocus={followFocus}
        onCongestion={congestion}
        onStreet={street}
      />
    </div>
  );
}

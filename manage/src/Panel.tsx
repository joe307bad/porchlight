// Beside the map: the feed's state, the county's figures, what's shown, the
// towns with anything going on, and everyone playing (what they're doing, and
// who and what is round them). Read from the World four times a second.

import { useEffect, useState } from 'react';
import type { CountyMap, Me } from './api.ts';
import type { Feed, FeedStatus } from './feed.ts';
import { KMH, PERSON, SPEEDS, type Layers, type MapRenderer } from './render.ts';
import { REACH, STOPPED, doing, type World } from './world.ts';

const REFRESH_MS = 250;

interface Props {
  who: Me;
  world: World;
  map: CountyMap;
  status: FeedStatus;
  feed: Feed | null;
  renderer: MapRenderer | null;
  layers: Layers;
  onLayers: (l: Layers) => void;
  selected: string | null;
  following: string | null;
  onFollow: (sid: string | null) => void;
  onTown: (t: CountyMap['towns'][number]) => void;
}

const LAYER_NAMES: [keyof Layers, string][] = [
  ['cars', 'Cars'],
  ['people', 'Townsfolk'],
  ['players', 'Players'],
  ['names', 'Names'],
  ['reach', `Who's near whom (${REACH} tiles)`],
  ['roads', 'Streets'],
  ['trails', 'Trails'],
];

export function Panel(p: Props) {
  const [, setTick] = useState(0);
  const [open, setOpen] = useState(true);
  useEffect(() => {
    const t = setInterval(() => setTick((n) => n + 1), REFRESH_MS);
    return () => clearInterval(t);
  }, []);
  const totals = p.world.totals();
  const players = p.world.players().filter((x) => x.seen);
  const towns = p.world.order;
  const rate = useRate(p.feed);

  return (
    <aside className={`panel ${open ? '' : 'panel-closed'}`}>
      <header className="panel-head">
        <div>
          <h1>Manage Porchlight</h1>
          <p className="mode">Traffic</p>
          <Status status={p.status} />
        </div>
        <button className="btn btn-small panel-toggle" onClick={() => setOpen(!open)} aria-expanded={open}>
          {open ? 'Hide' : 'Show'}
        </button>
      </header>
      {open && (
        <div className="panel-body">
          <section className="stats" aria-label="The county now">
            <Stat label="Players" value={totals.players} />
            <Stat label="Cars" value={totals.cars} />
            <Stat label="Townsfolk" value={totals.people} />
            <Stat label="Towns live" value={totals.towns} />
            <Stat label="Moving" value={totals.moving} hint={totals.moving ? `avg ${Math.round(totals.speed * KMH)} km/h` : undefined} />
            <Stat label="Stopped" value={totals.stopped} hint={totals.cars ? `${Math.round((100 * totals.stopped) / totals.cars)}% of cars` : undefined} />
          </section>

          <section>
            <h2>Players</h2>
            {players.length === 0 ? (
              <p className="muted">Nobody's playing right now. Cars and townsfolk only run in a town while someone's in it.</p>
            ) : (
              <ul className="list">
                {players.map((pl) => (
                  <li key={pl.sid} className={pl.sid === p.selected ? 'sel' : ''}>
                    <button className="row" onClick={() => p.onFollow(p.following === pl.sid ? null : pl.sid)} title={p.following === pl.sid ? 'Stop following' : 'Follow them on the map'}>
                      <span className="swatch" style={{ background: pl.color, borderRadius: pl.car > 0 ? 2 : 99 }} />
                      <span className="grow">
                        <span className="name">
                          {pl.name}
                          {pl.host && <span className="badge" title="Their game runs this town's cars and townsfolk">host</span>}
                        </span>
                        <span className="sub">
                          {doing(pl)} · {pl.town}
                        </span>
                        <span className="sub">
                          {pl.nearCars} cars, {pl.nearPeople} townsfolk
                          {pl.nearPlayers.length > 0 && `, with ${pl.nearPlayers.map((q) => q.name).join(', ')}`}
                        </span>
                      </span>
                      <span className="follow">{p.following === pl.sid ? 'Following' : 'Follow'}</span>
                    </button>
                  </li>
                ))}
              </ul>
            )}
          </section>

          <section>
            <h2>Towns</h2>
            <ul className="list">
              {p.map.towns.map((t) => {
                const live = towns.filter((r) => r.meta.key === t.key);
                const n = (f: (r: (typeof live)[number]) => number) => live.reduce((a, r) => a + f(r), 0);
                const cars = n((r) => r.cars.n);
                let stopped = 0;
                for (const r of live) for (let i = 0; i < r.cars.n; i++) if (r.cars.ts[i] < STOPPED) stopped++;
                return (
                  <li key={t.key}>
                    <button className="row" onClick={() => p.onTown(t)} title="Show it on the map">
                      <span className="grow">
                        <span className="name">{t.name}</span>
                        <span className="sub">
                          {live.length === 0
                            ? 'Nobody there'
                            : `${n((r) => r.players.length)} playing · ${cars} cars${cars ? ` (${stopped} stopped)` : ''} · ${n((r) => r.people.n)} townsfolk`}
                        </span>
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </section>

          <section>
            <h2>Show</h2>
            <div className="layers">
              {LAYER_NAMES.map(([k, label]) => (
                <label key={k} className="check">
                  <input type="checkbox" checked={p.layers[k]} onChange={(e) => p.onLayers({ ...p.layers, [k]: e.target.checked })} />
                  {label}
                </label>
              ))}
            </div>
            <div className="legend">
              {SPEEDS.map((s) => (
                <span key={s.label}>
                  <i style={{ background: s.color }} /> {s.label}
                </span>
              ))}
              <span>
                <i style={{ background: PERSON }} /> Townsperson
              </span>
              <span>
                <i className="ring" /> Player (a square at the wheel; ringed: runs the town)
              </span>
            </div>
          </section>

          <footer className="foot muted">
            {p.who?.name ? `Signed in as ${p.who.name}. ` : ''}
            {rate} · {p.renderer?.fps ?? 0} fps
          </footer>
        </div>
      )}
    </aside>
  );
}

function Stat({ label, value, hint }: { label: string; value: number; hint?: string }) {
  return (
    <div className="stat" title={hint}>
      <span className="v">{value.toLocaleString()}</span>
      <span className="l">{label}</span>
      {hint && <span className="h">{hint}</span>}
    </div>
  );
}

function Status({ status }: { status: FeedStatus }) {
  switch (status.state) {
    case 'live':
      return (
        <p className="status live">
          <i /> Live
        </p>
      );
    case 'connecting':
      return <p className="status">Connecting…</p>;
    case 'retrying':
      return <p className="status bad">{`${status.why} Again in ${Math.ceil(status.inMs / 1000)} s…`}</p>;
    case 'refused':
      return <p className="status bad">{status.why}</p>;
    case 'signedOut':
      return <p className="status bad">Signed out.</p>;
  }
}

/** What the feed's taking in, a second at a time. */
function useRate(feed: Feed | null): string {
  const [rate, setRate] = useState('');
  useEffect(() => {
    if (!feed) return;
    let bytes = feed.bytes;
    let frames = feed.frames;
    const t = setInterval(() => {
      const b = feed.bytes - bytes;
      const f = feed.frames - frames;
      bytes = feed.bytes;
      frames = feed.frames;
      setRate(`${f} updates/s, ${b < 1024 ? `${b} B` : `${(b / 1024).toFixed(1)} KB`}/s`);
    }, 1000);
    return () => clearInterval(t);
  }, [feed]);
  return rate;
}

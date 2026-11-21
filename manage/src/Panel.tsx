// Beside the map, as simple as it can be: what was clicked (a car, a
// townsperson or a building) at the top, each town in a line or two (click to
// go there), what the map shows with its key, and anyone playing. Read from the
// World four times a second.

import { useEffect, useRef, useState } from 'react';
import type { CountyMap, Me } from './api.ts';
import type { Feed, FeedStatus } from './feed.ts';
import { ALL_WAY, LAMPS, PERSON, SPEEDS, STOP_SIGN, type Layers, type MapRenderer } from './render.ts';
import { REACH, STOPPED, doing, type World } from './world.ts';
import { ZONES } from './zoning.ts';
import { MODES, dayMark, describeInside, figures, mark, untilText, type DayView, type BuildingLife, type EntityFocus, type FocusTarget, type TownLife, type Why } from './life.ts';

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
  /** Each town's life the simulation's running, by key. */
  lives: Map<string, TownLife>;
  /** Whether the towns' life has been asked for yet. */
  livesAsked: boolean;
  /** The building clicked, and who's in it. */
  building: BuildingLife | null;
  onCloseBuilding: () => void;
  following: string | null;
  onFollow: (sid: string | null) => void;
  onTown: (t: CountyMap['towns'][number]) => void;
  /** The car or townsperson focused on (the simulation's latest on them), or a word on why there's none. */
  focus: EntityFocus | null;
  focusNote: string | null;
  followingFocus: boolean;
  onFollowFocus: (on: boolean) => void;
  onCloseFocus: () => void;
  /** Focus on someone by who they are (a driver, their car, someone in a building). */
  onFocus: (t: FocusTarget) => void;
}

/** Each thing the map can show: its checkbox, and its key (what its colours mean). */
const LAYERS: { key: keyof Layers; label: string; legend?: { color: string; label: string; shape?: 'dot' | 'ring' | 'stop' }[] }[] = [
  { key: 'cars', label: 'Cars, by speed', legend: SPEEDS.map((s) => ({ color: s.color, label: s.label })) },
  { key: 'people', label: 'Townsfolk', legend: [{ color: PERSON, label: 'on foot' }] },
  {
    key: 'signals',
    label: 'Traffic lights',
    legend: [
      { color: LAMPS.green, label: 'go', shape: 'dot' },
      { color: LAMPS.amber, label: 'ending', shape: 'dot' },
      { color: LAMPS.red, label: 'stop', shape: 'dot' },
    ],
  },
  {
    key: 'stops',
    label: 'Stop signs',
    legend: [
      { color: STOP_SIGN, label: 'stop for the bigger road', shape: 'stop' },
      { color: ALL_WAY, label: 'all-way stop', shape: 'stop' },
    ],
  },
  { key: 'zoning', label: 'Land use (discs: people inside)', legend: ZONES.map((z) => ({ color: z.color, label: z.label })) },
  { key: 'players', label: 'Players', legend: [{ color: '#8ab4ff', label: 'player', shape: 'ring' }] },
  { key: 'roads', label: 'Streets' },
  { key: 'names', label: 'Town names' },
  { key: 'trails', label: 'Trails' },
  { key: 'reach', label: `Who's near whom (${REACH} tiles)` },
];

export function Panel(p: Props) {
  const [, setTick] = useState(0);
  const [open, setOpen] = useState(true);
  useEffect(() => {
    const t = setInterval(() => setTick((n) => n + 1), REFRESH_MS);
    return () => clearInterval(t);
  }, []);
  const players = p.world.players().filter((x) => x.seen);
  const rate = useRate(p.feed);
  const townName = (key: string) => p.map.towns.find((t) => t.key === key)?.name ?? key;

  return (
    <aside className={`panel ${open ? '' : 'panel-closed'}`}>
      <header className="panel-head">
        <div>
          <h1>Manage Porchlight</h1>
          <Status status={p.status} />
        </div>
        <button className="btn btn-small panel-toggle" onClick={() => setOpen(!open)} aria-expanded={open}>
          {open ? 'Hide' : 'Show'}
        </button>
      </header>
      {open && (
        <div className="panel-body">
          <SimMissing sim={p.map.sim} running={p.lives.size > 0} asked={p.livesAsked} />
          {/* What was clicked, always at the top: a car or person and why, a building and who's inside, or how to. */}
          {p.focus || p.focusNote ? (
            <FocusView
              f={p.focus}
              note={p.focusNote}
              town={p.focus ? townName(p.focus.town) : ''}
              following={p.followingFocus}
              onFollow={p.onFollowFocus}
              onClose={p.onCloseFocus}
              onFocus={p.onFocus}
            />
          ) : p.building ? (
            <Building building={p.building} town={townName(p.building.town)} onClose={p.onCloseBuilding} onFocus={p.onFocus} />
          ) : (
            <section className="hint" aria-label="Selected">
              <p>
                <b>Click on the map</b> to look closer: a <b>car</b> (who's driving, where to and why), a <b>townsperson</b> (their day, and what they'll do
                next) or a <b>building</b> (who's inside). Zoom in to see the traffic lights and stop signs.
              </p>
            </section>
          )}

          <section aria-label="Towns">
            <h2>Towns</h2>
            <ul className="list">
              {p.map.towns.map((t) => {
                const life = p.lives.get(t.key);
                return (
                  <li key={t.key}>
                    <button className="row" onClick={() => p.onTown(t)} title={`Show it on the map. Its key: ${t.key}`}>
                      <span className="grow">
                        <span className="name">
                          {t.name}
                          {life && <span className="time"> · {life.time}</span>}
                        </span>
                        {townLines(p.world, t.key, life).map((line) => (
                          <span key={line} className="sub">
                            {line}
                          </span>
                        ))}
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </section>

          <section aria-label="Show">
            <h2>Show</h2>
            <ul className="layer-list">
              {LAYERS.map((l) => (
                <li key={l.key}>
                  <label className="check">
                    <input type="checkbox" checked={p.layers[l.key]} onChange={(e) => p.onLayers({ ...p.layers, [l.key]: e.target.checked })} />
                    {l.label}
                  </label>
                  {l.legend && (
                    <span className="legend">
                      {l.legend.map((k) => (
                        <span key={k.label}>
                          <i className={k.shape ?? ''} style={{ background: k.color }} /> {k.label}
                        </span>
                      ))}
                    </span>
                  )}
                </li>
              ))}
            </ul>
          </section>

          {players.length > 0 && (
            <section aria-label="Players">
              <h2>Playing now</h2>
              <ul className="list">
                {players.map((pl) => (
                  <li key={pl.sid} className={pl.sid === p.selected ? 'sel' : ''}>
                    <button className="row" onClick={() => p.onFollow(p.following === pl.sid ? null : pl.sid)} title={p.following === pl.sid ? 'Stop following' : 'Follow them on the map'}>
                      <span className="swatch" style={{ background: pl.color, borderRadius: pl.car > 0 ? 2 : 99 }} />
                      <span className="grow">
                        <span className="name">{pl.name}</span>
                        <span className="sub">
                          {doing(pl)} · {pl.town}
                        </span>
                      </span>
                      <span className="follow">{p.following === pl.sid ? 'Following' : 'Follow'}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </section>
          )}

          <footer className="foot muted">
            {p.who?.name ? `Signed in as ${p.who.name}. ` : ''}
            {rate} · {p.renderer?.fps ?? 0} fps
          </footer>
        </div>
      )}
    </aside>
  );
}

/** A town's figures in a line or two: who's out, the cars on its roads, and how its day's going. */
export function townLines(world: World, key: string, life: TownLife | undefined): string[] {
  let cars = 0;
  let stopped = 0;
  let running = false;
  for (const r of world.order) {
    if (r.meta.key !== key) continue;
    running = true;
    for (let i = 0; i < r.cars.n; i++)
      if (r.cars.shown[i]) {
        cars++;
        if (r.cars.ts[i] < STOPPED) stopped++;
      }
  }
  const onRoad = `${cars.toLocaleString()} cars on the road${cars ? ` (${stopped} stopped)` : ''}`;
  if (!life) return [running ? onRoad : 'Not running'];
  const f = figures(life);
  const drive = MODES.indexOf('drive');
  const mean = f.meanMinutes[drive];
  return [
    `${f.people.toLocaleString()} people · ${f.out.toLocaleString()} out · ${onRoad}`,
    `${f.made.toLocaleString()} trips today · ${Math.round(f.shares[drive] * 100)}% by car${mean !== null ? `, ${mean.toFixed(1)} min each` : ''}`,
  ];
}

/**
 * Said at the top when the server isn't running the towns' life: everything the panel shows past the map's
 * streets (cars, townsfolk, lights, stop signs, who's inside, a click's why) comes from it.
 */
export function SimMissing({ sim, running, asked }: { sim: boolean | undefined; running: boolean; asked: boolean }) {
  if (running || (sim !== false && !asked)) return null;
  return (
    <section className="warn" role="status" aria-label="Simulation">
      <h2>{sim === false ? 'No town simulation on this server' : 'No town is running yet'}</h2>
      <p className="sub">
        {sim === false ? (
          <>
            The cars, townsfolk, traffic lights, stop signs, who's in each building and what you click on all come from the server's town simulation
            (sim/, .NET 8), and this server hasn't got it. Install the .NET SDK (8 or newer: <code>brew install --cask dotnet-sdk</code> on a Mac), then
            start <code>./dev.sh</code> again: it builds it.
          </>
        ) : (
          <>
            The server's town simulation hasn't started any towns, so there are no cars, townsfolk, lights or stop signs yet. A town takes a few seconds
            to start; if this stays, look for <code>[sim]</code> lines in the server's output.
          </>
        )}
      </p>
    </section>
  );
}

/** The building clicked: who's inside, and when each goes out next; click someone to focus on them. */
function Building({ building, town, onClose, onFocus }: { building: BuildingLife; town: string; onClose: () => void; onFocus: (t: FocusTarget) => void }) {
  const ref = useRef<HTMLElement>(null);
  useEffect(() => ref.current?.scrollIntoView?.({ block: 'start', behavior: 'smooth' }), [building]);
  return (
    <section className="focus" aria-label="Who's in the building" ref={ref}>
      <h2>{building.inside.length === 0 ? 'Nobody in' : `${building.inside.length}${building.inside.length >= 50 ? '+' : ''} inside`}</h2>
      <p className="sub">A building in {town}. Click someone for their day.</p>
      <div className="focus-buttons">
        <button className="btn btn-small" onClick={onClose}>
          Close
        </button>
      </div>
      <ul className="sub inside">
        {describeInside(building).map((line, i) => (
          <li key={line}>
            <button className="link" onClick={() => onFocus({ town: building.town, kind: 'person', id: building.inside[i].id })} title="Focus on them: why they do what they do, and what's next">
              {line}
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}

/**
 * One car or townsperson, followed by who they are: what they're doing, why (their decision tree: each question
 * their day and the town's rules asked, the answer, and what it went by) and what's next (their decision queue).
 */
function FocusView({
  f,
  note,
  town,
  following,
  onFollow,
  onClose,
  onFocus,
}: {
  f: EntityFocus | null;
  note: string | null;
  town: string;
  following: boolean;
  onFollow: (on: boolean) => void;
  onClose: () => void;
  onFocus: (t: FocusTarget) => void;
}) {
  // Someone new focused on (from a list further down, say): bring them into view.
  const ref = useRef<HTMLElement>(null);
  const who = f ? `${f.town}|${f.kind}|${f.id}` : note;
  useEffect(() => {
    ref.current?.scrollIntoView?.({ block: 'start', behavior: 'smooth' });
  }, [who]);
  if (!f)
    return (
      <section className="focus" aria-label="Focus" ref={ref}>
        <p className="small">{note ?? 'Looking…'}</p>
        <button className="btn btn-small" onClick={onClose}>
          Close
        </button>
      </section>
    );
  const title = f.kind === 'person' ? `Person #${f.id}` : f.bus !== undefined && f.bus >= 0 ? 'The bus' : f.van !== undefined && f.van >= 0 ? `Van #${f.van}` : `Car #${f.id}`;
  return (
    <section className="focus" aria-label="Focus" ref={ref}>
      <h2>{title}</h2>
      <p className="sub">
        {f.who} · {town} · {f.time}
      </p>
      <p className="doing">{f.doing}</p>
      {note && <p className="small bad">{note}</p>}
      <div className="focus-buttons">
        <button className={`btn btn-small ${following ? 'btn-primary' : ''}`} onClick={() => onFollow(!following)} aria-pressed={following}>
          {following ? 'Following' : 'Follow'}
        </button>
        {f.kind === 'person' && f.car !== undefined && f.car >= 0 && (
          <button className="btn btn-small" onClick={() => onFocus({ town: f.town, kind: 'car', id: f.car! })}>
            Their car
          </button>
        )}
        {f.kind === 'car' && f.owner !== undefined && f.owner >= 0 && (
          <button className="btn btn-small" onClick={() => onFocus({ town: f.town, kind: 'person', id: f.owner! })}>
            Its driver
          </button>
        )}
        <button className="btn btn-small" onClick={onClose}>
          Close
        </button>
      </div>
      {f.day && <Day day={f.day} owner={f.kind === 'car' ? f.owner : undefined} />}
      <h3>Why</h3>
      <Tree items={f.tree} />
      <h3>Next</h3>
      <ol className="queue">
        {f.queue.map((item, i) => (
          <li key={i} className={item.now ? 'now' : ''}>
            <span className="when">{untilText(item, f.clock)}</span>
            <span className="what">{item.what}</span>
          </li>
        ))}
      </ol>
      {f.recent && f.recent.length > 0 && (
        <>
          <h3>Earlier</h3>
          <ul className="sub recent">
            {[...f.recent].reverse().map((t, i) => (
              <li key={i}>
                {t.started}–{t.ended ?? '…'} · {t.purpose.toLowerCase()} by {t.mode}: {t.to}
                {!t.made && <span className="bad"> · couldn't be made{t.failed ? ` (${t.failed})` : ''}</span>}
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  );
}

/** Where someone is in their day: each trip (made, under way, to come), what decides what's next, and the rule their day was drawn up by. */
function Day({ day, owner }: { day: DayView; owner?: number }) {
  return (
    <>
      <h3>{owner !== undefined ? `Its driver's day (#${owner})` : 'Their day'}</h3>
      <p className="decides">{day.decides}</p>
      <ol className="day">
        {day.entries.map((e, i) => (
          <li key={i} className={e.status}>
            <span className="mark">{dayMark(e.status)}</span>
            <span className="when">{e.tomorrow ? `tmrw ${e.time}` : e.time}</span>
            <span className="what">
              {e.what}
              {e.mode && <span className="sub"> {e.mode}</span>}
              {e.note && <span className="note">{e.note}</span>}
            </span>
          </li>
        ))}
        {day.entries.length === 0 && <li className="later">Nothing planned.</li>}
      </ol>
      <p className="rule">
        <b>{day.who}.</b> {day.rule}
      </p>
    </>
  );
}

/** A decision tree: each question, its answer (✓ yes, ✗ no; a fact unmarked), what it went by, and the questions under it. */
function Tree({ items }: { items: Why[] }) {
  return (
    <ul className="tree">
      {items.map((w, i) => (
        <li key={i}>
          <span className={`mark ${w.yes === undefined ? '' : w.yes ? 'good' : 'bad'}`} aria-label={w.yes === undefined ? undefined : w.yes ? 'yes' : 'no'}>
            {mark(w)}
          </span>
          <div>
            <span className="q">{w.q}</span> <span className="a">{w.a}</span>
            {w.note && <span className="note">{w.note}</span>}
            {w.under && <Tree items={w.under} />}
          </div>
        </li>
      ))}
    </ul>
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

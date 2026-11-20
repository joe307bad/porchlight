// Beside the map: the feed's state, the county's figures, what's shown, the
// towns with anything going on, and everyone playing (what they're doing, and
// who and what is round them). Read from the World four times a second.

import { useEffect, useRef, useState } from 'react';
import type { CountyMap, Me } from './api.ts';
import type { Feed, FeedStatus } from './feed.ts';
import { KMH, PERSON, SPEEDS, type Layers, type MapRenderer } from './render.ts';
import { REACH, STOPPED, doing, type World } from './world.ts';
import { BALANCED, JOBS_PER_HOME, ZONES, landUse } from './zoning.ts';
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

const LAYER_NAMES: [keyof Layers, string][] = [
  ['cars', 'Cars'],
  ['people', 'Townsfolk'],
  ['players', 'Players'],
  ['names', 'Names'],
  ['reach', `Who's near whom (${REACH} tiles)`],
  ['roads', 'Streets'],
  ['zoning', 'Land use'],
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
          {(p.focus || p.focusNote) && (
            <FocusView
              f={p.focus}
              note={p.focusNote}
              town={p.focus ? (p.map.towns.find((t) => t.key === p.focus!.town)?.name ?? '') : ''}
              following={p.followingFocus}
              onFollow={p.onFollowFocus}
              onClose={p.onCloseFocus}
              onFocus={p.onFocus}
            />
          )}
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
                const cars = n((r) => r.cars.out);
                let stopped = 0;
                for (const r of live) for (let i = 0; i < r.cars.n; i++) if (r.cars.shown[i] && r.cars.ts[i] < STOPPED) stopped++;
                return (
                  <li key={t.key}>
                    <button className="row" onClick={() => p.onTown(t)} title="Show it on the map">
                      <span className="grow">
                        <span className="name">{t.name}</span>
                        <span className="sub">
                          {live.length === 0
                            ? 'Nobody there'
                            : `${n((r) => r.players.length)} playing · ${cars} cars${cars ? ` (${stopped} stopped)` : ''} · ${n((r) => r.people.out)} townsfolk out`}
                        </span>
                      </span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </section>

          <Life map={p.map} lives={p.lives} building={p.building} onClose={p.onCloseBuilding} onTown={p.onTown} onFocus={p.onFocus} />
          <LandUse map={p.map} onTown={p.onTown} />

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

/** Each town's day so far, as the simulation has it: who's where, how they got about, and who's in the building clicked. */
function Life({
  map,
  lives,
  building,
  onClose,
  onTown,
  onFocus,
}: {
  map: CountyMap;
  lives: Map<string, TownLife>;
  building: BuildingLife | null;
  onClose: () => void;
  onTown: (t: CountyMap['towns'][number]) => void;
  onFocus: (t: FocusTarget) => void;
}) {
  const towns = map.towns.filter((t) => lives.has(t.key));
  const name = (key: string) => map.towns.find((t) => t.key === key)?.name ?? key;
  return (
    <section aria-label="Town life">
      <h2>Town life</h2>
      {towns.length === 0 ? (
        <p className="muted small">Shown when the server's running the towns: everyone's day, by the county clock.</p>
      ) : (
        <ul className="list">
          {towns.map((t) => {
            const life = lives.get(t.key)!;
            const f = figures(life);
            return (
              <li key={t.key}>
                <button className="row" onClick={() => onTown(t)} title="Show it on the map">
                  <span className="grow">
                    <span className="name">
                      {t.name} · {life.time}
                    </span>
                    <span className="sub">
                      {f.people.toLocaleString()} people: {f.indoors.toLocaleString()} indoors · {f.out.toLocaleString()} out · {f.offMap.toLocaleString()} out of town
                    </span>
                    <span className="sub">
                      {f.made.toLocaleString()} trips made{f.failed ? `, ${f.failed.toLocaleString()} couldn't be` : ''} ·{' '}
                      {MODES.map((m, i) => `${m} ${Math.round(f.shares[i] * 100)}%${f.meanMinutes[i] !== null ? ` (${f.meanMinutes[i]!.toFixed(1)} min)` : ''}`).join(' · ')}
                    </span>
                    <span className="sub">
                      {f.busiestHour >= 0 ? `busiest ${String(f.busiestHour).padStart(2, '0')}:00 · ` : ''}
                      {Math.round(f.parked * 100)}% of kerb spots taken
                      {life.summary.waits.length ? ` · ${life.summary.waits.length} slow junction${life.summary.waits.length === 1 ? '' : 's'}` : ''}
                    </span>
                  </span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
      {building && (
        <div className="building" aria-label="Who's in the building">
          <p className="name">
            {building.inside.length === 0 ? 'Nobody in' : `${building.inside.length}${building.inside.length >= 50 ? '+' : ''} inside`} · {name(building.town)}{' '}
            <button className="btn btn-small" onClick={onClose}>
              Close
            </button>
          </p>
          <ul className="sub inside">
            {describeInside(building).map((line, i) => (
              <li key={line}>
                <button className="link" onClick={() => onFocus({ town: building.town, kind: 'person', id: building.inside[i].id })} title="Focus on them: why they do what they do, and what's next">
                  {line}
                </button>
              </li>
            ))}
          </ul>
        </div>
      )}
      <p className="muted small">The discs on the buildings are the people inside; click one to see who. Click a car or townsperson on the map (or someone listed) to focus on them.</p>
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

/** What each town's buildings are for: homes and work, made with the town from its seed. */
function LandUse({ map, onTown }: { map: CountyMap; onTown: (t: CountyMap['towns'][number]) => void }) {
  const towns = map.towns.map((t) => ({ t, u: landUse(t) }));
  return (
    <section aria-label="Land use">
      <h2>Land use</h2>
      <p className="muted small">
        Every building is a home, a workplace or both, set when the town is made from its seed. Each town aims for a job for every working resident, about{' '}
        {JOBS_PER_HOME} a household; planners count {BALANCED.low}–{BALANCED.high} as balanced.
      </p>
      <ul className="list">
        {towns.map(({ t, u }) => (
          <li key={t.key}>
            <button className="row" onClick={() => onTown(t)} title="Show it on the map">
              <span className="grow">
                <span className="name">{t.name}</span>
                {!u ? (
                  <span className="sub">Not zoned yet: shown once the simulation has made the town.</span>
                ) : (
                  <>
                    <span className="sub">
                      {u.residential.toLocaleString()} residential · {u.business.toLocaleString()} business · {u.mixed.toLocaleString()} mixed · {(u.byZone[5] ?? 0).toLocaleString()} civic
                    </span>
                    <span className="zone-bar" aria-hidden="true">
                      {u.byZone.map((n, z) =>
                        n ? <i key={z} style={{ flexGrow: n, background: ZONES[z].color }} title={`${ZONES[z].label}: ${n.toLocaleString()}`} /> : null,
                      )}
                    </span>
                    <span className="sub">
                      {u.homes.toLocaleString()} homes ({u.residents.toLocaleString()} people, {u.workers.toLocaleString()} at work) · {u.jobs.toLocaleString()} jobs
                    </span>
                    <span className={`sub ${u.balance === 'balanced' ? 'good' : 'bad'}`}>
                      {u.jobsPerHome.toFixed(2)} jobs a home, {u.balance}
                    </span>
                  </>
                )}
              </span>
            </button>
          </li>
        ))}
      </ul>
      <div className="legend">
        {ZONES.map((z) => (
          <span key={z.label}>
            <i style={{ background: z.color }} /> {z.label}
          </span>
        ))}
      </div>
    </section>
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

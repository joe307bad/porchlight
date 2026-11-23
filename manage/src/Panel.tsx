// Beside the map, as simple as it can be, everything on one line: at the top
// whatever's open (a car or townsperson clicked: what it is and what it'll do
// next; a building: who's inside; a town: its people, as a table; a kind of
// thing on the map: each one out now), then a line a town, the kinds of car and
// person (show or hide each, click one to list them), what else the map shows,
// and anyone playing. No explanations or keys in the panel itself: the ⓘ in its
// head opens them (KeyModal). Read from the World four times a second.

import { useEffect, useRef, useState } from 'react';
import { briefOn, congestionOf } from './api.ts';
import type { CountyMap, Me } from './api.ts';
import type { Feed, FeedStatus } from './feed.ts';
import { ALL_WAY, LAMPS, PERSON, SPEEDS, STOP_SIGN, type Layers, type MapRenderer } from './render.ts';
import { REACH, STOPPED, doing, type World } from './world.ts';
import { ZONES } from './zoning.ts';
import {
  ENTITY_KINDS,
  MODES,
  POPULATION_COLUMNS,
  POPULATION_KINDS,
  describeInside,
  figures,
  heldText,
  holdBars,
  ofKind,
  untilText,
  type BuildingLife,
  type CongestedStreet,
  type Congestion,
  type EntityFocus,
  type FocusTarget,
  type TownLife,
} from './life.ts';

const REFRESH_MS = 250;
/** The most of a kind listed at once (each with its line from the simulation). */
const LISTED = 40;

type Kind = (typeof ENTITY_KINDS)[number];

interface Props {
  who: Me;
  world: World;
  map: CountyMap;
  status: FeedStatus;
  feed: Feed | null;
  renderer: MapRenderer | null;
  layers: Layers;
  onLayers: (l: Layers) => void;
  /** The kinds of car and person hidden on the map (ENTITY_KINDS' ids). */
  hidden: Set<string>;
  onHidden: (h: Set<string>) => void;
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
  /** Focus on whoever's drawn in a town's car or person record now (a kind's list). */
  onFocusSlot: (key: string, kind: 'car' | 'person', slot: number) => void;
  /** A town's most congested streets to draw on the map, at the hour picked (null: none). */
  onCongestion: (key: string | null, streets: CongestedStreet[] | null) => void;
  /** Take the map to one of a town's streets. */
  onStreet: (key: string, points: number[]) => void;
}

/** How often a town's congestion is asked for again while it's open (ms). */
const CONGESTION_MS = 10_000;

/** Each thing the map can show besides the cars and people: its checkbox's word, what it is in full, and its key (in the ⓘ). */
const LAYERS: { key: keyof Layers; short: string; label: string; legend?: { color: string; label: string; shape?: 'dot' | 'ring' | 'stop' }[] }[] = [
  { key: 'cars', short: 'Cars', label: 'Cars, by speed', legend: SPEEDS.map((s) => ({ color: s.color, label: s.label })) },
  { key: 'people', short: 'People', label: 'Townsfolk', legend: [{ color: PERSON, label: 'on foot' }] },
  {
    key: 'signals',
    short: 'Lights',
    label: 'Traffic lights',
    legend: [
      { color: LAMPS.green, label: 'go', shape: 'dot' },
      { color: LAMPS.amber, label: 'ending', shape: 'dot' },
      { color: LAMPS.red, label: 'stop', shape: 'dot' },
    ],
  },
  {
    key: 'stops',
    short: 'Stop signs',
    label: 'Stop signs',
    legend: [
      { color: STOP_SIGN, label: 'stop for the bigger road', shape: 'stop' },
      { color: ALL_WAY, label: 'all-way stop', shape: 'stop' },
    ],
  },
  { key: 'zoning', short: 'Land use', label: 'Land use (a disc on each building, sized by the people inside)', legend: ZONES.map((z) => ({ color: z.color, label: z.label })) },
  { key: 'players', short: 'Players', label: 'Players', legend: [{ color: '#8ab4ff', label: 'player', shape: 'ring' }] },
  { key: 'roads', short: 'Streets', label: 'Streets' },
  { key: 'names', short: 'Names', label: 'Town names' },
  { key: 'trails', short: 'Trails', label: 'Trails: where things have been lately' },
  { key: 'reach', short: 'Reach', label: `Reach: who's near whom (within ${REACH} tiles)` },
];

/** What's open at the top of the panel when nothing's clicked on the map: a town's table, or a kind's list. */
type Drill = { town: string } | { kind: string; town?: string } | null;

export function Panel(p: Props) {
  const [, setTick] = useState(0);
  const [open, setOpen] = useState(true);
  const [key, setKey] = useState(false);
  const [drill, setDrill] = useState<Drill>(null);
  useEffect(() => {
    const t = setInterval(() => setTick((n) => n + 1), REFRESH_MS);
    return () => clearInterval(t);
  }, []);
  const players = p.world.players().filter((x) => x.seen);
  const rate = useRate(p.feed);
  const townName = (key: string) => p.map.towns.find((t) => t.key === key)?.name ?? key;
  const simMissing = simMissingNote(p.map.sim, p.lives.size > 0, p.livesAsked);
  const toggle = (id: string) => {
    const h = new Set(p.hidden);
    if (h.has(id)) h.delete(id);
    else h.add(id);
    p.onHidden(h);
  };
  const drilledTown = drill && !('kind' in drill) ? p.map.towns.find((t) => t.key === drill.town) : undefined;
  const drilledKind = drill && 'kind' in drill ? ENTITY_KINDS.find((k) => k.id === drill.kind) : undefined;

  return (
    <aside className={`panel ${open ? '' : 'panel-closed'}`}>
      <header className="panel-head">
        <h1>Manage</h1>
        <Status status={p.status} />
        <button className="icon-btn" onClick={() => setKey(true)} aria-label="Key: what the map shows" title="Key: what the map shows">
          ⓘ
        </button>
        <button className="btn btn-small panel-toggle" onClick={() => setOpen(!open)} aria-expanded={open}>
          {open ? 'Hide' : 'Show'}
        </button>
      </header>
      {key && <KeyModal sim={simMissing} onClose={() => setKey(false)} />}
      {open && (
        <div className="panel-body">
          {simMissing && (
            <button className="warn line" role="status" onClick={() => setKey(true)} title="Why, and how to start it">
              {simMissing.title}
            </button>
          )}
          {/* What's open, always at the top: what was clicked on the map, or else a town or a kind drilled into. */}
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
          ) : drilledTown ? (
            <TownView
              name={drilledTown.name}
              townKey={drilledTown.key}
              life={p.lives.get(drilledTown.key)}
              onKind={(kind) => setDrill({ kind, town: drilledTown.key })}
              onCongestion={p.onCongestion}
              onStreet={p.onStreet}
              onClose={() => setDrill(null)}
            />
          ) : drilledKind ? (
            <KindList
              kind={drilledKind}
              lives={p.lives}
              town={drill && 'kind' in drill ? drill.town : undefined}
              towns={p.map.towns}
              onTown={(town) => setDrill({ kind: drilledKind.id, town })}
              townName={townName}
              onFocusSlot={p.onFocusSlot}
              onClose={() => setDrill(null)}
            />
          ) : null}

          <section aria-label="Towns">
            <h2>Towns</h2>
            <ul className="list">
              {p.map.towns.map((t) => {
                const life = p.lives.get(t.key);
                const on = drilledTown?.key === t.key;
                return (
                  <li key={t.key} className={on ? 'sel' : ''}>
                    <button
                      className="row line"
                      onClick={() => {
                        p.onTown(t);
                        setDrill(on ? null : { town: t.key });
                      }}
                      title={[t.name, ...townLines(p.world, t.key, life)].join('\n')}
                    >
                      <span className="name">{t.name}</span>
                      {life && <span className="time">{life.time}</span>}
                      <span className="figs">{townFigures(p.world, t.key, life)}</span>
                    </button>
                  </li>
                );
              })}
            </ul>
          </section>

          <section aria-label="On the map">
            <h2>On the map</h2>
            <ul className="list kinds">
              {ENTITY_KINDS.map((k) => {
                const n = ofKind(p.lives, k).length;
                const shown = !p.hidden.has(k.id) && (k.of === 'car' ? p.layers.cars : p.layers.people);
                return (
                  <li key={k.id} className={drilledKind?.id === k.id ? 'sel' : ''}>
                    <span className="row line">
                      <input type="checkbox" checked={!p.hidden.has(k.id)} onChange={() => toggle(k.id)} aria-label={`Show ${k.label.toLowerCase()}`} />
                      <button className={`link name ${shown ? '' : 'muted'}`} onClick={() => setDrill(drilledKind?.id === k.id ? null : { kind: k.id, town: drilledTown?.key })} title="List each of them out now">
                        {k.label}
                      </button>
                      <span className="figs">{n.toLocaleString()}</span>
                    </span>
                  </li>
                );
              })}
            </ul>
          </section>

          <section aria-label="Show">
            <h2>Show</h2>
            <ul className="layer-grid">
              {LAYERS.map((l) => (
                <li key={l.key}>
                  <label className="check line" title={l.label}>
                    <input type="checkbox" checked={p.layers[l.key]} onChange={(e) => p.onLayers({ ...p.layers, [l.key]: e.target.checked })} />
                    {l.short}
                  </label>
                </li>
              ))}
            </ul>
          </section>

          {players.length > 0 && (
            <section aria-label="Players">
              <h2>Playing</h2>
              <ul className="list">
                {players.map((pl) => (
                  <li key={pl.sid} className={pl.sid === p.selected ? 'sel' : ''}>
                    <button
                      className="row line"
                      onClick={() => p.onFollow(p.following === pl.sid ? null : pl.sid)}
                      title={`${pl.name}: ${doing(pl)} in ${pl.town}. ${p.following === pl.sid ? 'Stop following' : 'Follow them on the map'}`}
                    >
                      <span className="swatch" style={{ background: pl.color, borderRadius: pl.car > 0 ? 2 : 99 }} />
                      <span className="name">{pl.name}</span>
                      <span className="figs">{pl.town}</span>
                      <span className="follow">{p.following === pl.sid ? 'Following' : 'Follow'}</span>
                    </button>
                  </li>
                ))}
              </ul>
            </section>
          )}

          <footer className="foot muted line">
            {p.who?.name ? `${p.who.name} · ` : ''}
            {rate} · {p.renderer?.fps ?? 0} fps
          </footer>
        </div>
      )}
    </aside>
  );
}

/** A town in a few words, for its one line: its people, who's out, and its cars on the road. */
export function townFigures(world: World, key: string, life: TownLife | undefined): string {
  let cars = 0;
  let running = false;
  for (const r of world.order) {
    if (r.meta.key !== key) continue;
    running = true;
    for (let i = 0; i < r.cars.n; i++) if (r.cars.shown[i]) cars++;
  }
  if (!life) return running ? `${cars.toLocaleString()} cars` : 'off';
  const f = figures(life);
  return `${f.people.toLocaleString()} · ${f.out.toLocaleString()} out · ${cars.toLocaleString()} cars`;
}

/** A town's figures in a line or two (its row's tooltip): who's out, the cars on its roads, and how its day's going. */
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

/** A town drilled into: who its people are and where they are now, and its day so far, as tables. */
function TownView({
  name,
  townKey,
  life,
  onKind,
  onCongestion,
  onStreet,
  onClose,
}: {
  name: string;
  townKey: string;
  life: TownLife | undefined;
  onKind: (kind: string) => void;
  onCongestion: Props['onCongestion'];
  onStreet: Props['onStreet'];
  onClose: () => void;
}) {
  const here = life ? new Map([[townKey, life]]) : new Map<string, TownLife>();
  const rows = life?.summary.population ?? [];
  const totals = POPULATION_COLUMNS.map((_, c) => rows.reduce((n, r) => n + (r[c + 1] as number), 0));
  const f = life ? figures(life) : null;
  return (
    <section className="focus" aria-label="Town">
      <header className="focus-head line">
        <h2>
          {name}
          {life && <span className="time"> · {life.time}</span>}
        </h2>
        <button className="icon-btn" onClick={onClose} aria-label="Close">
          ✕
        </button>
      </header>
      {!life ? (
        <p className="sub line">Not running</p>
      ) : (
        <>
          <div className="table-wrap">
            <table className="table" aria-label="Population">
              <thead>
                <tr>
                  <th />
                  {POPULATION_COLUMNS.map((c) => (
                    <th key={c}>{c}</th>
                  ))}
                </tr>
              </thead>
              <tbody>
                {rows.map((r) => (
                  <tr key={r[0]}>
                    <th>{POPULATION_KINDS[r[0]] ?? r[0]}</th>
                    {r.slice(1).map((v, i) => (
                      <td key={i}>{(v as number).toLocaleString()}</td>
                    ))}
                  </tr>
                ))}
                <tr className="total">
                  <th>All</th>
                  {totals.map((v, i) => (
                    <td key={i}>{v.toLocaleString()}</td>
                  ))}
                </tr>
              </tbody>
            </table>
          </div>
          {f && (
            <div className="table-wrap">
              <table className="table" aria-label="Today">
                <thead>
                  <tr>
                    <th>Today</th>
                    {MODES.map((m) => (
                      <th key={m}>{m === 'drive' ? 'Car' : m === 'walk' ? 'Walk' : 'Bus'}</th>
                    ))}
                  </tr>
                </thead>
                <tbody>
                  <tr>
                    <th>Share of trips</th>
                    {f.shares.map((s, i) => (
                      <td key={i}>{Math.round(s * 100)}%</td>
                    ))}
                  </tr>
                  <tr>
                    <th>Mean trip (min)</th>
                    {f.meanMinutes.map((m, i) => (
                      <td key={i}>{m === null ? '–' : m.toFixed(1)}</td>
                    ))}
                  </tr>
                </tbody>
              </table>
              <table className="table" aria-label="Day">
                <tbody>
                  <tr>
                    <th>Trips made</th>
                    <td>{f.made.toLocaleString()}</td>
                    <th>Failed</th>
                    <td>{f.failed.toLocaleString()}</td>
                  </tr>
                  <tr>
                    <th>Kerb spots taken</th>
                    <td>{Math.round(f.parked * 100)}%</td>
                    <th>Busiest hour</th>
                    <td>{f.busiestHour < 0 ? '–' : `${String(f.busiestHour).padStart(2, '0')}:00`}</td>
                  </tr>
                </tbody>
              </table>
            </div>
          )}
          <CongestionView townKey={townKey} onCongestion={onCongestion} onStreet={onStreet} />
          <ul className="list kinds" aria-label={`On the map in ${name}`}>
            {ENTITY_KINDS.map((k) => {
              const n = ofKind(here, k).length;
              return (
                <li key={k.id}>
                  <button className="row line" onClick={() => onKind(k.id)} disabled={n === 0} title={`List ${name}'s ${k.label.toLowerCase()} out now`}>
                    <span className="name">{k.label}</span>
                    <span className="figs">{n.toLocaleString()}</span>
                  </button>
                </li>
              );
            })}
          </ul>
        </>
      )}
    </section>
  );
}

/**
 * A town's congestion over the last day: a bar an hour (how long cars were held up in all), a slider to pick one,
 * and that hour's worst streets, drawn red on the map and numbered; click one to go to it.
 */
function CongestionView({ townKey, onCongestion, onStreet }: { townKey: string; onCongestion: Props['onCongestion']; onStreet: Props['onStreet'] }) {
  const [ago, setAgo] = useState(0);
  const [c, setC] = useState<Congestion | null>(null);
  useEffect(() => {
    let live = true;
    const ask = () => void congestionOf(townKey, ago).then((a) => live && setC(a));
    ask();
    const t = setInterval(ask, CONGESTION_MS);
    return () => {
      live = false;
      clearInterval(t);
    };
  }, [townKey, ago]);
  useEffect(() => {
    onCongestion(c && c.ago === ago ? c.town : null, c && c.ago === ago ? c.streets : null);
  }, [c, ago, onCongestion]);
  useEffect(() => () => onCongestion(null, null), [onCongestion]);
  if (!c) return null;
  const bars = holdBars(c.hours);
  const at = c.hours.length - 1 - ago;
  const hour = c.hours[at];
  const until = ago === 0 ? 'now' : c.hours[at + 1]?.time ?? '';
  return (
    <div className="congestion" aria-label="Congestion">
      <h2 className="line">Congestion</h2>
      <div className="bars" role="group" aria-label="Hold-ups each hour">
        {bars.map((b, i) => (
          <button
            key={i}
            className={`bar ${i === at ? 'on' : ''}`}
            style={{ height: `${Math.max(2, Math.round(b * 28))}px` }}
            onClick={() => setAgo(c.hours.length - 1 - i)}
            title={`${c.hours[i].time}: ${heldText(c.hours[i].hold)} held up, ${c.hours[i].cars.toLocaleString()} cars`}
            aria-label={`${c.hours[i].time}`}
          />
        ))}
      </div>
      <input
        type="range"
        className="hour-slider"
        min={0}
        max={c.hours.length - 1}
        value={at}
        onChange={(e) => setAgo(c.hours.length - 1 - Number(e.target.value))}
        aria-label="Hour"
        aria-valuetext={hour ? `${hour.time} to ${until}` : ''}
      />
      {hour && (
        <p className="sub line">
          {hour.time}–{until} · {heldText(hour.hold)} held up · {hour.cars.toLocaleString()} cars
        </p>
      )}
      <ol className="list">
        {c.streets.map((s, n) => (
          <li key={s.road}>
            <button className="row line" onClick={() => onStreet(townKey, s.points)} title={`${heldText(s.hold)} held up in all, ${heldText(s.mean)} a car`}>
              <span className="name">
                {n + 1}. {s.kind === 0 ? 'Main road' : 'Street'} {s.road}
              </span>
              <span className="figs">
                {heldText(s.hold)} · {s.cars} cars
              </span>
            </button>
          </li>
        ))}
        {c.streets.length === 0 && <li className="sub line">Nobody held up</li>}
      </ol>
    </div>
  );
}

/** A kind of thing on the map drilled into: each one out now, a line each (the simulation's word on it); click one to focus on it. */
function KindList({
  kind,
  lives,
  town,
  towns,
  onTown,
  townName,
  onFocusSlot,
  onClose,
}: {
  kind: Kind;
  lives: Map<string, TownLife>;
  /** Only this town's, or every town's. */
  town: string | undefined;
  towns: CountyMap['towns'];
  onTown: (town: string | undefined) => void;
  townName: (key: string) => string;
  onFocusSlot: (key: string, kind: 'car' | 'person', slot: number) => void;
  onClose: () => void;
}) {
  const everywhere = ofKind(lives, kind);
  const all = town ? everywhere.filter(([k]) => k === town) : everywhere;
  const listed = all.slice(0, LISTED);
  const [lines, setLines] = useState<Map<string, string>>(new Map());
  const asked = useRef(new Set<string>());
  const ids = listed.map(([k, i]) => `${k}|${i}`).join(',');
  useEffect(() => {
    asked.current.clear();
    setLines(new Map());
  }, [kind.id, town]);
  useEffect(() => {
    for (const [k, i] of listed) {
      const id = `${k}|${i}`;
      if (asked.current.has(id)) continue;
      asked.current.add(id);
      void briefOn(k, kind.of, i).then((line) => line && setLines((m) => new Map(m).set(id, line)));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [ids]);
  return (
    <section className="focus" aria-label={kind.label}>
      <header className="focus-head line">
        <h2>
          {kind.label} · {all.length.toLocaleString()}
        </h2>
        <select className="town-pick" value={town ?? ''} onChange={(e) => onTown(e.target.value || undefined)} aria-label="Which town">
          <option value="">All towns</option>
          {towns
            .filter((t) => lives.has(t.key))
            .map((t) => (
              <option key={t.key} value={t.key}>
                {t.name} ({everywhere.filter(([k]) => k === t.key).length})
              </option>
            ))}
        </select>
        <button className="icon-btn" onClick={onClose} aria-label="Close">
          ✕
        </button>
      </header>
      <ul className="list">
        {listed.map(([k, i]) => {
          const line = lines.get(`${k}|${i}`);
          return (
            <li key={`${k}|${i}`}>
              <button className="row line" onClick={() => onFocusSlot(k, kind.of, i)} title={line ?? ''}>
                {!town && <span className="figs">{townName(k)}</span>}
                <span className="name">{line ?? '…'}</span>
              </button>
            </li>
          );
        })}
        {all.length === 0 && <li className="sub line">None out now</li>}
        {all.length > LISTED && <li className="sub line">and {(all.length - LISTED).toLocaleString()} more</li>}
      </ul>
    </section>
  );
}

/** The key, and what the panel leaves out to keep to a line: what each colour and shape on the map means, how to look closer, and (if so) why there's no traffic. */
function KeyModal({ sim, onClose }: { sim: SimNote | null; onClose: () => void }) {
  useEffect(() => {
    const esc = (e: KeyboardEvent) => e.key === 'Escape' && onClose();
    window.addEventListener('keydown', esc);
    return () => window.removeEventListener('keydown', esc);
  }, [onClose]);
  return (
    <div className="modal-back" onClick={onClose}>
      <div className="modal" role="dialog" aria-modal="true" aria-label="Key" onClick={(e) => e.stopPropagation()}>
        <header className="modal-head">
          <h2>Key</h2>
          <button className="icon-btn" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </header>
        {sim && (
          <section className="warn">
            <h3>{sim.title}</h3>
            <p className="sub">{sim.body}</p>
          </section>
        )}
        <h3>Looking closer</h3>
        <p className="sub">
          Click a <b>car</b> or <b>townsperson</b> on the map for what it is and what it'll do next, or a <b>building</b> for who's inside. Click a{' '}
          <b>town</b> for its people as a table, and a kind under <b>On the map</b> for each of them out now; its box shows or hides them. Zoom in to see
          the traffic lights and stop signs.
        </p>
        {LAYERS.filter((l) => l.legend).map((l) => (
          <div key={l.key} className="key-row">
            <h3>{l.label}</h3>
            <span className="legend">
              {l.legend!.map((k) => (
                <span key={k.label}>
                  <i className={k.shape ?? ''} style={{ background: k.color }} /> {k.label}
                </span>
              ))}
            </span>
          </div>
        ))}
        <h3>On the map</h3>
        <ul className="sub key-list">
          <li>
            <b>Residents' cars</b>: the town's own, driven by those who live there
          </li>
          <li>
            <b>Commuters' cars</b>: driven in from out of town to work
          </li>
          <li>
            <b>Through traffic</b>: driving across town, gate to gate
          </li>
          <li>
            <b>Vans on their rounds</b>: deliveries, tradespeople, the post and taxis, day and night
          </li>
          <li>
            <b>Buses</b> on their routes, and <b>boats</b> on the water
          </li>
          <li>
            <b>Workers</b>, <b>students</b>, and those <b>out on errands</b> (at home in the day), on foot
          </li>
          <li>
            <b>Commuters on foot</b>, to and from their cars; <b>wanderers</b> stroll with no day of their own
          </li>
        </ul>
        <h3>The rest</h3>
        <ul className="sub key-list">
          {LAYERS.filter((l) => !l.legend).map((l) => (
            <li key={l.key}>
              <b>{l.short}</b>: {l.label}
            </li>
          ))}
          <li>
            <b>Towns</b>: people · out now · cars on the road
          </li>
        </ul>
      </div>
    </div>
  );
}

interface SimNote {
  title: string;
  body: string;
}

/**
 * When the server isn't running the towns' life: everything the panel shows past the map's streets (cars, townsfolk,
 * lights, stop signs, who's inside, a click's why) comes from it. Its title goes on a line at the top; the why and the
 * how-to, in the key.
 */
export function simMissingNote(sim: boolean | undefined, running: boolean, asked: boolean): SimNote | null {
  if (running || (sim !== false && !asked)) return null;
  return sim === false
    ? {
        title: 'No town simulation on this server',
        body: "The cars, townsfolk, traffic lights, stop signs, who's in each building and what you click on all come from the server's town simulation (sim/, .NET 8), and this server hasn't got it. Install the .NET SDK (8 or newer: brew install --cask dotnet-sdk on a Mac), then start ./dev.sh again: it builds it.",
      }
    : {
        title: 'No town is running yet',
        body: "The server's town simulation hasn't started any towns, so there are no cars, townsfolk, lights or stop signs yet. A town takes a few seconds to start; if this stays, look for [sim] lines in the server's output.",
      };
}

/** The building clicked: who's inside, a line each, and when each goes out next; click someone to focus on them. */
function Building({ building, town, onClose, onFocus }: { building: BuildingLife; town: string; onClose: () => void; onFocus: (t: FocusTarget) => void }) {
  const ref = useRef<HTMLElement>(null);
  useEffect(() => ref.current?.scrollIntoView?.({ block: 'start', behavior: 'smooth' }), [building]);
  return (
    <section className="focus" aria-label="Who's in the building" ref={ref}>
      <header className="focus-head line">
        <h2>
          {building.inside.length === 0 ? 'Nobody in' : `${building.inside.length}${building.inside.length >= 50 ? '+' : ''} inside`} · {town}
        </h2>
        <button className="icon-btn" onClick={onClose} aria-label="Close">
          ✕
        </button>
      </header>
      <ul className="list">
        {describeInside(building).map((line, i) => (
          <li key={line}>
            <button className="row line" onClick={() => onFocus({ town: building.town, kind: 'person', id: building.inside[i].id })} title={line}>
              <span className="name">{line}</span>
            </button>
          </li>
        ))}
      </ul>
    </section>
  );
}

/** The next things a focus lists (what it's going to do). */
const NEXT = 4;

/** One car or townsperson: what it is, what it's doing, and what it'll do next. Nothing more. */
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
        <header className="focus-head line">
          <span className="sub">{note ?? 'Looking…'}</span>
          <button className="icon-btn" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </header>
      </section>
    );
  const what = f.kind === 'person' ? 'Person' : f.bus !== undefined && f.bus >= 0 ? 'Bus' : f.van !== undefined && f.van >= 0 ? 'Van' : 'Car';
  const next = f.queue.filter((q) => !q.now).slice(0, NEXT);
  return (
    <section className="focus" aria-label="Focus" ref={ref}>
      <header className="focus-head line">
        <h2 title={f.who}>
          {what} #{f.kind === 'car' && f.van !== undefined && f.van >= 0 ? f.van : f.id} · {town}
        </h2>
        <button className={`icon-btn ${following ? 'on' : ''}`} onClick={() => onFollow(!following)} aria-pressed={following} aria-label="Follow" title={following ? 'Following' : 'Follow'}>
          ◎
        </button>
        {f.kind === 'person' && f.car !== undefined && f.car >= 0 && (
          <button className="icon-btn" onClick={() => onFocus({ town: f.town, kind: 'car', id: f.car! })} aria-label="Their car" title="Their car">
            🚗
          </button>
        )}
        {f.kind === 'car' && f.owner !== undefined && f.owner >= 0 && (
          <button className="icon-btn" onClick={() => onFocus({ town: f.town, kind: 'person', id: f.owner! })} aria-label="Its driver" title="Its driver">
            👤
          </button>
        )}
        <button className="icon-btn" onClick={onClose} aria-label="Close">
          ✕
        </button>
      </header>
      <p className="doing line" title={f.doing}>
        {f.doing}
      </p>
      {note && <p className="small bad line">{note}</p>}
      <ol className="queue">
        {next.map((item, i) => (
          <li key={i} className="line" title={item.what}>
            <span className="when">{untilText(item, f.clock)}</span>
            <span className="what">{item.what}</span>
          </li>
        ))}
        {next.length === 0 && <li className="sub line">Nothing next</li>}
      </ol>
    </section>
  );
}

function Status({ status }: { status: FeedStatus }) {
  switch (status.state) {
    case 'live':
      return (
        <p className="status live" title="Live">
          <i />
        </p>
      );
    case 'connecting':
      return <p className="status line">Connecting…</p>;
    case 'retrying':
      return <p className="status bad line" title={status.why}>{`Again in ${Math.ceil(status.inMs / 1000)} s`}</p>;
    case 'refused':
      return <p className="status bad line" title={status.why}>{status.why}</p>;
    case 'signedOut':
      return <p className="status bad line">Signed out</p>;
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
      setRate(`${f}/s · ${b < 1024 ? `${b} B` : `${(b / 1024).toFixed(1)} KB`}/s`);
    }, 1000);
    return () => clearInterval(t);
  }, [feed]);
  return rate;
}

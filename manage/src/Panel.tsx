// Beside the map, as little as it can be, as breadcrumbs (crumbs.ts):
//
//   County                 the towns, each with its people
//   › Lanternport          its congestion (an hour at a time, its worst streets by
//                          name) and, under it, everything out in it now, a tab a sort
//   › Car 12               what's clicked: followed on the map, what it's doing and
//   › Person 40            what's next; a building, who's inside. On from there:
//                          a driver's car, someone in a building.
//
// Going back to a crumb picks up as it was left. No boxes that scroll on their
// own: only the sidebar does. What the map shows (its layers, the kinds of car
// and person) is in the ⓘ, with the key. Read from the World four times a second.

import { useEffect, useRef, useState } from 'react';
import { briefOn, congestionOf } from './api.ts';
import type { CountyMap, Me } from './api.ts';
import type { FeedStatus } from './feed.ts';
import { ALL_WAY, LAMPS, PERSON, SPEEDS, STOP_SIGN, type Layers } from './render.ts';
import { REACH, doing, type PlayerView } from './world.ts';
import { ZONES } from './zoning.ts';
import { editableTowns, openEditor } from './edit/api.ts';
import { ENTITY_KINDS, describeInside, heldText, holdBars, streetBetween, streetName, streetText, untilText, type BuildingLife, type CongestedStreet, type Congestion, type EntityFocus, type FocusTarget, type TownLife, marketText } from './life.ts';
import { CALENDAR, CONGESTION, PAGE, TABS, crumbLabel, population, tabEntities, type Crumb, type Memory, type TownMemory } from './crumbs.ts';

const REFRESH_MS = 250;
/** What a service vehicle's called in its focus heading (sim's ServiceKind). */
const SERVICE_NAMES: Record<string, string> = { Bins: 'Bin lorry', Ambulance: 'Ambulance', FireEngine: 'Fire engine' };

interface Props {
  /** A person's name, once heard (their focus): for their crumb. */
  personName: (town: string, id: number) => string | undefined;
  who: Me;
  map: CountyMap;
  status: FeedStatus;
  layers: Layers;
  onLayers: (l: Layers) => void;
  /** The kinds of car and person hidden on the map (ENTITY_KINDS' ids). */
  hidden: Set<string>;
  onHidden: (h: Set<string>) => void;
  /** Each town's life the simulation's running, by key. */
  lives: Map<string, TownLife>;
  /** Whether the towns' life has been asked for yet. */
  livesAsked: boolean;
  /** Where the sidebar is, and what each crumb was left showing. */
  crumbs: Crumb[];
  memory: Memory;
  onCrumb: (i: number) => void;
  onTown: (key: string) => void;
  /** A town's car or person record clicked in its list: followed, a crumb on. */
  onPick: (town: string, of: 'car' | 'person', slot: number) => void;
  /** Someone by who they are (a driver, their car, someone in a building): followed, a crumb on. */
  onFocus: (t: FocusTarget) => void;
  /** The building open, and who's in it. */
  building: BuildingLife | null;
  /** The car or person open (the simulation's latest on them), or a word on why there's none. */
  focus: EntityFocus | null;
  focusNote: string | null;
  followingFocus: boolean;
  onFollowFocus: (on: boolean) => void;
  /** A town's most congested streets to draw on the map, at the hour picked (null: none). */
  onCongestion: (key: string | null, streets: CongestedStreet[] | null) => void;
  /** Take the map to one of a town's streets. */
  onStreet: (key: string, points: number[]) => void;
  /** The players connected now (the feed's), and the one the map follows (their session), if any. */
  players: () => PlayerView[];
  following: string | null;
  /** Follow a player on the map (null: stop). */
  onFollow: (sid: string | null) => void;
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
  { key: 'names', short: 'Names', label: 'Town and street names' },
  { key: 'trails', short: 'Trails', label: 'Trails: where things have been lately' },
  { key: 'reach', short: 'Reach', label: `Reach: who's near whom (within ${REACH} tiles)` },
];

export function Panel(p: Props) {
  const [, setTick] = useState(0);
  const [open, setOpen] = useState(true);
  const [key, setKey] = useState(false);
  useEffect(() => {
    const t = setInterval(() => setTick((n) => n + 1), REFRESH_MS);
    return () => clearInterval(t);
  }, []);
  const townName = (key: string) => p.map.towns.find((t) => t.key === key)?.name ?? key;
  const simMissing = simMissingNote(p.map.sim, p.lives.size > 0, p.livesAsked);
  const top = p.crumbs[p.crumbs.length - 1];

  return (
    <aside className={`panel ${open ? '' : 'panel-closed'}`}>
      <header className="panel-head">
        <h1>Manage</h1>
        <Status status={p.status} />
        <button className="icon-btn" onClick={() => setKey(true)} aria-label="Key and what the map shows" title="Key and what the map shows">
          ⓘ
        </button>
        <button className="btn btn-small panel-toggle" onClick={() => setOpen(!open)} aria-expanded={open}>
          {open ? 'Hide' : 'Show'}
        </button>
      </header>
      {key && <KeyModal sim={simMissing} layers={p.layers} onLayers={p.onLayers} hidden={p.hidden} onHidden={p.onHidden} onClose={() => setKey(false)} />}
      {open && (
        <div className={`panel-body ${top.at === 'town' ? 'fill' : ''}`}>
          {simMissing && (
            <button className="warn line" role="status" onClick={() => setKey(true)} title="Why, and how to start it">
              {simMissing.title}
            </button>
          )}
          <nav className="crumbs" aria-label="Breadcrumbs">
            <ol>
              {p.crumbs.map((c, i) => (
                <li key={i}>
                  {i === p.crumbs.length - 1 ? (
                    <span aria-current="page">{crumbLabel(c, townName, p.personName)}</span>
                  ) : (
                    <button className="link" onClick={() => p.onCrumb(i)}>
                      {crumbLabel(c, townName, p.personName)}
                    </button>
                  )}
                </li>
              ))}
            </ol>
          </nav>
          {p.focusNote && top.at !== 'entity' && <p className="sub line">{p.focusNote}</p>}
          {top.at === 'county' && (
            <>
              <Towns towns={p.map.towns} lives={p.lives} picked={p.memory.town} onTown={p.onTown} />
              <Players players={p.players()} following={p.following} onFollow={p.onFollow} />
            </>
          )}
          {top.at === 'town' && (
            <TownView
              key={top.town}
              townKey={top.town}
              life={p.lives.get(top.town)}
              memory={p.memory}
              onPick={p.onPick}
              onCongestion={p.onCongestion}
              onStreet={p.onStreet}
            />
          )}
          {top.at === 'entity' && (
            <FocusView f={p.focus} note={p.focusNote} town={townName(top.town)} following={p.followingFocus} onFollow={p.onFollowFocus} onFocus={p.onFocus} />
          )}
          {top.at === 'building' && (p.building ? <Building building={p.building} onFocus={p.onFocus} /> : <p className="sub line">Looking…</p>)}
        </div>
      )}
    </aside>
  );
}

/** The county: its towns, a line each with its people. */
function Towns({ towns, lives, picked, onTown }: { towns: CountyMap['towns']; lives: Map<string, TownLife>; picked: string | null; onTown: (key: string) => void }) {
  return (
    <ul className="list" aria-label="Towns">
      {towns.map((t) => {
        const people = population(lives.get(t.key));
        return (
          <li key={t.key} className={picked === t.key ? 'sel' : ''}>
            <button className="row line" onClick={() => onTown(t.key)}>
              <span className="name">{t.name}</span>
              <span className="figs">{people === null ? '–' : people.toLocaleString()}</span>
            </button>
          </li>
        );
      })}
    </ul>
  );
}

/** The players connected now, a line each with their town; click one to follow them on the map (again to stop). */
function Players({ players, following, onFollow }: { players: PlayerView[]; following: string | null; onFollow: (sid: string | null) => void }) {
  const sorted = [...players].sort((a, b) => a.name.localeCompare(b.name) || a.sid.localeCompare(b.sid));
  return (
    <>
      <h2 className="line">Players{players.length > 0 ? ` · ${players.length}` : ''}</h2>
      {sorted.length === 0 ? (
        <p className="sub line">Nobody's playing right now.</p>
      ) : (
        <ul className="list" aria-label="Players">
          {sorted.map((pl) => {
            const on = following === pl.sid;
            const line = `${pl.name} · ${doing(pl)} in ${pl.town}`;
            return (
              <li key={pl.sid} className={on ? 'sel' : ''}>
                <button className="row line" onClick={() => onFollow(on ? null : pl.sid)} aria-pressed={on} title={on ? `Following ${pl.name} (click to stop)` : `Follow ${line}`}>
                  <i className="swatch player-dot" style={{ background: pl.color }} />
                  <span className="name">{pl.name}</span>
                  <span className="figs">{doing(pl)} · {pl.town}</span>
                </button>
              </li>
            );
          })}
        </ul>
      )}
    </>
  );
}

/**
 * A town: collapsible sections, as the game's own sidebar has them (Sidebar.cs): its congestion, then one for each
 * sort of thing out in it now, with how many. Opening one closes the others; the open one scrolls on its own between
 * the headings, which stay put above and below it, so the sidebar never scrolls past them. Click someone to follow.
 */
function TownView({
  townKey,
  life,
  memory,
  onPick,
  onCongestion,
  onStreet,
}: {
  townKey: string;
  life: TownLife | undefined;
  memory: Memory;
  onPick: Props['onPick'];
  onCongestion: Props['onCongestion'];
  onStreet: Props['onStreet'];
}) {
  // As it was left (Memory), and kept there as it changes.
  const [m, setM] = useState<TownMemory>(() => memory.of(townKey));
  const change = (patch: Partial<TownMemory>) => setM(memory.set(townKey, patch));
  const toggle = (id: string) => change({ open: m.open === id ? null : id });
  const counts = new Map(TABS.map((t) => [t.id, tabEntities(life, t.id).length]));
  const all = m.open && m.open !== CONGESTION ? tabEntities(life, m.open) : [];
  const listed = all.slice(0, m.shown);
  const lines = useBriefs(townKey, listed);
  const picked = useRef<HTMLLIElement>(null);
  // Back from someone clicked here: them, in view again.
  useEffect(() => {
    picked.current?.scrollIntoView?.({ block: 'center' });
  }, []);
  if (!life)
    return (
      <>
        <EditStreets townKey={townKey} />
        <p className="sub line">Not running</p>
      </>
    );
  return (
    <div className="sections">
      <EditStreets townKey={townKey} />
      {life.calendar && life.calendar.length > 0 && (
        <Section title="Calendar" open={m.open === CALENDAR} onToggle={() => toggle(CALENDAR)}>
          <CalendarView life={life} />
        </Section>
      )}
      <Section title="Congestion" open={m.open === CONGESTION} onToggle={() => toggle(CONGESTION)}>
        <CongestionView townKey={townKey} ago={m.ago} onAgo={(ago) => change({ ago })} onCongestion={onCongestion} onStreet={onStreet} />
      </Section>
      {TABS.filter((t) => counts.get(t.id)! > 0 || t.id === m.open).map((t) => (
        <Section key={t.id} title={t.label} figs={counts.get(t.id)!} open={m.open === t.id} onToggle={() => toggle(t.id)}>
          <ul className="list">
            {listed.map((e) => {
              const id = `${e.of}|${e.slot}`;
              const on = m.picked === id;
              return (
                <li key={id} className={on ? 'sel' : ''} ref={on ? picked : undefined}>
                  <button
                    className="row line"
                    onClick={() => {
                      change({ picked: id });
                      onPick(townKey, e.of, e.slot);
                    }}
                    title={lines.get(id) ?? e.kind}
                  >
                    <span className="name">{lines.get(id) ?? e.kind}</span>
                  </button>
                </li>
              );
            })}
            {all.length === 0 && <li className="sub line">None out now</li>}
          </ul>
          {all.length > listed.length && (
            <button className="btn btn-small more" onClick={() => change({ shown: m.shown + PAGE })}>
              Show {Math.min(PAGE, all.length - listed.length)} more of {(all.length - listed.length).toLocaleString()}
            </button>
          )}
        </Section>
      ))}
    </div>
  );
}

/** One collapsible section: its heading (a count on it, if any) and, open, what's in it. */
/** Today and the week ahead: what's on each day, and the market. */
function CalendarView({ life }: { life: TownLife }) {
  const days = life.calendar ?? [];
  const market = marketText(life);
  return (
    <div className="calendar">
      {market && (
        <p className="sub line">
          <strong>Market</strong> {market}
        </p>
      )}
      <ol className="list calendar-days">
        {days.map((d, i) => (
          <li key={d.day} className={`${d.weekend ? 'weekend' : ''} ${d.holiday ? 'holiday' : ''}`}>
            <span className="calendar-name">{i === 0 ? `Today, ${d.name}` : d.name}</span>
            <span className="sub">{d.events.length > 0 ? d.events.join(' · ') : d.weekend ? 'The weekend' : 'An ordinary day'}</span>
          </li>
        ))}
      </ol>
    </div>
  );
}

function Section({ title, figs, open, onToggle, children }: { title: string; figs?: number; open: boolean; onToggle: () => void; children: React.ReactNode }) {
  return (
    <section className={`sb-section ${open ? 'open' : ''}`}>
      <button className="sb-section-head" aria-expanded={open} onClick={onToggle}>
        <span className="sb-head-label">{title}</span>
        {figs !== undefined && <span className="sb-head-figs figs">{figs.toLocaleString()}</span>}
        <span className="sb-section-arrow" aria-hidden="true">
          ›
        </span>
      </button>
      {open && (
        <div className="sb-section-body" role="region" aria-label={title}>
          {children}
        </div>
      )}
    </section>
  );
}

/** The last line heard on each record ("town|car|12"), shown at once on coming back while it's asked for again. */
const heard = new Map<string, string>();

/** The simulation's line on each listed record ("Driving to work"), asked for once each time the list's shown. */
function useBriefs(town: string, listed: { of: 'car' | 'person'; slot: number }[]): Map<string, string> {
  const [lines, setLines] = useState<Map<string, string>>(() => {
    const m = new Map<string, string>();
    for (const [k, line] of heard) if (k.startsWith(`${town}|`)) m.set(k.slice(town.length + 1), line);
    return m;
  });
  const asked = useRef(new Set<string>());
  const ids = listed.map((e) => `${e.of}|${e.slot}`).join(',');
  useEffect(() => {
    for (const e of listed) {
      const id = `${e.of}|${e.slot}`;
      if (asked.current.has(id)) continue;
      asked.current.add(id);
      void briefOn(town, e.of, e.slot).then((line) => {
        if (!line) return;
        heard.set(`${town}|${id}`, line);
        setLines((m) => new Map(m).set(id, line));
      });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [town, ids]);
  return lines;
}

/**
 * A town's congestion over the last day: a bar an hour (how long cars were held up in all), a slider to pick one,
 * and that hour's worst streets by name, drawn red on the map and numbered; click one to go to it.
 */
function CongestionView({
  townKey,
  ago,
  onAgo,
  onCongestion,
  onStreet,
}: {
  townKey: string;
  ago: number;
  onAgo: (ago: number) => void;
  onCongestion: Props['onCongestion'];
  onStreet: Props['onStreet'];
}) {
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
  if (!c) return <p className="sub line">Looking…</p>;
  const bars = holdBars(c.hours);
  const at = c.hours.length - 1 - ago;
  const hour = c.hours[at];
  const until = ago === 0 ? 'now' : c.hours[at + 1]?.time ?? '';
  return (
    <div className="congestion">
      <div className="bars" role="group" aria-label="Hold-ups each hour">
        {bars.map((b, i) => (
          <button
            key={i}
            className={`bar ${i === at ? 'on' : ''}`}
            style={{ height: `${Math.max(2, Math.round(b * 28))}px` }}
            onClick={() => onAgo(c.hours.length - 1 - i)}
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
        onChange={(e) => onAgo(c.hours.length - 1 - Number(e.target.value))}
        aria-label="Hour"
        aria-valuetext={hour ? `${hour.time} to ${until}` : ''}
      />
      {hour && (
        <p className="sub line">
          {hour.time}–{until} · {heldText(hour.hold)} held up · {hour.cars.toLocaleString()} cars
        </p>
      )}
      <h2 className="line">Most congested</h2>
      <ol className="list" aria-label="Most congested streets">
        {c.streets.map((s, n) => (
          <li key={s.road}>
            <button className="row" onClick={() => onStreet(townKey, s.points)} title={`${streetText(s)}: ${heldText(s.hold)} held up in all, ${heldText(s.mean)} a car`}>
              <span className="name wrap">
                {n + 1}. {streetName(s)}
                {streetBetween(s) && <span className="between">{streetBetween(s)}</span>}
              </span>
              <span className="figs">{heldText(s.hold)}</span>
            </button>
          </li>
        ))}
        {c.streets.length === 0 && <li className="sub line">Nobody held up</li>}
      </ol>
    </div>
  );
}

/** The key, and what the panel leaves out to keep to a line: what each colour and shape on the map means, how to look closer, and (if so) why there's no traffic. */
function KeyModal({
  sim,
  layers,
  onLayers,
  hidden,
  onHidden,
  onClose,
}: {
  sim: SimNote | null;
  layers: Layers;
  onLayers: (l: Layers) => void;
  hidden: Set<string>;
  onHidden: (h: Set<string>) => void;
  onClose: () => void;
}) {
  const toggle = (id: string) => {
    const h = new Set(hidden);
    if (h.has(id)) h.delete(id);
    else h.add(id);
    onHidden(h);
  };
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
        <h3>Show on the map</h3>
        <ul className="layer-grid">
          {LAYERS.map((l) => (
            <li key={l.key}>
              <label className="check line" title={l.label}>
                <input type="checkbox" checked={layers[l.key]} onChange={(e) => onLayers({ ...layers, [l.key]: e.target.checked })} />
                {l.short}
              </label>
            </li>
          ))}
        </ul>
        <ul className="layer-grid">
          {ENTITY_KINDS.map((k) => (
            <li key={k.id}>
              <label className="check line" title={k.label}>
                <input type="checkbox" checked={!hidden.has(k.id)} onChange={() => toggle(k.id)} />
                {k.label}
              </label>
            </li>
          ))}
        </ul>
        <h3>Looking closer</h3>
        <p className="sub">
          Click a <b>town</b> for its congestion and everything out in it, a tab a sort. Click a <b>car</b> or <b>townsperson</b>, there or on the map,
          to follow it and see what it's doing and what it'll do next; a <b>building</b> for who's inside. The breadcrumbs at the top go back, as you left
          it. Right-click a spot to send one of your characters there. Zoom in to see street names, traffic lights and stop signs.
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
            <b>Towns</b>: the people who live in each
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

/** The building clicked: who's inside, a line each, and when each goes out next; click someone to follow them. */
function Building({ building, onFocus }: { building: BuildingLife; onFocus: (t: FocusTarget) => void }) {
  return (
    <section aria-label="Who's in the building">
      <h2 className="line">{building.inside.length === 0 ? 'Nobody in' : `${building.inside.length}${building.inside.length >= 50 ? '+' : ''} inside`}</h2>
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
  onFocus,
}: {
  f: EntityFocus | null;
  note: string | null;
  town: string;
  following: boolean;
  onFollow: (on: boolean) => void;
  onFocus: (t: FocusTarget) => void;
}) {
  if (!f) return <p className="sub line">{note ?? 'Looking…'}</p>;
  const what =
    f.kind === 'person' ? 'Person' : f.bus !== undefined && f.bus >= 0 ? 'Bus' : f.service ? SERVICE_NAMES[f.service] ?? 'Vehicle' : f.van !== undefined && f.van >= 0 ? 'Van' : 'Car';
  const next = f.queue.filter((q) => !q.now).slice(0, NEXT);
  return (
    <section className="entity" aria-label="Following">
      <header className="focus-head line">
        <h2 title={f.who}>
          {f.kind === 'person' && f.name ? f.name : `${what} #${f.kind === 'car' && f.van !== undefined && f.van >= 0 ? f.van : f.id}`} · {town}
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
      </header>
      {f.kind === 'car' && f.ownerName && <p className="sub line">{f.ownerName}'s car</p>}
      {f.kind === 'person' && f.household && <p className="sub line">{f.household}</p>}
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

/** The town's key without its revision: the same town whichever revision the map or the editor list has. */
const townOnly = (key: string) => key.replace(/(^|&)rev=\d+/, '').replace(/^&/, '');

/** "Edit streets", for a town this account may edit (GET /mp/edit/towns says). */
function EditStreets({ townKey }: { townKey: string }) {
  const [key, setKey] = useState<string | null>(null);
  useEffect(() => {
    let live = true;
    void editableTowns().then((ts) => {
      const t = ts.find((x) => x.editable && townOnly(x.key) === townOnly(townKey));
      if (live) setKey(t ? t.key : null);
    });
    return () => {
      live = false;
    };
  }, [townKey]);
  if (!key) return null;
  return (
    <div className="edit-entry">
      <button className="btn btn-small" onClick={() => openEditor(key)} title="Change this town's roads, junctions and centre in a draft, then commit it">
        Edit streets
      </button>
    </div>
  );
}

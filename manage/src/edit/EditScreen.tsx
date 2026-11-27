// The street editor (docs/street-editor.md §6): one town, full screen. The map's on the left (canvas.ts),
// a few tools along its top, and the panel on the right (under the map on a phone): Regenerate and what to
// put in the blocks picked, what the draft changes, the town's figures, a day run on it, and Commit. Click
// blocks on the map to pick them (shift-drag, or Box, for many), tick what's wanted, and Regenerate: that's a
// region added to the skeleton with a fresh salt, and the draft re-planned. The worker sorts out the angles
// and spacing itself, so there's nothing to fix by hand. Its answer carries the skeleton as it planned it,
// which the draft takes, and the new blocks in the same place are picked again, so Regenerate again re-rolls
// them. The bullseye is downtown. The draft saves itself a second after each change, with its undo.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import * as api from './api.ts';
import { DEFAULT_EDIT_LAYERS, EditRenderer, type EditLayers } from './canvas.ts';
import { addBlocks, addRegion, canCommit, changeCount, latestRegionNote, moveDowntown, NO_WANT, normalize, rebase, reselect, sameSkeleton, toggleBlock } from './skeleton.ts';
import type { Changes, CommitAnswer, Commands, Day, Pt, Skeleton, Town, Want } from './types.ts';

const UNDO_CAP = 100;
const SAVE_MS = 1000;
const LAYERS_KEY = 'porchlight.manage.edit.layers';

type Step = { label: string; skeleton: Skeleton };

const WANTS: { id: keyof Want; label: string }[] = [
  { id: 'roundabout', label: 'Roundabout' },
  { id: 'park', label: 'Park' },
  { id: 'school', label: 'School' },
  { id: 'shops', label: 'Shops' },
  { id: 'houses', label: 'Houses' },
];

function savedLayers(): EditLayers {
  try {
    const raw = localStorage.getItem(LAYERS_KEY);
    return raw ? { ...DEFAULT_EDIT_LAYERS, ...(JSON.parse(raw) as Partial<EditLayers>) } : DEFAULT_EDIT_LAYERS;
  } catch {
    return DEFAULT_EDIT_LAYERS;
  }
}

export function EditScreen({ townKey, onBack }: { townKey: string; onBack: () => void }) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const [renderer, setRenderer] = useState<EditRenderer | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [loaded, setLoaded] = useState(false);
  // The draft: its skeleton, the revision it's from (and that revision's skeleton), and undo.
  const [skel, setSkel] = useState<Skeleton>(() => normalize(null));
  const [baseRev, setBaseRev] = useState(0);
  const [base, setBase] = useState<Skeleton>(() => normalize(null));
  const [undo, setUndo] = useState<Step[]>([]);
  const [redo, setRedo] = useState<Step[]>([]);
  const [stale, setStale] = useState<{ rev: number; skeleton: Skeleton } | null>(null);
  const [clashes, setClashes] = useState<string[] | null>(null);
  // The latest answer.
  const [town, setTown] = useState<Town | null>(null);
  const [changes, setChanges] = useState<Changes | null>(null);
  const [note, setNote] = useState<{ ok: boolean; text: string } | null>(null);
  const [planError, setPlanError] = useState<string | null>(null);
  const [planMs, setPlanMs] = useState<number | null>(null);
  const [pending, setPending] = useState(false);
  const [live, setLive] = useState<Town | null>(null);
  // The screen.
  const [selection, setSelection] = useState<Pt[][]>([]);
  const [want, setWant] = useState<Want>(NO_WANT);
  const [boxMode, setBoxMode] = useState(false);
  const [layers, setLayers] = useState<EditLayers>(savedLayers);
  const [layersOpen, setLayersOpen] = useState(false);
  const [saved, setSaved] = useState<'saved' | 'saving' | 'unsaved' | 'failed'>('saved');
  const [panelOpen, setPanelOpen] = useState(true);
  const [day, setDay] = useState<{ draft: Day; live: Day } | null>(null);
  const [dayBusy, setDayBusy] = useState(false);
  const [dayError, setDayError] = useState<string | null>(null);
  const [committing, setCommitting] = useState(false);

  const seq = useRef(0);
  const skelRef = useRef(skel);
  skelRef.current = skel;
  const townRef = useRef(town);
  townRef.current = town;
  const dirty = useRef(false);

  // --- Re-planning -------------------------------------------------------------------------------

  const replan = useCallback(
    (s: Skeleton) => {
      const mine = ++seq.current;
      setPending(true);
      void api
        .plan(townKey, s, mine)
        .then((a) => {
          if (mine !== seq.current) return; // A newer one's on its way.
          setPending(false);
          if (!a.ok || !a.town) {
            setPlanError(a.error ?? "The draft couldn't be planned.");
            return;
          }
          // The skeleton as it was planned (a salt moved on, a region dropped) takes the place of the one
          // asked for, in the same undo step.
          const planned = a.skeleton ? normalize(a.skeleton) : s;
          if (a.skeleton && !sameSkeleton(planned, skelRef.current)) {
            setSkel(planned);
            skelRef.current = planned;
            dirty.current = true;
            setSaved('unsaved');
          }
          const t = a.town;
          setPlanError(null);
          setTown(t);
          // The blocks have been planned again: pick the new ones in the same place.
          setSelection((sel) => reselect(t.blocks, sel));
          setChanges(a.changes ?? null);
          setNote(latestRegionNote(a, planned));
          setPlanMs(a.ms ?? null);
        })
        .catch((e: Error) => {
          if (mine !== seq.current) return;
          setPending(false);
          setPlanError(e.message);
        });
    },
    [townKey],
  );

  // --- Loading -----------------------------------------------------------------------------------

  const load = useCallback(async () => {
    setLoadError(null);
    try {
      const d = await api.loadDraft(townKey);
      const current = d.current.skeleton ?? normalize(null);
      const c = d.commands;
      setSkel(d.skeleton);
      skelRef.current = d.skeleton;
      setBaseRev(d.baseRev);
      setBase(c?.base ? normalize(c.base) : d.baseRev === d.current.rev ? current : d.skeleton);
      setUndo(c?.undo ?? []);
      setRedo(c?.redo ?? []);
      setStale(d.stale ? { rev: d.current.rev, skeleton: current } : null);
      setClashes(null);
      setDay(null);
      setSelection([]);
      setNote(null);
      dirty.current = false;
      setSaved('saved');
      setLoaded(true);
      replan(d.skeleton);
      void api.liveTown(townKey, current).then(setLive);
    } catch (e) {
      setLoadError((e as Error).message);
    }
  }, [townKey, replan]);

  useEffect(() => {
    void load();
  }, [load]);

  // --- The map -----------------------------------------------------------------------------------

  useEffect(() => {
    const r = new EditRenderer(canvas.current!);
    setRenderer(r);
    // The mock's for trying it out and for headless tests, which find things on the map through it.
    if (api.isMock()) (window as unknown as { editRenderer: EditRenderer }).editRenderer = r;
    return () => r.dispose();
  }, []);

  /** A change to the draft: undoable, planned and saved. */
  const change = useCallback(
    (next: Skeleton, label: string) => {
      const was = skelRef.current;
      if (sameSkeleton(was, next)) return;
      setUndo((u) => [...u, { label, skeleton: was }].slice(-UNDO_CAP));
      setRedo([]);
      setSkel(next);
      skelRef.current = next;
      dirty.current = true;
      setSaved('unsaved');
      replan(next);
    },
    [replan],
  );

  useEffect(() => {
    if (!renderer) return;
    renderer.onToggle = (poly) => setSelection((sel) => toggleBlock(sel, poly));
    renderer.onBox = (polys) => setSelection((sel) => addBlocks(sel, polys));
    renderer.onDowntown = (at) => change(moveDowntown(skelRef.current, at, townRef.current), 'Move downtown');
  }, [renderer, change]);

  useEffect(() => {
    renderer?.setTown(town);
  }, [renderer, town]);
  useEffect(() => {
    if (renderer) {
      renderer.live = live;
      renderer.redraw();
    }
  }, [renderer, live]);
  useEffect(() => {
    if (!renderer) return;
    renderer.pending = pending;
    renderer.downtown = skel.center ?? null;
    renderer.lost = changes?.lostHouses ?? [];
    renderer.redraw();
  }, [renderer, pending, skel.center, changes]);
  useEffect(() => {
    renderer?.setSelection(selection);
  }, [renderer, selection]);
  useEffect(() => {
    if (!renderer) return;
    renderer.boxMode = boxMode;
  }, [renderer, boxMode]);
  useEffect(() => {
    if (!renderer) return;
    renderer.layers = layers;
    renderer.redraw();
    try {
      localStorage.setItem(LAYERS_KEY, JSON.stringify(layers));
    } catch {
      // Not remembered.
    }
  }, [renderer, layers]);

  // --- Regenerating ------------------------------------------------------------------------------

  const regenerate = useCallback(() => {
    if (!selection.length) return;
    const n = selection.length;
    change(addRegion(skelRef.current, selection, want), `Regenerate ${n} block${n === 1 ? '' : 's'}`);
  }, [selection, want, change]);

  // --- Saving ------------------------------------------------------------------------------------

  const commands = useMemo<Commands>(() => ({ base, undo, redo }), [base, undo, redo]);
  const save = useCallback(async () => {
    if (!dirty.current) return;
    dirty.current = false;
    setSaved('saving');
    try {
      await api.saveDraft(townKey, baseRev, skelRef.current, commands);
      setSaved((s) => (s === 'saving' ? 'saved' : s));
    } catch {
      dirty.current = true;
      setSaved('failed');
    }
  }, [townKey, baseRev, commands]);
  useEffect(() => {
    if (saved !== 'unsaved') return;
    const t = setTimeout(() => void save(), SAVE_MS);
    return () => clearTimeout(t);
  }, [skel, saved, save]);

  const back = useCallback(() => {
    void save().finally(onBack);
  }, [save, onBack]);

  // --- Undo --------------------------------------------------------------------------------------

  const doUndo = useCallback(() => {
    if (!undo.length) return;
    const step = undo[undo.length - 1];
    setUndo(undo.slice(0, -1));
    setRedo([...redo, { label: step.label, skeleton: skelRef.current }]);
    setSkel(step.skeleton);
    skelRef.current = step.skeleton;
    dirty.current = true;
    setSaved('unsaved');
    replan(step.skeleton);
  }, [undo, redo, replan]);
  const doRedo = useCallback(() => {
    if (!redo.length) return;
    const step = redo[redo.length - 1];
    setRedo(redo.slice(0, -1));
    setUndo([...undo, { label: step.label, skeleton: skelRef.current }]);
    setSkel(step.skeleton);
    skelRef.current = step.skeleton;
    dirty.current = true;
    setSaved('unsaved');
    replan(step.skeleton);
  }, [undo, redo, replan]);

  // --- Keys --------------------------------------------------------------------------------------

  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      const el = e.target as HTMLElement | null;
      if (el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.tagName === 'SELECT')) return;
      const mod = e.ctrlKey || e.metaKey;
      if (mod && e.key.toLowerCase() === 'z') {
        e.preventDefault();
        if (e.shiftKey) doRedo();
        else doUndo();
      } else if (mod && e.key.toLowerCase() === 'y') {
        e.preventDefault();
        doRedo();
      } else if (mod) return;
      else if (e.key === 'Escape') {
        renderer?.cancel();
        setSelection([]);
      } else if (e.key === 'b' || e.key === 'B') setBoxMode((b) => !b);
    };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [doUndo, doRedo, renderer]);

  // --- Stale drafts ------------------------------------------------------------------------------

  const doRebase = () => {
    if (!stale) return;
    const r = rebase(base, skelRef.current, stale.skeleton);
    setBase(stale.skeleton);
    setBaseRev(stale.rev);
    setUndo([]);
    setRedo([]);
    setSkel(r.skeleton);
    skelRef.current = r.skeleton;
    setStale(null);
    setClashes(r.clashes);
    dirty.current = true;
    setSaved('unsaved');
    replan(r.skeleton);
    void api.liveTown(townKey, stale.skeleton).then(setLive);
  };
  const doDiscard = async () => {
    if (!confirm('Throw this draft away and start again from the town as it is now?')) return;
    await api.discardDraft(townKey).catch(() => {});
    await load();
  };

  // --- A day -------------------------------------------------------------------------------------

  const runDay = async () => {
    setDayBusy(true);
    setDayError(null);
    try {
      setDay(await api.runDay(townKey, skelRef.current));
    } catch (e) {
      setDayError((e as Error).message);
    } finally {
      setDayBusy(false);
    }
  };

  const goTo = (p: Pt | null) => {
    if (p) renderer?.goTo(p, 10);
    if (window.matchMedia?.('(max-width: 720px)').matches) setPanelOpen(false);
  };

  if (loadError)
    return (
      <main className="card-page">
        <section className="card">
          <h1>Edit streets</h1>
          <p>{loadError}</p>
          <div className="edit-row">
            <button className="btn" onClick={() => void load()}>
              Try again
            </button>
            <button className="btn" onClick={onBack}>
              Back
            </button>
          </div>
        </section>
      </main>
    );

  const townName = new URLSearchParams(townKey).get('seed') ?? 'Town';
  const count = changeCount(skel, base);
  const picked = selection.length;
  return (
    <div className={`edit ${panelOpen ? '' : 'panel-closed'}`}>
      <div className="edit-map">
        <canvas ref={canvas} className={`map ${boxMode ? 'boxing' : ''}`} aria-label="The town's blocks, to pick" />
        <div className="edit-tools" role="toolbar" aria-label="Tools">
          <button className="btn btn-small" onClick={back} title="Back to the county map">
            ← Back
          </button>
          <button className={`btn btn-small ${boxMode ? 'on' : ''}`} onClick={() => setBoxMode(!boxMode)} aria-pressed={boxMode} title="Drag a box to pick every block in it (B, or shift-drag)">
            Box <kbd>B</kbd>
          </button>
          <button className="btn btn-small" onClick={() => setSelection([])} disabled={!picked} title="Pick nothing (Esc)">
            Clear
          </button>
          <button className="btn btn-small" onClick={doUndo} disabled={!undo.length} title={undo.length ? `Undo: ${undo[undo.length - 1].label} (Ctrl Z)` : 'Nothing to undo'}>
            Undo
          </button>
          <button className="btn btn-small" onClick={doRedo} disabled={!redo.length} title={redo.length ? `Redo: ${redo[redo.length - 1].label} (Ctrl Shift Z)` : 'Nothing to redo'}>
            Redo
          </button>
          <button className="btn btn-small" onClick={() => setLayersOpen(!layersOpen)} aria-expanded={layersOpen}>
            Layers
          </button>
          <button className="btn btn-small" onClick={() => renderer?.fit()} title="The whole town">
            Fit
          </button>
        </div>
        {layersOpen && (
          <div className="edit-layers">
            {(
              [
                ['blocks', 'Blocks'],
                ['buildings', 'Buildings'],
                ['lanes', 'Lanes and pockets'],
                ['controls', 'Signals and signs'],
                ['heat', 'Traffic (v/c)'],
                ['ghost', 'The town as it is'],
              ] as [keyof EditLayers, string][]
            ).map(([k, label]) => (
              <label key={k}>
                <input type="checkbox" checked={layers[k]} onChange={(e) => setLayers({ ...layers, [k]: e.target.checked })} /> {label}
              </label>
            ))}
          </div>
        )}
        {stale && (
          <div className="edit-banner warn">
            Someone committed revision {stale.rev} since this draft began.
            <button className="btn btn-small btn-primary" onClick={doRebase}>
              Rebase
            </button>
            <button className="btn btn-small" onClick={() => void doDiscard()}>
              Discard
            </button>
          </div>
        )}
        {clashes && (
          <div className="edit-banner">
            {clashes.length ? (
              <>
                Rebased onto revision {baseRev}. Both changed: {clashes.join('; ')}.
              </>
            ) : (
              <>Rebased onto revision {baseRev}, nothing clashed.</>
            )}
            <button className="btn btn-small" onClick={() => setClashes(null)}>
              OK
            </button>
          </div>
        )}
        <div className="edit-status">
          {!loaded ? 'Loading the draft…' : pending ? 'Planning…' : planError ? <span className="bad">{planError}</span> : planMs !== null ? `Planned in ${planMs} ms` : ''}
          {' · '}
          {saved === 'saved' ? 'Saved' : saved === 'saving' ? 'Saving…' : saved === 'failed' ? <span className="bad">Not saved</span> : 'Unsaved'}
          {' · '}Drag the bullseye to move downtown
        </div>
        <button className="btn btn-small edit-panel-toggle" onClick={() => setPanelOpen(!panelOpen)} aria-expanded={panelOpen}>
          {panelOpen ? 'Hide panel' : `Panel${picked ? ` (${picked} picked)` : ''}`}
        </button>
      </div>
      <aside className="panel edit-panel" aria-label="The draft">
        <div className="panel-head">
          <h1>Edit {townName}</h1>
          <span className="sub">
            {count} change{count === 1 ? '' : 's'} · rev {baseRev}
          </span>
        </div>
        <div className="panel-body">
          <div className="edit-form edit-regen">
            <p className="sub">
              {picked ? `${picked} block${picked === 1 ? '' : 's'} picked.` : 'Click blocks on the map to pick them; shift-drag (or Box) to pick many.'}
            </p>
            <div className="edit-row">
              <button className="btn btn-primary" onClick={regenerate} disabled={!picked} title={picked ? 'Make these blocks again, a new way each time' : 'Pick some blocks first'}>
                Regenerate
              </button>
              <button className="btn" onClick={doUndo} disabled={!undo.length} title={undo.length ? `Undo: ${undo[undo.length - 1].label}` : 'Nothing to undo'}>
                Undo
              </button>
              {picked > 0 && (
                <button className="btn btn-small" onClick={() => setSelection([])}>
                  Clear
                </button>
              )}
            </div>
            <div className="edit-wants" role="group" aria-label="What to put there">
              {WANTS.map((w) => (
                <label key={w.id} className="check">
                  <input type="checkbox" checked={want[w.id]} onChange={(e) => setWant({ ...want, [w.id]: e.target.checked })} /> {w.label}
                </label>
              ))}
            </div>
            {note && <p className={`sub edit-note ${note.ok ? '' : 'warn'}`}>{note.text}</p>}
          </div>

          <h2>Changes</h2>
          {changes ? <ChangesView c={changes} /> : <p className="sub line">—</p>}

          <h2>The town</h2>
          {town ? <Stats town={town} /> : <p className="sub line">—</p>}

          <h2>A day</h2>
          <div className="edit-row">
            <button className="btn btn-small" onClick={() => void runDay()} disabled={dayBusy || !town}>
              {dayBusy ? 'Running a day…' : 'Run a day'}
            </button>
          </div>
          {dayError && <p className="sub bad">{dayError}</p>}
          {day && <DayView day={day} onGo={goTo} />}

          <div className="edit-commit">
            <button className="btn btn-primary" disabled={!canCommit(skel, base, pending)} onClick={() => setCommitting(true)} title={pending ? 'Wait for the plan' : count === 0 ? 'Nothing to commit yet' : undefined}>
              Commit…
            </button>
            <button className="btn btn-small" onClick={() => void doDiscard()}>
              Discard draft
            </button>
          </div>
        </div>
      </aside>
      {committing && (
        <CommitModal
          townKey={townKey}
          skel={skel}
          changes={changes}
          onClose={() => setCommitting(false)}
          onDone={() => {
            setCommitting(false);
            void load();
          }}
        />
      )}
    </div>
  );
}

// --- The side panel's parts --------------------------------------------------------------------------

function ChangesView({ c }: { c: Changes }) {
  const pair = (label: string, [a, b]: [number, number]) => (a !== b ? <li key={label}>{`${label}: ${a} → ${b}`}</li> : null);
  const items = [
    c.housesLost ? <li key="lost" className="bad">{c.housesLost} houses lost (marked ✕)</li> : null,
    c.housesNew ? <li key="new">{c.housesNew} new houses</li> : null,
    c.roadsAdded ? <li key="ra">{c.roadsAdded} roads added</li> : null,
    c.roadsRemoved ? <li key="rr">{c.roadsRemoved} roads taken out</li> : null,
    pair('Shops', c.shops),
    pair('Roundabouts', c.roundabouts),
    pair('Signals', c.signals),
  ].filter(Boolean);
  return items.length ? <ul className="edit-changes">{items}</ul> : <p className="sub line">Nothing yet.</p>;
}

function Stats({ town }: { town: Town }) {
  const rows = Object.entries(town.stats).filter(([, v]) => typeof v === 'number') as [string, number][];
  return (
    <dl className="edit-stats">
      {rows.map(([k, v]) => (
        <div key={k}>
          <dt>{k}</dt>
          <dd>{v}</dd>
        </div>
      ))}
    </dl>
  );
}

function DayView({ day, onGo }: { day: { draft: Day; live: Day }; onGo: (p: Pt) => void }) {
  const row = (label: string, f: (d: Day) => number, unit = '', lowerBetter = true) => {
    const [a, b] = [f(day.live), f(day.draft)];
    const better = lowerBetter ? b < a : b > a;
    return (
      <tr key={label}>
        <th>{label}</th>
        <td>
          {a}
          {unit}
        </td>
        <td className={a === b ? '' : better ? 'good' : 'bad'}>
          {b}
          {unit}
        </td>
      </tr>
    );
  };
  return (
    <>
      <table className="edit-day">
        <thead>
          <tr>
            <th />
            <th>Now</th>
            <th>Draft</th>
          </tr>
        </thead>
        <tbody>
          {row('Trips', (d) => d.trips, '', false)}
          {row('Mean drive', (d) => d.meanCarMin, ' min')}
          {row('90% drive', (d) => d.p90CarMin, ' min')}
          {row('Mean walk', (d) => d.meanWalkMin, ' min')}
          {row('Failed', (d) => d.failed)}
          {row('Gave up', (d) => d.gaveUp)}
        </tbody>
      </table>
      {day.draft.worst.length > 0 && (
        <>
          <p className="sub">Worst junctions in the draft</p>
          <ul className="list">
            {day.draft.worst.map((w, i) => (
              <li key={i}>
                <button className="row line" onClick={() => onGo(w.at)}>
                  <span className="name">{w.control}</span>
                  <span className="sub">{w.delayS} s a car</span>
                </button>
              </li>
            ))}
          </ul>
        </>
      )}
    </>
  );
}

function CommitModal({
  townKey,
  skel,
  changes,
  onClose,
  onDone,
}: {
  townKey: string;
  skel: Skeleton;
  changes: Changes | null;
  onClose: () => void;
  onDone: () => void;
}) {
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<CommitAnswer | null>(null);
  const go = async () => {
    setBusy(true);
    setError(null);
    try {
      const a = await api.commit(townKey, skel, message.trim());
      // An older server can still refuse a draft for its problems: said in a line, not a list.
      if ('problems' in a) setError(a.problems[0]?.text ?? "The server couldn't commit this draft.");
      else setDone(a);
    } catch (e) {
      setError((e as Error).message);
    } finally {
      setBusy(false);
    }
  };
  return (
    <div className="edit-modal-back" role="dialog" aria-modal="true" aria-label="Commit the draft">
      <section className="card edit-modal">
        {done ? (
          <>
            <h1>Committed: revision {done.rev}</h1>
            <p>
              The town is rebuilt from it. {done.rehoused ? `${done.rehoused} household${done.rehoused === 1 ? ' was' : 's were'} rehoused. ` : ''}
              {done.players ? `${done.players} player${done.players === 1 ? '' : 's'} in the town will see it change.` : ''}
            </p>
            <button className="btn btn-primary" onClick={onDone}>
              OK
            </button>
          </>
        ) : (
          <>
            <h1>Commit the draft</h1>
            {changes && <ChangesView c={changes} />}
            <label className="edit-message">
              What changed, and why
              <textarea value={message} onChange={(e) => setMessage(e.target.value)} rows={3} placeholder="A park and a school in the north end" autoFocus />
            </label>
            {error && <p className="sub bad">{error}</p>}
            <div className="edit-row">
              <button className="btn btn-primary" onClick={() => void go()} disabled={busy || !message.trim()}>
                {busy ? 'Committing…' : 'Commit'}
              </button>
              <button className="btn" onClick={onClose} disabled={busy}>
                Cancel
              </button>
            </div>
          </>
        )}
      </section>
    </div>
  );
}

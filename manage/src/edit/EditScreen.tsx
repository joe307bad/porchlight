// The street editor (docs/street-editor.md §6): one town, full screen. The map's on the left (canvas.ts),
// the tools along its top, and the panel on the right: what's selected, the problems, what the draft changes,
// the town's figures, a day run on it, and Commit. Every edit is a skeleton (skeleton.ts); a new one is sent
// to be planned (a pause in a drag, or letting go) and the answer drawn when it comes, older answers dropped.
// The draft saves itself a second after each change, with its undo, so a reload picks up where it was left.

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import * as api from './api.ts';
import { DEFAULT_EDIT_LAYERS, EditRenderer, type Edit, type EditLayers, type Tool } from './canvas.ts';
import {
  changeCount,
  deleteRoad,
  dist,
  drawRoad,
  junctionOverride,
  moveBend,
  moveCenter,
  moveNode,
  normalize,
  pullBend,
  rebase,
  roadOverride,
  sameEnds,
  setJunction,
  setRoad,
  splitRoad,
  withBend,
  withRoad,
} from './skeleton.ts';
import type { Changes, CommitAnswer, Commands, Control, Day, Problem, Pt, RoadClass, Skeleton, Town } from './types.ts';

const UNDO_CAP = 100;
const SAVE_MS = 1000;
const LAYERS_KEY = 'porchlight.manage.edit.layers';

type Step = { label: string; skeleton: Skeleton };
/** What's selected, by where it is (ids change with every answer). */
type Picked = { kind: 'road'; a: Pt; b: Pt } | { kind: 'junction'; at: Pt } | null;

const TOOLS: { id: Tool; key: string; label: string; title: string }[] = [
  { id: 'select', key: 'V', label: 'Select', title: 'Select and drag (V)' },
  { id: 'draw', key: 'R', label: 'Road', title: 'Draw a road: click points, double-click or Enter to finish (R)' },
  { id: 'delete', key: 'Del', label: 'Delete', title: 'Click a road to take it out (Del)' },
  { id: 'split', key: 'S', label: 'Split', title: 'Click a road to split it there (S)' },
];

const CONTROLS: { id: Control; label: string }[] = [
  { id: 'auto', label: 'Auto' },
  { id: 'free', label: 'Free' },
  { id: 'stop', label: 'Stop' },
  { id: 'allway', label: 'All-way' },
  { id: 'signal', label: 'Signal' },
  { id: 'roundabout', label: 'Roundabout' },
];

function savedLayers(): EditLayers {
  try {
    const raw = localStorage.getItem(LAYERS_KEY);
    return raw ? { ...DEFAULT_EDIT_LAYERS, ...(JSON.parse(raw) as Partial<EditLayers>) } : DEFAULT_EDIT_LAYERS;
  } catch {
    return DEFAULT_EDIT_LAYERS;
  }
}

/** An edit made on the map, as a skeleton: against the town the map had when the drag began. */
export function applyEdit(skel: Skeleton, town: Town, e: Edit, cls: RoadClass): { skeleton: Skeleton; label: string } {
  switch (e.type) {
    case 'node': {
      const to = e.merge !== undefined ? (town.nodes.find((n) => n.id === e.merge)?.at ?? e.to) : e.to;
      return { skeleton: moveNode(skel, town, e.node, to), label: e.merge !== undefined ? 'Merge junctions' : e.join !== undefined ? 'Join a road' : 'Move a junction' };
    }
    case 'bend':
      return { skeleton: moveBend(skel, town, e.road, e.index, e.to), label: 'Move a bend' };
    case 'newBend': {
      const r = town.roads.find((x) => x.id === e.road);
      if (!r) return { skeleton: skel, label: 'Bend a road' };
      return { skeleton: withRoad(skel, town, r, pullBend(withBend(r.pts, e.seg, e.to), e.seg + 1, e.to)), label: 'Bend a road' };
    }
    case 'center':
      return { skeleton: moveCenter(skel, e.to), label: 'Move the town centre' };
    case 'draw':
      return { skeleton: drawRoad(skel, town, e.pts, cls), label: cls === 'arterial' ? 'Draw a main road' : 'Draw a street' };
    case 'delete':
      return { skeleton: deleteRoad(skel, town, e.road), label: 'Delete a road' };
    case 'split':
      return { skeleton: splitRoad(skel, town, e.road, e.at), label: 'Split a road' };
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
  const [problems, setProblems] = useState<Problem[]>([]);
  const [changes, setChanges] = useState<Changes | null>(null);
  const [planError, setPlanError] = useState<string | null>(null);
  const [planMs, setPlanMs] = useState<number | null>(null);
  const [pending, setPending] = useState(false);
  const [live, setLive] = useState<Town | null>(null);
  // The screen.
  const [tool, setTool] = useState<Tool>('select');
  const [drawClass, setDrawClass] = useState<RoadClass>('local');
  const [layers, setLayers] = useState<EditLayers>(savedLayers);
  const [layersOpen, setLayersOpen] = useState(false);
  const [picked, setPicked] = useState<Picked>(null);
  const [warnings, setWarnings] = useState<string[]>([]);
  const [saved, setSaved] = useState<'saved' | 'saving' | 'unsaved' | 'failed'>('saved');
  const [panelOpen, setPanelOpen] = useState(true);
  const [day, setDay] = useState<{ draft: Day; live: Day } | null>(null);
  const [dayBusy, setDayBusy] = useState(false);
  const [dayError, setDayError] = useState<string | null>(null);
  const [committing, setCommitting] = useState(false);

  const seq = useRef(0);
  const skelRef = useRef(skel);
  skelRef.current = skel;
  const drawClassRef = useRef(drawClass);
  drawClassRef.current = drawClass;
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
          setPlanError(null);
          setTown(a.town);
          setProblems(a.problems ?? []);
          setChanges(a.changes ?? null);
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
      setBaseRev(d.baseRev);
      setBase(c?.base ? normalize(c.base) : d.baseRev === d.current.rev ? current : d.skeleton);
      setUndo(c?.undo ?? []);
      setRedo(c?.redo ?? []);
      setStale(d.stale ? { rev: d.current.rev, skeleton: current } : null);
      setClashes(null);
      setDay(null);
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
      if (JSON.stringify(was) === JSON.stringify(next)) return;
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
    renderer.onEdit = (e, t) => {
      const { skeleton, label } = applyEdit(skelRef.current, t, e, drawClassRef.current);
      change(skeleton, label);
      if (e.type === 'split') {
        setTool('select');
        setPicked({ kind: 'junction', at: e.at });
      }
    };
    renderer.onPause = (e, t) => replan(applyEdit(skelRef.current, t, e, drawClassRef.current).skeleton);
    renderer.onWarnings = setWarnings;
    renderer.onSelect = (s) => {
      const t = renderer.town;
      if (!s || !t) return setPicked(null);
      if (s.kind === 'road') {
        const r = t.roads.find((x) => x.id === s.id);
        setPicked(r ? { kind: 'road', a: r.pts[0], b: r.pts[r.pts.length - 1] } : null);
      } else {
        const n = t.nodes.find((x) => x.id === s.node);
        setPicked(n ? { kind: 'junction', at: n.at } : null);
      }
    };
  }, [renderer, change, replan]);

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
    renderer.center = skel.center ?? null;
    renderer.marks = problems.filter((p) => p.at).map((p) => ({ at: p.at!, level: p.level }));
    renderer.lost = changes?.lostHouses ?? [];
    renderer.redraw();
  }, [renderer, pending, skel.center, problems, changes]);
  useEffect(() => {
    if (!renderer) return;
    renderer.tool = tool;
    renderer.drawClass = drawClass;
    if (tool !== 'draw') renderer.cancel();
    renderer.redraw();
  }, [renderer, tool, drawClass]);
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

  // What's picked, in the latest answer.
  const road = useMemo(() => (picked?.kind === 'road' && town ? (town.roads.find((r) => sameEnds(r.pts[0], r.pts[r.pts.length - 1], picked.a, picked.b)) ?? null) : null), [picked, town]);
  const junction = useMemo(() => {
    if (picked?.kind !== 'junction' || !town) return null;
    let best: Town['nodes'][number] | null = null;
    for (const n of town.nodes) if (n.legs >= 3 && dist(n.at, picked.at) <= 1.5 && (!best || dist(n.at, picked.at) < dist(best.at, picked.at))) best = n;
    return best;
  }, [picked, town]);
  useEffect(() => {
    if (!renderer) return;
    renderer.selection = road ? { kind: 'road', id: road.id } : junction ? { kind: 'junction', node: junction.id } : null;
    renderer.redraw();
  }, [renderer, road, junction]);

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
      else if (e.key === 'v' || e.key === 'V') setTool('select');
      else if (e.key === 'r' || e.key === 'R') setTool('draw');
      else if (e.key === 's' || e.key === 'S') setTool('split');
      else if (e.key === 'Delete' || e.key === 'Backspace') {
        if (road && town) change(deleteRoad(skelRef.current, town, road.id), 'Delete a road');
        else setTool('delete');
      } else if (e.key === 'Escape') {
        renderer?.cancel();
        setTool('select');
        setPicked(null);
      } else if (e.key === 'Enter') renderer?.finishDraw();
    };
    window.addEventListener('keydown', key);
    return () => window.removeEventListener('keydown', key);
  }, [doUndo, doRedo, renderer, road, town, change]);

  // --- Stale drafts ------------------------------------------------------------------------------

  const doRebase = () => {
    if (!stale) return;
    const r = rebase(base, skelRef.current, stale.skeleton);
    setBase(stale.skeleton);
    setBaseRev(stale.rev);
    setUndo([]);
    setRedo([]);
    setSkel(r.skeleton);
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

  const errors = problems.filter((p) => p.level === 'error');
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
  return (
    <div className={`edit ${panelOpen ? '' : 'panel-closed'}`}>
      <div className="edit-map">
        <canvas ref={canvas} className="map" aria-label="The town's streets, to edit" />
        <div className="edit-tools" role="toolbar" aria-label="Tools">
          <button className="btn btn-small" onClick={back} title="Back to the county map">
            ← Back
          </button>
          {TOOLS.map((t) => (
            <button key={t.id} className={`btn btn-small ${tool === t.id ? 'on' : ''}`} onClick={() => setTool(t.id)} title={t.title} aria-pressed={tool === t.id}>
              {t.label} <kbd>{t.key}</kbd>
            </button>
          ))}
          {tool === 'draw' && (
            <select className="edit-select" value={drawClass} onChange={(e) => setDrawClass(e.target.value as RoadClass)} aria-label="What to draw">
              <option value="local">Street</option>
              <option value="collector">Collector</option>
              <option value="arterial">Main road</option>
            </select>
          )}
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
        {warnings.length > 0 && <div className="edit-warnings">{warnings.join(' · ')}</div>}
        <div className="edit-status">
          {!loaded ? 'Loading the draft…' : pending ? 'Planning…' : planError ? <span className="bad">{planError}</span> : planMs !== null ? `Planned in ${planMs} ms` : ''}
          {' · '}
          {saved === 'saved' ? 'Saved' : saved === 'saving' ? 'Saving…' : saved === 'failed' ? <span className="bad">Not saved</span> : 'Unsaved'}
          {' · '}Drag the bullseye to move the centre
        </div>
        <button className="btn btn-small edit-panel-toggle" onClick={() => setPanelOpen(!panelOpen)} aria-expanded={panelOpen}>
          {panelOpen ? 'Hide panel' : `Panel${errors.length ? ` (${errors.length} !)` : ''}`}
        </button>
      </div>
      <aside className="panel edit-panel" aria-label="The draft">
        <div className="panel-head">
          <h1>Edit {townName}</h1>
          <span className="sub">{changeCount(skel, base)} changes · rev {baseRev}</span>
        </div>
        <div className="panel-body">
          {road && town && <RoadPanel key={`r${road.id}`} town={town} road={road} skel={skel} onChange={change} />}
          {junction && town && <JunctionPanel town={town} node={junction} skel={skel} onChange={change} />}
          {!road && !junction && <p className="sub line">Click a road or junction to change it. Drag a junction, a bend or the bullseye to move it.</p>}

          <h2>Problems {problems.length > 0 && <span className="sub">({problems.length})</span>}</h2>
          {problems.length === 0 ? (
            <p className="sub line">None.</p>
          ) : (
            <ul className="list">
              {problems.map((p, i) => (
                <li key={i}>
                  <button className={`row line edit-problem ${p.level}`} onClick={() => goTo(p.at)} disabled={!p.at}>
                    <span className="name">{p.level === 'error' ? '✖' : '!'} {p.text}</span>
                  </button>
                </li>
              ))}
            </ul>
          )}

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
            <button className="btn btn-primary" disabled={errors.length > 0 || pending || changeCount(skel, base) === 0} onClick={() => setCommitting(true)} title={errors.length ? 'Fix the problems marked ✖ first' : undefined}>
              Commit…
            </button>
            {errors.length > 0 && <span className="sub bad">{errors.length} to fix first</span>}
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
          problems={problems}
          onClose={() => setCommitting(false)}
          onDone={() => {
            setCommitting(false);
            void load();
          }}
          onProblems={(p) => setProblems(p)}
        />
      )}
    </div>
  );
}

// --- The side panel's parts --------------------------------------------------------------------------

function RoadPanel({ town, road, skel, onChange }: { town: Town; road: Town['roads'][number]; skel: Skeleton; onChange: (s: Skeleton, label: string) => void }) {
  const a = road.pts[0], b = road.pts[road.pts.length - 1];
  const o = roadOverride(skel, a, b);
  const set = (patch: Parameters<typeof setRoad>[3], label: string) => onChange(setRoad(skel, a, b, patch), label);
  const d = road.design;
  return (
    <div className="edit-form">
      <h2>
        {road.name ?? 'Road'} <span className="sub">{road.kind === 'main' ? 'main road' : road.kind}</span>
      </h2>
      {d && (
        <p className="sub">
          {d.lanes[0]}+{d.lanes[1]} lanes, {d.width} wide{d.divided ? ', divided' : ''}
          {d.pockets.length ? `, ${d.pockets.length} turn pocket${d.pockets.length > 1 ? 's' : ''}` : ''} · {d.flow[0] + d.flow[1]} cars a day · v/c {d.vc.toFixed(2)}
        </p>
      )}
      <label>
        Class
        <select value={o?.cls ?? ''} onChange={(e) => set({ cls: (e.target.value || undefined) as RoadClass | undefined }, 'Set a road’s class')}>
          <option value="">Auto ({road.cls})</option>
          <option value="arterial">Arterial</option>
          <option value="collector">Collector</option>
          <option value="local">Local</option>
        </select>
      </label>
      <div className="edit-seg" role="group" aria-label="Lanes each way">
        <span>Lanes</span>
        {[0, 1, 2, 3].map((n) => (
          <button key={n} className={`btn btn-small ${(o?.lanes ?? 0) === n ? 'on' : ''}`} onClick={() => set({ lanes: n || undefined }, 'Set a road’s lanes')}>
            {n === 0 ? 'Auto' : n}
          </button>
        ))}
      </div>
      <label className="check">
        <input type="checkbox" checked={!!o?.divided} onChange={(e) => set({ divided: e.target.checked }, 'Divide a road')} /> Divided (a median)
      </label>
      <label className="check">
        <input type="checkbox" checked={!!o?.oneWay} onChange={(e) => set({ oneWay: e.target.checked }, 'Make a road one way')} /> One way ({`from its first end`})
      </label>
      <label className="check">
        <input type="checkbox" checked={!!o?.locked} onChange={(e) => set({ locked: e.target.checked }, 'Lock a road')} /> Lock (the planner leaves it as set)
      </label>
      <div className="edit-row">
        <button className="btn btn-small" onClick={() => onChange(deleteRoad(skel, town, road.id), 'Delete a road')}>
          Delete road
        </button>
      </div>
    </div>
  );
}

function JunctionPanel({ town, node, skel, onChange }: { town: Town; node: Town['nodes'][number]; skel: Skeleton; onChange: (s: Skeleton, label: string) => void }) {
  const o = junctionOverride(skel, node.at);
  const j = town.junctions.find((x) => x.node === node.id);
  const now = o?.control ?? 'auto';
  return (
    <div className="edit-form">
      <h2>
        Junction <span className="sub">{node.legs} ways{node.gate ? ', a gate' : ''}</span>
      </h2>
      {j && (
        <p className="sub">
          {j.form === 'roundabout' ? 'A roundabout' : `Controlled by ${j.control === 'allway' ? 'all-way stops' : j.control}`}
          {j.protectedLeft ? ', protected lefts' : ''} · {j.entering} cars a day in
        </p>
      )}
      <div className="edit-seg wrap" role="group" aria-label="Control">
        {CONTROLS.map((c) => (
          <button key={c.id} className={`btn btn-small ${now === c.id ? 'on' : ''}`} onClick={() => onChange(setJunction(skel, node.at, c.id), `Set a junction to ${c.label.toLowerCase()}`)}>
            {c.label}
          </button>
        ))}
      </div>
    </div>
  );
}

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
  problems,
  onClose,
  onDone,
  onProblems,
}: {
  townKey: string;
  skel: Skeleton;
  changes: Changes | null;
  problems: Problem[];
  onClose: () => void;
  onDone: () => void;
  onProblems: (p: Problem[]) => void;
}) {
  const [message, setMessage] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [done, setDone] = useState<CommitAnswer | null>(null);
  const warnings = problems.filter((p) => p.level === 'warning');
  const go = async () => {
    setBusy(true);
    setError(null);
    try {
      const a = await api.commit(townKey, skel, message.trim());
      if ('problems' in a) {
        onProblems(a.problems);
        setError(`The server found ${a.problems.length} problem${a.problems.length === 1 ? '' : 's'} to fix first.`);
      } else setDone(a);
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
            {warnings.length > 0 && (
              <>
                <p className="sub">Still to know about:</p>
                <ul className="edit-changes">
                  {warnings.map((w, i) => (
                    <li key={i}>{w.text}</li>
                  ))}
                </ul>
              </>
            )}
            <label className="edit-message">
              What changed, and why
              <textarea value={message} onChange={(e) => setMessage(e.target.value)} rows={3} placeholder="A roundabout at the High Street crossroads" autoFocus />
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

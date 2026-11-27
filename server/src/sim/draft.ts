// The street editor's draft worker (`porchlight-sim --draft`, sim/src/Draft.cs; its wire is
// ../../../docs/street-editor-api.md): one process beside the simulation's, asked one thing at a time,
// a JSON line each way, matched by id. A worker that stops is started again (after a pause that grows
// if it keeps stopping); whatever it was asked is answered with an error.
//
// Requests wait their turn (the worker does one at a time). A plan still waiting when another comes for
// the same editor and town is answered `superseded` at once: dragging sends a plan a step, and only the
// latest is wanted.

import { spawn, type ChildProcessWithoutNullStreams } from 'node:child_process';
import type { SimCommand } from './pool.ts';

const RESTART_MS = [1_000, 2_000, 5_000, 10_000, 30_000];
/** How long each op may take once sent (ms): a day is a minute's work in a big town. */
const TIMEOUT_MS: Record<string, number> = { plan: 60_000, houses: 60_000, day: 300_000 };

export type DraftAnswer = { id?: number; ok: boolean; error?: string; [k: string]: unknown };

/** What the edit routes ask (edit.ts): the worker, or a test's stand-in. */
export interface DraftClient {
  /** Ask the worker; `coalesce` names a request a newer one with the same name replaces while it waits. */
  request(req: Record<string, unknown>, coalesce?: string): Promise<DraftAnswer>;
  close(): void;
}

interface Job {
  req: Record<string, unknown>;
  coalesce?: string;
  resolve: (a: DraftAnswer) => void;
}

export class DraftWorker implements DraftClient {
  private proc: ChildProcessWithoutNullStreams | null = null;
  private buf = '';
  private queue: Job[] = [];
  private current: { id: number; job: Job; timer: ReturnType<typeof setTimeout> } | null = null;
  private nextId = 1;
  private stops = 0;
  private startedAt = 0;
  private restart: ReturnType<typeof setTimeout> | null = null;
  private closed = false;
  log: (line: string) => void = (line) => console.log(line);

  private readonly cmd: SimCommand;

  constructor(cmd: SimCommand) {
    this.cmd = { command: cmd.command, args: [...cmd.args, '--draft'] };
  }

  start(): void {
    if (this.closed || this.proc) return;
    this.startedAt = Date.now();
    const proc = spawn(this.cmd.command, this.cmd.args, { stdio: ['pipe', 'pipe', 'pipe'], env: process.env });
    this.proc = proc;
    this.buf = '';
    proc.stdout.setEncoding('utf8');
    proc.stdout.on('data', (chunk: string) => this.read(chunk));
    proc.stderr.on('data', (chunk: Buffer) => {
      for (const line of chunk.toString().split('\n')) if (line.trim()) this.log(`[draft] ${line.trim()}`);
    });
    proc.stdin.on('error', () => {}); // it went: 'exit' says so
    proc.on('error', (err) => this.log(`draft: couldn't start ${this.cmd.command}: ${err.message}`));
    proc.on('exit', (code, signal) => {
      if (this.proc !== proc) return;
      this.proc = null;
      this.fail('the draft worker stopped');
      if (this.closed) return;
      if (Date.now() - this.startedAt > 60_000) this.stops = 0;
      const wait = RESTART_MS[Math.min(this.stops++, RESTART_MS.length - 1)];
      this.log(`draft: stopped (${signal ?? `code ${code}`}); starting again in ${wait / 1000} s`);
      this.restart = setTimeout(() => {
        this.restart = null;
        this.start();
        this.pump();
      }, wait);
      this.restart.unref?.();
    });
  }

  request(req: Record<string, unknown>, coalesce?: string): Promise<DraftAnswer> {
    if (this.closed) return Promise.resolve({ ok: false, error: 'the draft worker is closed' });
    return new Promise((resolve) => {
      if (coalesce) {
        for (const waiting of this.queue.filter((j) => j.coalesce === coalesce)) waiting.resolve({ ok: false, error: 'superseded' });
        this.queue = this.queue.filter((j) => j.coalesce !== coalesce);
      }
      this.queue.push({ req, coalesce, resolve });
      if (!this.proc && !this.restart) this.start();
      this.pump();
    });
  }

  close(): void {
    this.closed = true;
    if (this.restart) clearTimeout(this.restart);
    for (const j of this.queue) j.resolve({ ok: false, error: 'the draft worker is closed' });
    this.queue = [];
    this.fail('the draft worker is closed');
    const proc = this.proc;
    this.proc = null;
    if (proc) {
      proc.stdin.end();
      setTimeout(() => proc.kill('SIGKILL'), 2_000).unref();
    }
  }

  /** Send the next request, if the worker's free. */
  private pump(): void {
    if (this.current || !this.proc?.stdin.writable) return;
    const job = this.queue.shift();
    if (!job) return;
    const id = this.nextId++;
    const ms = TIMEOUT_MS[String(job.req.op)] ?? 60_000;
    const timer = setTimeout(() => {
      // Too long: whatever it's doing is abandoned with it (a stuck worker would hold everyone up).
      this.log(`draft: ${String(job.req.op)} took over ${ms / 1000} s; restarting the worker`);
      this.proc?.kill('SIGKILL');
    }, ms);
    timer.unref?.();
    this.current = { id, job, timer };
    this.proc.stdin.write(`${JSON.stringify({ ...job.req, id })}\n`);
  }

  /** Whatever was asked and not answered, answered with an error. */
  private fail(error: string): void {
    const c = this.current;
    this.current = null;
    if (!c) return;
    clearTimeout(c.timer);
    c.job.resolve({ ok: false, error });
  }

  private read(chunk: string): void {
    this.buf += chunk;
    for (;;) {
      const nl = this.buf.indexOf('\n');
      if (nl < 0) return;
      const line = this.buf.slice(0, nl);
      this.buf = this.buf.slice(nl + 1);
      if (!line.trim()) continue;
      let answer: DraftAnswer;
      try {
        answer = JSON.parse(line) as DraftAnswer;
      } catch {
        this.log(`draft: not JSON: ${line.slice(0, 200)}`);
        continue;
      }
      const c = this.current;
      if (!c || answer.id !== c.id) continue; // an answer to something given up on
      this.current = null;
      clearTimeout(c.timer);
      c.job.resolve(answer);
      this.pump();
    }
  }
}

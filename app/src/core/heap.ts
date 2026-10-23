// Binary min-heap of integer items keyed by a numeric priority.
// Ties pop in insertion order so searches stay deterministic.

export class MinHeap {
  private items: number[] = [];
  private prio: number[] = [];
  private order: number[] = [];
  private counter = 0;

  get size(): number {
    return this.items.length;
  }

  push(item: number, priority: number): void {
    this.items.push(item);
    this.prio.push(priority);
    this.order.push(this.counter++);
    this.up(this.items.length - 1);
  }

  /** Remove and return the item with the lowest priority. */
  pop(): number {
    const top = this.items[0];
    const lastItem = this.items.pop()!;
    const lastPrio = this.prio.pop()!;
    const lastOrder = this.order.pop()!;
    if (this.items.length > 0) {
      this.items[0] = lastItem;
      this.prio[0] = lastPrio;
      this.order[0] = lastOrder;
      this.down(0);
    }
    return top;
  }

  private less(a: number, b: number): boolean {
    return this.prio[a] < this.prio[b] || (this.prio[a] === this.prio[b] && this.order[a] < this.order[b]);
  }

  private swap(a: number, b: number): void {
    [this.items[a], this.items[b]] = [this.items[b], this.items[a]];
    [this.prio[a], this.prio[b]] = [this.prio[b], this.prio[a]];
    [this.order[a], this.order[b]] = [this.order[b], this.order[a]];
  }

  private up(i: number): void {
    while (i > 0) {
      const parent = (i - 1) >> 1;
      if (!this.less(i, parent)) break;
      this.swap(i, parent);
      i = parent;
    }
  }

  private down(i: number): void {
    const n = this.items.length;
    for (;;) {
      const l = i * 2 + 1;
      const r = l + 1;
      let best = i;
      if (l < n && this.less(l, best)) best = l;
      if (r < n && this.less(r, best)) best = r;
      if (best === i) return;
      this.swap(i, best);
      i = best;
    }
  }
}

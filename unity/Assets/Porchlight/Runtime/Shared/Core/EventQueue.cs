// A timetable of things due to happen: each thing (a car, a person, a bus …) has
// a key, and at most one time it's next due. Scheduling a key again moves it;
// cancelling forgets it. Times are whole milliseconds, and things due at the same
// millisecond come out in key order, so a run goes the same way on every machine.
//
// Underneath it's a binary heap of (time, key) entries that are never removed in
// place: a moved or cancelled key leaves its old entry behind, and Pop skips any
// entry that no longer matches the key's current time. Pure.

using System.Collections.Generic;

namespace Porchlight.Core
{
    public sealed class EventQueue
    {
        struct Entry
        {
            public long at;
            public long key;
        }

        readonly List<Entry> heap = new List<Entry>();
        /// <summary>Each key's current time.</summary>
        readonly Dictionary<long, long> due = new Dictionary<long, long>();

        /// <summary>How many keys are due (not counting stale entries).</summary>
        public int Count => due.Count;

        /// <summary>Entries in the heap, stale ones included (for tests and measuring).</summary>
        public int HeapSize => heap.Count;

        /// <summary>Make `key` due at `at`, instead of whenever it was.</summary>
        public void Schedule(long key, long at)
        {
            if (due.TryGetValue(key, out long was) && was == at) return;
            due[key] = at;
            Push(new Entry { at = at, key = key });
            // Many moves leave many stale entries: sweep them now and then.
            if (heap.Count > 64 && heap.Count > due.Count * 4) Compact();
        }

        public void Cancel(long key) => due.Remove(key);

        public bool IsDue(long key) => due.ContainsKey(key);

        /// <summary>When `key` is due, or -1 if it isn't.</summary>
        public long When(long key) => due.TryGetValue(key, out long at) ? at : -1;

        /// <summary>The earliest time anything's due, or long.MaxValue.</summary>
        public long Next
        {
            get
            {
                DropStale();
                return heap.Count == 0 ? long.MaxValue : heap[0].at;
            }
        }

        /// <summary>The first thing due at or before `until`, taken off the timetable.</summary>
        public bool TryPop(long until, out long key, out long at)
        {
            DropStale();
            key = at = 0;
            if (heap.Count == 0 || heap[0].at > until) return false;
            var e = heap[0];
            RemoveTop();
            due.Remove(e.key);
            key = e.key;
            at = e.at;
            return true;
        }

        public void Clear()
        {
            heap.Clear();
            due.Clear();
        }

        void DropStale()
        {
            while (heap.Count > 0 && !(due.TryGetValue(heap[0].key, out long at) && at == heap[0].at)) RemoveTop();
        }

        void Compact()
        {
            var live = new List<Entry>(due.Count);
            foreach (var e in heap) if (due.TryGetValue(e.key, out long at) && at == e.at) live.Add(e);
            heap.Clear();
            // A key can have two live-looking entries only if it was moved away and back; keep one.
            var seen = new HashSet<long>();
            foreach (var e in live) if (seen.Add(e.key)) Push(e);
        }

        static bool Before(in Entry a, in Entry b) => a.at < b.at || (a.at == b.at && a.key < b.key);

        void Push(Entry e)
        {
            heap.Add(e);
            int i = heap.Count - 1;
            while (i > 0)
            {
                int up = (i - 1) / 2;
                if (!Before(heap[i], heap[up])) break;
                (heap[i], heap[up]) = (heap[up], heap[i]);
                i = up;
            }
        }

        void RemoveTop()
        {
            int last = heap.Count - 1;
            heap[0] = heap[last];
            heap.RemoveAt(last);
            int i = 0, n = heap.Count;
            for (;;)
            {
                int l = i * 2 + 1, r = l + 1, best = i;
                if (l < n && Before(heap[l], heap[best])) best = l;
                if (r < n && Before(heap[r], heap[best])) best = r;
                if (best == i) break;
                (heap[i], heap[best]) = (heap[best], heap[i]);
                i = best;
            }
        }
    }
}

// Port of src/objects/js-weak-refs.{h,cc}, js-weak-refs-inl.h and js-weak-refs.tq
// (JSWeakRef, JSFinalizationRegistry, WeakCell), together with the parts of
// the heap that drive them: Heap::KeepDuringJob / ClearKeptObjects, the
// dirty FinalizationRegistry list, the GC-time clearing of WeakCells
// (MarkCompactCollector::ClearJSWeakRefs) and FinalizationRegistryCleanupTask
// (src/heap/finalization-registry-cleanup-task.cc).
//
// Deviation (see deviations.md, "Weak references"): V8's GC clears dead
// targets during its atomic pause. V8Sharp holds targets through CLR weak
// references and notices the CLR GC's clearing afterwards: whenever a
// collection happened since the last check (GC.CollectionCount), the next
// microtask checkpoint or Isolate.CollectGarbage walks the active cells of the
// live registries, moves cells whose targets died to the cleared list, and
// posts the cleanup task. Isolate.CollectGarbage (d8's gc()) forces a full
// blocking collection first, so tests observe clearing deterministically.
namespace V8Sharp.Objects
{
    /// <summary>
    /// V8's JSWeakRef. The target is held by a CLR weak reference; while the
    /// current job runs, the isolate's KeepDuringJob set keeps it alive.
    /// </summary>
    public sealed class JSWeakRef(Map map) : JSObject(map)
    {
        /// <summary>The [[WeakRefTarget]] (a JSReceiver or an unregistered symbol).</summary>
        public WeakReference<HeapObject>? Target;

        /// <summary>The target, or null once it was collected.</summary>
        public HeapObject? GetTarget() => Target is not null && Target.TryGetTarget(out HeapObject? t) ? t : null;
    }

    /// <summary>V8's WeakCell: one registration of a FinalizationRegistry.</summary>
    public sealed class WeakCell(JSFinalizationRegistry finalizationRegistry, JSValue holdings, HeapObject target,
        HeapObject? unregisterToken) : HeapObject(InstanceType.WeakCellType)
    {
        public readonly JSFinalizationRegistry FinalizationRegistry = finalizationRegistry;
        public readonly JSValue Holdings = holdings;
        /// <summary>The target; null once cleared or unregistered (V8: undefined).</summary>
        public WeakReference<HeapObject>? Target = new(target);
        /// <summary>The unregister token, held weakly; null when there is none.</summary>
        public WeakReference<HeapObject>? UnregisterToken = unregisterToken is null ? null : new(unregisterToken);
        /// <summary>The identity hash of the unregister token (the key_map key).</summary>
        public int UnregisterTokenHash;

        // The doubly linked lists of the registry's active_cells / cleared_cells.
        public WeakCell? Prev;
        public WeakCell? Next;

        // The doubly linked list of cells with the same unregister-token hash.
        public WeakCell? KeyListPrev;
        public WeakCell? KeyListNext;

        public HeapObject? GetTarget() => Target is not null && Target.TryGetTarget(out HeapObject? t) ? t : null;

        public HeapObject? GetUnregisterToken() =>
            UnregisterToken is not null && UnregisterToken.TryGetTarget(out HeapObject? t) ? t : null;

        /// <summary>
        /// WeakCell::Nullify: the target died; move the cell from the active
        /// list to the head of the cleared list.
        /// </summary>
        public void Nullify()
        {
            Target = null;
            JSFinalizationRegistry fr = FinalizationRegistry;
            if (Prev is not null)
            {
                Debug.Assert(!ReferenceEquals(fr.ActiveCells, this));
                Prev.Next = Next;
            }
            else
            {
                Debug.Assert(ReferenceEquals(fr.ActiveCells, this));
                fr.ActiveCells = Next;
            }
            if (Next is not null) Next.Prev = Prev;

            Prev = null;
            WeakCell? clearedHead = fr.ClearedCells;
            if (clearedHead is not null) clearedHead.Prev = this;
            Next = clearedHead;
            fr.ClearedCells = this;
        }

        /// <summary>
        /// WeakCell::RemoveFromFinalizationRegistryCells: unlinks the cell from
        /// whichever list (active or cleared) it is in.
        /// </summary>
        public void RemoveFromFinalizationRegistryCells()
        {
            // It's important to set_target to undefined here. This guards that we won't
            // call Nullify (which assumes that the WeakCell is in active_cells).
            Target = null;
            JSFinalizationRegistry fr = FinalizationRegistry;
            if (ReferenceEquals(fr.ActiveCells, this))
            {
                Debug.Assert(Prev is null);
                fr.ActiveCells = Next;
            }
            else if (ReferenceEquals(fr.ClearedCells, this))
            {
                Debug.Assert(Prev is null);
                fr.ClearedCells = Next;
            }
            else
            {
                Prev!.Next = Next;
            }
            if (Next is not null) Next.Prev = Prev;
            Prev = null;
            Next = null;
        }
    }

    /// <summary>V8's JSFinalizationRegistry.</summary>
    public sealed class JSFinalizationRegistry(Map map) : JSObject(map)
    {
        public NativeContext NativeContext = null!;
        public JSValue Cleanup;
        public WeakCell? ActiveCells;
        public WeakCell? ClearedCells;
        /// <summary>
        /// key_map: unregister-token identity hash to the head of the cells with
        /// that hash (V8: a SimpleNumberDictionary; the tokens are held weakly,
        /// so the map is keyed on their identity hashes).
        /// </summary>
        public Dictionary<int, WeakCell>? KeyMap;
        public bool ScheduledForCleanup;
        /// <summary>Whether the isolate tracks this registry for GC-time clearing.</summary>
        internal bool Tracked;

        /// <summary>JSFinalizationRegistry::NeedsCleanup.</summary>
        public bool NeedsCleanup => ClearedCells is not null;

        /// <summary>PushCell (finalization-registry.tq): prepends to active_cells.</summary>
        public void PushCell(WeakCell cell)
        {
            Debug.Assert(ReferenceEquals(this, cell.FinalizationRegistry));
            cell.Next = ActiveCells;
            if (ActiveCells is not null) ActiveCells.Prev = cell;
            ActiveCells = cell;
        }

        /// <summary>JSFinalizationRegistry::RegisterWeakCellWithUnregisterToken.</summary>
        public static void RegisterWeakCellWithUnregisterToken(JSFinalizationRegistry finalizationRegistry, WeakCell weakCell,
            HeapObject unregisterToken)
        {
            Dictionary<int, WeakCell> keyMap = finalizationRegistry.KeyMap ??= [];

            // Unregister tokens are held weakly as objects are often their own
            // unregister token. To avoid using an ephemeron map, the map for token
            // lookup is keyed on the token's identity hash instead of the token itself.
            int key = (int)ObjectOps.GetOrCreateHashRaw(unregisterToken);
            weakCell.UnregisterTokenHash = key;
            if (keyMap.TryGetValue(key, out WeakCell? existingWeakCell))
            {
                existingWeakCell.KeyListPrev = weakCell;
                weakCell.KeyListNext = existingWeakCell;
            }
            keyMap[key] = weakCell;
        }

        /// <summary>JSFinalizationRegistry::Unregister.</summary>
        public static bool Unregister(JSFinalizationRegistry finalizationRegistry, HeapObject unregisterToken) =>
            // Iterate through the doubly linked list of WeakCells associated with the
            // key. Each WeakCell will be in the "active_cells" or "cleared_cells" list of
            // its FinalizationRegistry; remove it from there.
            finalizationRegistry.RemoveUnregisterToken(unregisterToken, true);

        /// <summary>
        /// JSFinalizationRegistry::RemoveUnregisterToken. With
        /// <paramref name="unregisterToken"/> null, removes the cells whose token
        /// died (the GC-time case, kKeepMatchedCellsInRegistry) under
        /// <paramref name="deadTokenHash"/>.
        /// </summary>
        bool RemoveUnregisterToken(HeapObject? unregisterToken, bool removeMatchedCellsFromRegistry, int deadTokenHash = 0)
        {
            if (KeyMap is null) return false;

            int key;
            if (unregisterToken is not null)
            {
                // If the token doesn't have a hash, it was not used as a key inside any hash
                // tables.
                JSValue hash = ObjectOps.GetHash(unregisterToken);
                if (hash.IsUndefined) return false;
                key = (int)hash.Number;
            }
            else
            {
                key = deadTokenHash;
            }
            if (!KeyMap.TryGetValue(key, out WeakCell? value)) return false;

            bool wasPresent = false;
            WeakCell? newKeyListHead = null;
            WeakCell? newKeyListPrev = null;
            // Compute a new key list that doesn't have unregister_token. Because
            // unregister tokens are held weakly, key_map is keyed using the tokens'
            // identity hashes, and identity hashes may collide.
            while (value is not null)
            {
                WeakCell weakCell = value;
                value = weakCell.KeyListNext;
                HeapObject? cellToken = weakCell.GetUnregisterToken();
                bool matches = unregisterToken is not null
                    ? ReferenceEquals(cellToken, unregisterToken)
                    : weakCell.UnregisterToken is not null && cellToken is null;
                if (matches)
                {
                    // weak_cell has the same unregister token; remove it from the key list.
                    if (removeMatchedCellsFromRegistry) weakCell.RemoveFromFinalizationRegistryCells();
                    // Clear unregister token-related fields.
                    weakCell.UnregisterToken = null;
                    weakCell.KeyListPrev = null;
                    weakCell.KeyListNext = null;
                    wasPresent = true;
                }
                else
                {
                    // weak_cell has a different unregister token with the same key (hash
                    // collision); fix up the list.
                    weakCell.KeyListPrev = newKeyListPrev;
                    weakCell.KeyListNext = null;
                    if (newKeyListPrev is null)
                    {
                        newKeyListHead = weakCell;
                    }
                    else
                    {
                        newKeyListPrev.KeyListNext = weakCell;
                    }
                    newKeyListPrev = weakCell;
                }
            }
            if (newKeyListHead is null)
            {
                KeyMap.Remove(key);
            }
            else
            {
                KeyMap[key] = newKeyListHead;
            }
            return wasPresent;
        }

        /// <summary>JSFinalizationRegistry::RemoveCellFromUnregisterTokenMap.</summary>
        void RemoveCellFromUnregisterTokenMap(WeakCell weakCell)
        {
            Debug.Assert(weakCell.UnregisterToken is not null);
            // Remove weak_cell from the linked list of other WeakCells with the same
            // unregister token and remove its unregister token from key_map if necessary.
            if (weakCell.KeyListPrev is null)
            {
                int key = weakCell.UnregisterTokenHash;
                if (weakCell.KeyListNext is null)
                {
                    // weak_cell is the only one associated with its key; remove the key
                    // from the hash table.
                    KeyMap!.Remove(key);
                }
                else
                {
                    // weak_cell is the list head for its key; we need to change the value
                    // of the key in the hash table.
                    WeakCell next = weakCell.KeyListNext;
                    next.KeyListPrev = null;
                    KeyMap![key] = next;
                }
            }
            else
            {
                // weak_cell is somewhere in the middle of its key list.
                WeakCell prev = weakCell.KeyListPrev;
                prev.KeyListNext = weakCell.KeyListNext;
                if (weakCell.KeyListNext is not null) weakCell.KeyListNext.KeyListPrev = prev;
            }

            // weak_cell is now removed from the unregister token map, so clear its
            // unregister token-related fields.
            weakCell.UnregisterToken = null;
            weakCell.KeyListPrev = null;
            weakCell.KeyListNext = null;
        }

        /// <summary>JSFinalizationRegistry::PopClearedCell.</summary>
        public WeakCell PopClearedCell()
        {
            WeakCell head = ClearedCells!;
            Debug.Assert(head.Prev is null);
            WeakCell? tail = head.Next;
            head.Next = null;
            if (tail is not null) tail.Prev = null;
            ClearedCells = tail;

            // If the WeakCell has an unregister token, remove the cell from the
            // unregister token linked lists and and the unregister token from key_map.
            if (head.UnregisterToken is not null) RemoveCellFromUnregisterTokenMap(head);
            return head;
        }

        /// <summary>
        /// JSFinalizationRegistry::Cleanup (ES #sec-cleanup-finalization-registry):
        /// calls the cleanup callback with the held value of every cleared cell.
        /// An exception propagates and interrupts the cleanup.
        /// </summary>
        public static void CleanupRegistry(Isolate isolate, JSFinalizationRegistry finalizationRegistry)
        {
            // 2. Let callback be finalizationRegistry.[[CleanupCallback]].
            JSValue callback = finalizationRegistry.Cleanup;

            // 3. While finalizationRegistry.[[Cells]] contains a Record cell such that
            //    cell.[[WeakRefTarget]] is empty, an implementation may perform the
            //    following steps:
            while (finalizationRegistry.NeedsCleanup)
            {
                // a. Choose any such cell.
                // b. Remove cell from finalizationRegistry.[[Cells]].
                WeakCell weakCell = finalizationRegistry.PopClearedCell();

                // c. Perform ? HostCallJobCallback(callback, undefined,
                //    « cell.[[HeldValue]] »).
                Execution.Call(isolate, callback, JSValue.Undefined, [weakCell.Holdings]);
            }
        }

        /// <summary>
        /// The GC-time part of MarkCompactCollector::ClearJSWeakRefs for one
        /// registry: nullifies the cells whose target died and drops the key-map
        /// entries of dead unregister tokens. Returns whether the registry
        /// became dirty (has cleared cells).
        /// </summary>
        internal bool ClearDeadCells()
        {
            WeakCell? cell = ActiveCells;
            while (cell is not null)
            {
                WeakCell? next = cell.Next;
                if (cell.Target is not null && cell.GetTarget() is null)
                {
                    // The value of the WeakCell is dead.
                    cell.Nullify();
                }
                cell = next;
            }

            // Unregister tokens that died: remove them from the key map, keeping
            // their cells in the registry.
            if (KeyMap is { Count: > 0 })
            {
                List<int>? deadKeys = null;
                foreach (KeyValuePair<int, WeakCell> entry in KeyMap)
                {
                    for (WeakCell? c = entry.Value; c is not null; c = c.KeyListNext)
                    {
                        if (c.UnregisterToken is not null && c.GetUnregisterToken() is null)
                        {
                            (deadKeys ??= []).Add(entry.Key);
                            break;
                        }
                    }
                }
                if (deadKeys is not null)
                {
                    foreach (int key in deadKeys) RemoveUnregisterToken(null, false, key);
                }
            }
            return NeedsCleanup;
        }
    }
}

namespace V8Sharp
{
    using V8Sharp.Objects;

    /// <summary>The weak-reference state V8 keeps on the Heap.</summary>
    public sealed partial class Isolate
    {
        readonly HashSet<HeapObject> _weakRefsKeepDuringJob = new(ReferenceEqualityComparer.Instance);
        readonly List<WeakReference<JSFinalizationRegistry>> _trackedFinalizationRegistries = [];
        readonly Queue<JSFinalizationRegistry> _dirtyFinalizationRegistries = new();
        bool _isFinalizationRegistryCleanupTaskPosted;
        int _lastObservedGcCount = -1;
        readonly Queue<Action<Isolate>> _foregroundTasks = new();

        /// <summary>Whether the KeepDuringJob set is non-empty (Heap::weak_refs_keep_during_job()).</summary>
        public bool HasKeptObjects => _weakRefsKeepDuringJob.Count != 0;

        /// <summary>Heap::KeepDuringJob / Runtime_JSWeakRefAddToKeptObjects.</summary>
        public void KeepDuringJob(HeapObject target) => _weakRefsKeepDuringJob.Add(target);

        /// <summary>
        /// Isolate::ClearKeptObjects (Heap::ClearKeptObjects): called after each
        /// microtask checkpoint. Also processes WeakCells cleared by a CLR GC
        /// that happened meanwhile.
        /// </summary>
        public void ClearKeptObjects()
        {
            _weakRefsKeepDuringJob.Clear();
            ProcessWeakCellsIfCollected();
        }

        /// <summary>
        /// A full garbage collection (d8's gc(), v8::Isolate::RequestGarbageCollectionForTesting):
        /// a blocking CLR collection, then the clearing of dead WeakCells and
        /// the posting of the FinalizationRegistry cleanup task.
        /// </summary>
        public void CollectGarbage()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            ProcessWeakCells();
        }

        /// <summary>Starts tracking a registry that got its first cell (so GC-time clearing sees it).</summary>
        internal void TrackFinalizationRegistry(JSFinalizationRegistry finalizationRegistry)
        {
            if (finalizationRegistry.Tracked) return;
            finalizationRegistry.Tracked = true;
            _trackedFinalizationRegistries.Add(new WeakReference<JSFinalizationRegistry>(finalizationRegistry));
        }

        void ProcessWeakCellsIfCollected()
        {
            if (_trackedFinalizationRegistries.Count == 0) return;
            int count = GC.CollectionCount(0);
            if (count == _lastObservedGcCount) return;
            ProcessWeakCells();
        }

        /// <summary>
        /// MarkCompactCollector::ClearJSWeakRefs (the WeakCell part): nullifies
        /// the cells of dead targets in every live registry, enqueues the dirty
        /// registries and posts the cleanup task if needed.
        /// </summary>
        public void ProcessWeakCells()
        {
            _lastObservedGcCount = GC.CollectionCount(0);
            for (int i = _trackedFinalizationRegistries.Count - 1; i >= 0; i--)
            {
                if (!_trackedFinalizationRegistries[i].TryGetTarget(out JSFinalizationRegistry? finalizationRegistry))
                {
                    // The registry itself died: its cells are never cleaned up.
                    _trackedFinalizationRegistries.RemoveAt(i);
                    continue;
                }
                bool dirty = finalizationRegistry.ClearDeadCells();
                if (finalizationRegistry.ActiveCells is null && finalizationRegistry.KeyMap is not { Count: > 0 })
                {
                    finalizationRegistry.Tracked = false;
                    _trackedFinalizationRegistries.RemoveAt(i);
                }
                if (dirty && !finalizationRegistry.ScheduledForCleanup)
                {
                    EnqueueDirtyJSFinalizationRegistry(finalizationRegistry);
                }
            }
            PostFinalizationRegistryCleanupTaskIfNeeded();
        }

        /// <summary>Heap::EnqueueDirtyJSFinalizationRegistry.</summary>
        void EnqueueDirtyJSFinalizationRegistry(JSFinalizationRegistry finalizationRegistry)
        {
            Debug.Assert(!finalizationRegistry.ScheduledForCleanup);
            finalizationRegistry.ScheduledForCleanup = true;
            _dirtyFinalizationRegistries.Enqueue(finalizationRegistry);
        }

        /// <summary>Heap::PostFinalizationRegistryCleanupTaskIfNeeded.</summary>
        void PostFinalizationRegistryCleanupTaskIfNeeded()
        {
            // Only one cleanup task is posted at a time.
            if (_dirtyFinalizationRegistries.Count == 0 || _isFinalizationRegistryCleanupTaskPosted) return;
            PostNonNestableTask(static isolate => isolate.RunFinalizationRegistryCleanupTask());
            _isFinalizationRegistryCleanupTaskPosted = true;
        }

        /// <summary>FinalizationRegistryCleanupTask::RunInternal.</summary>
        void RunFinalizationRegistryCleanupTask()
        {
            // First clear that the task is posted in case of early returns below.
            _isFinalizationRegistryCleanupTaskPosted = false;

            // There could be no dirty FinalizationRegistries.
            if (!_dirtyFinalizationRegistries.TryDequeue(out JSFinalizationRegistry? finalizationRegistry)) return;
            finalizationRegistry.ScheduledForCleanup = false;

            // Since FinalizationRegistry cleanup callbacks are scheduled by V8, enter the
            // FinalizationRegistry's context.
            NativeContext nativeContext = finalizationRegistry.NativeContext;
            using (EnterContext(nativeContext))
            {
                // Exceptions are reported via the message handler (a verbose TryCatch).
                // Cleanup is interrupted if there is an exception.
                try
                {
                    JSFinalizationRegistry.CleanupRegistry(this, finalizationRegistry);
                }
                catch (JavaScriptException e)
                {
                    (nativeContext.MicrotaskQueue ?? DefaultMicrotaskQueue).ReportMessageFromMicrotask(this, e);
                }
            }
            if (finalizationRegistry.NeedsCleanup && !finalizationRegistry.ScheduledForCleanup)
            {
                EnqueueDirtyJSFinalizationRegistry(finalizationRegistry);
            }

            // Repost if there are remaining dirty FinalizationRegistries.
            PostFinalizationRegistryCleanupTaskIfNeeded();
        }

        // ---- The foreground task runner (v8::Platform's GetForegroundTaskRunner) --------------------

        /// <summary>Raised when a task is posted, so an embedder can schedule <see cref="RunPendingTasks"/>.</summary>
        public event Action<Isolate>? ForegroundTaskPosted;

        /// <summary>TaskRunner::PostNonNestableTask on the isolate's foreground task runner (any thread).</summary>
        public void PostNonNestableTask(Action<Isolate> task)
        {
            lock (_foregroundTasks) _foregroundTasks.Enqueue(task);
            ForegroundTaskPosted?.Invoke(this);
        }

        readonly List<(long DueTicks, Action<Isolate> Task)> _delayedTasks = [];

        /// <summary>TaskRunner::PostNonNestableDelayedTask: runs <paramref name="task"/> after <paramref name="delaySeconds"/>.</summary>
        public void PostNonNestableDelayedTask(Action<Isolate> task, double delaySeconds)
        {
            long due = Environment.TickCount64 + (long)Math.Ceiling(Math.Min(delaySeconds * 1000, long.MaxValue / 4));
            lock (_foregroundTasks) _delayedTasks.Add((due, task));
            ForegroundTaskPosted?.Invoke(this);
        }

        /// <summary>Whether foreground tasks (immediate or delayed) are pending.</summary>
        public bool HasPendingTasks
        {
            get
            {
                lock (_foregroundTasks) return _foregroundTasks.Count != 0 || _delayedTasks.Count != 0;
            }
        }

        /// <summary>
        /// Moves the delayed tasks that are due to the task queue. When nothing
        /// else is pending, waits for the earliest one first (d8's message loop
        /// waits for delayed tasks the same way).
        /// </summary>
        void MoveDueDelayedTasks()
        {
            long wait;
            lock (_foregroundTasks)
            {
                if (_delayedTasks.Count == 0) return;
                long earliest = long.MaxValue;
                foreach (var (due, _) in _delayedTasks) earliest = Math.Min(earliest, due);
                wait = _foregroundTasks.Count == 0 ? earliest - Environment.TickCount64 : 0;
            }
            if (wait > 0) Thread.Sleep((int)Math.Min(wait, int.MaxValue));
            lock (_foregroundTasks)
            {
                long now = Environment.TickCount64;
                for (int i = 0; i < _delayedTasks.Count; i++)
                {
                    if (_delayedTasks[i].DueTicks > now) continue;
                    _foregroundTasks.Enqueue(_delayedTasks[i].Task);
                    _delayedTasks.RemoveAt(i--);
                }
            }
        }

        /// <summary>
        /// v8::platform::PumpMessageLoop: runs the pending foreground tasks (not
        /// the ones they post), each followed by a microtask checkpoint as the
        /// HTML event loop does. Returns whether any task ran.
        /// </summary>
        public bool RunPendingTasks()
        {
            MoveDueDelayedTasks();
            int n;
            lock (_foregroundTasks) n = _foregroundTasks.Count;
            if (n == 0) return false;
            for (int i = 0; i < n; i++)
            {
                Action<Isolate>? task;
                lock (_foregroundTasks)
                {
                    if (!_foregroundTasks.TryDequeue(out task)) break;
                }
                task(this);
                DefaultMicrotaskQueue.PerformCheckpoint(this);
            }
            return true;
        }
    }
}

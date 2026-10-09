// Copyright 2026 Curiosity GmbH
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

// V8Sharp: replaces the Microsoft.Extensions.ObjectPool package, of which WACS
// used DefaultObjectPool<T> with a PooledObjectPolicy<T>. An ExecContext (and
// so its pools) belongs to one thread, so the pool is a plain bounded stack.

using System.Collections.Generic;

namespace Wacs.Core.Utilities
{
    /// <summary>Creates pooled objects and resets them when they come back.</summary>
    public abstract class PooledObjectPolicy<T> where T : class
    {
        public abstract T Create();

        /// <summary>Prepares <paramref name="obj"/> for reuse; false drops it.</summary>
        public abstract bool Return(T obj);
    }

    /// <summary>A pool of reusable objects, as Microsoft.Extensions.ObjectPool's.</summary>
    public abstract class ObjectPool<T> where T : class
    {
        public abstract T Get();
        public abstract void Return(T obj);
    }

    /// <summary>
    /// Keeps up to <c>maximumRetained</c> returned objects. Not thread-safe:
    /// each ExecContext owns its pools.
    /// </summary>
    public sealed class DefaultObjectPool<T> : ObjectPool<T> where T : class
    {
        private readonly PooledObjectPolicy<T> _policy;
        private readonly Stack<T> _items;
        private readonly int _maximumRetained;

        public DefaultObjectPool(PooledObjectPolicy<T> policy, int maximumRetained)
        {
            _policy = policy;
            _maximumRetained = maximumRetained;
            _items = new Stack<T>(maximumRetained < 64 ? maximumRetained : 64);
        }

        public override T Get() => _items.Count > 0 ? _items.Pop() : _policy.Create();

        public override void Return(T obj)
        {
            if (!_policy.Return(obj)) return;
            if (_items.Count < _maximumRetained) _items.Push(obj);
        }
    }
}

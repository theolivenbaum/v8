// Installs d8's global surface (src/d8/d8.cc: Shell::CreateGlobalTemplate,
// CreateRealmTemplate, CreateD8Template, CreatePerformanceTemplate) in the
// current realm, on top of one host dispatcher: host(op, ...args).
// Evaluates to the installer; the installer returns the helpers D8Shell uses
// to report exceptions and run setTimeout callbacks.
(function install(host, isMainRealm, options, nativePrint, nativePrintErr, nativeWrite) {
  'use strict';
  const global = globalThis;
  const ObjectDefineProperty = Object.defineProperty;
  const Uint8ArrayCtor = global.Uint8Array;
  const StringCtor = String;
  const charCodeAt = Function.prototype.call.bind(String.prototype.charCodeAt);
  const ReflectApply = Reflect.apply;
  const FunctionToString = Function.prototype.toString;
  const JSONStringify = JSON.stringify;
  const ArrayIsArray = Array.isArray;
  const WeakMapCtor = WeakMap;
  const weakMapGet = WeakMap.prototype.get;
  const weakMapSet = WeakMap.prototype.set;
  const weakMapHas = WeakMap.prototype.has;
  const ErrorCtor = Error;
  const TypeErrorCtor = TypeError;

  // ObjectTemplate::Set with default attributes: writable, enumerable, configurable.
  function set(obj, name, value) {
    ObjectDefineProperty(obj, name, { value, writable: true, enumerable: true, configurable: true });
  }
  // A FunctionTemplate method that reads its receiver.
  function method(name, impl) {
    return { [name](...args) { return impl(this, args); } }[name];
  }
  // FunctionTemplate functions have length 0 and no prototype-visible source;
  // the closest JS equivalent is a method (no [[Construct]], no .prototype).
  function fn(name, impl) {
    const f = { [name](...args) { return impl(...args); } }[name];
    return f;
  }
  // WriteToFile: symbols print their description, everything else ToString.
  function toStr(a) {
    if (typeof a === 'symbol') a = a.description;
    return `${a}`;
  }
  function join(args) {
    let s = '';
    for (let i = 0; i < args.length; i++) {
      if (i !== 0) s += ' ';
      s += toStr(args[i]);
    }
    return s;
  }
  function binaryToArrayBuffer(s) {
    const bytes = new Uint8ArrayCtor(s.length);
    for (let i = 0; i < s.length; i++) bytes[i] = charCodeAt(s, i);
    return bytes.buffer;
  }
  function unsupported(name) {
    return fn(name, () => host('unsupported', name));
  }
  function readFile(name, format) {
    if (format !== undefined && toStr(format) === 'binary') return readBuffer(name);
    return host('read', toStr(name));
  }
  function readBuffer(name) {
    return binaryToArrayBuffer(host('readbuffer', toStr(name)));
  }
  function executeFile(...names) {
    for (const n of names) host('load', toStr(n));
  }

  ObjectDefineProperty(global, Symbol.toStringTag, { value: 'global', writable: true, enumerable: true, configurable: true });
  set(global, 'onerror', null);
  set(global, 'version', fn('version', () => host('version')));
  set(global, 'print', nativePrint !== undefined ? nativePrint : fn('print', (...a) => { host('print', join(a)); }));
  set(global, 'printErr', nativePrintErr !== undefined ? nativePrintErr : fn('printErr', (...a) => { host('printErr', join(a)); }));
  set(global, 'write', nativeWrite !== undefined ? nativeWrite : fn('write', (...a) => { host('write', join(a)); }));
  set(global, 'read', fn('read', readFile));
  set(global, 'readbuffer', fn('readbuffer', readBuffer));
  set(global, 'readline', fn('readline', () => undefined));
  set(global, 'load', fn('load', executeFile));
  set(global, 'setTimeout', fn('setTimeout', (callback) => {
    if (typeof callback === 'function') host('setTimeout', callback);
    return 0;
  }));
  if (!options.omitQuit) {
    set(global, 'quit', fn('quit', (code) => { host('quit', code === undefined ? 0 : code | 0); }));
  }

  const Realm = {};
  set(Realm, 'current', fn('current', () => host('realmCurrent')));
  set(Realm, 'owner', fn('owner', (o) => host('realmOwner', o)));
  set(Realm, 'global', fn('global', (i) => host('realmGlobal', i)));
  set(Realm, 'create', fn('create', (...a) => {
    // Shell::RealmCreate reads realm_options.create_own_microtask_queue.
    let ownQueue = false;
    const opts = a[0];
    if (a.length > 0 && ((typeof opts === 'object' && opts !== null) || typeof opts === 'function')) {
      // A verbose TryCatch: an exception from the getter is reported, not thrown.
      try {
        ownQueue = !!opts.create_own_microtask_queue;
      } catch (e) {
      }
    }
    return host('realmCreate', false, ownQueue);
  }));
  set(Realm, 'createAllowCrossRealmAccess', fn('createAllowCrossRealmAccess', () => host('realmCreate', true)));
  set(Realm, 'navigate', fn('navigate', (i) => host('realmNavigate', i, false)));
  set(Realm, 'navigateSameOrigin', fn('navigateSameOrigin', (i) => host('realmNavigate', i, true)));
  set(Realm, 'detachGlobal', fn('detachGlobal', (i) => host('realmDetachGlobal', i)));
  set(Realm, 'dispose', fn('dispose', (i) => host('realmDispose', i)));
  set(Realm, 'switch', fn('switch', (i) => host('realmSwitch', i)));
  set(Realm, 'eval', fn('eval', (i, s) => host('realmEval', i, s)));
  // The shared value lives in a JS object of the main realm, so any value
  // (a symbol too) survives without crossing into the host.
  ObjectDefineProperty(Realm, 'shared', {
    get: fn('shared', () => host('realmSharedBox').value),
    set: fn('shared', (v) => { host('realmSharedBox').value = v; }),
    enumerable: true,
    configurable: true,
  });
  set(global, 'Realm', Realm);

  const performance = {};
  set(performance, 'now', fn('now', () => host('performanceNow')));
  // Shell::PerformanceMark / PerformanceMeasure.
  const performanceEntry = (entryType, name, startTime, duration) => {
    const entry = {};
    ObjectDefineProperty(entry, 'entryType', { value: entryType, enumerable: true, configurable: true });
    ObjectDefineProperty(entry, 'name', { value: name, enumerable: true, configurable: true });
    ObjectDefineProperty(entry, 'startTime', { value: startTime, enumerable: true, configurable: true });
    ObjectDefineProperty(entry, 'duration', { value: duration, enumerable: true, configurable: true });
    return entry;
  };
  const lookupPerformanceMark = (name) => {
    const t = host('performanceMarkLookup', name);
    if (t === undefined) throw new ErrorCtor('Invalid performance.mark "' + name + '" does not exist');
    return t;
  };
  set(performance, 'mark', fn('mark', (...a) => {
    if (a.length < 1 || typeof a[0] !== 'string') throw new ErrorCtor("Invalid 'name' argument");
    return performanceEntry('mark', a[0], host('performanceMark', a[0]), 0);
  }));
  set(performance, 'measure', fn('measure', (...a) => {
    if (a.length < 1 || typeof a[0] !== 'string') throw new ErrorCtor("Invalid 'name' argument");
    let start = 0;
    let end = host('performanceNow');
    const startMark = a[1];
    if (typeof startMark === 'string') {
      start = lookupPerformanceMark(startMark);
      if (a.length === 3) {
        if (typeof a[2] !== 'string') throw new ErrorCtor('Expect string as end mark.');
        end = lookupPerformanceMark(a[2]);
      }
    } else if (startMark === undefined) {
    } else if ((typeof startMark !== 'object' || startMark === null) && typeof startMark !== 'function') {
      throw new ErrorCtor("Invalid 'startMark' argument: Not an Object");
    } else if (a.length > 2) {
      throw new ErrorCtor('Too many arguments');
    } else {
      const t = startMark.startTime;
      if (typeof t !== 'number') throw new ErrorCtor("Invalid 'startMark' argument: No numeric 'startTime' field");
      start = t;
    }
    return performanceEntry('measure', a[0], start, end - start);
  }));
  set(performance, 'measureMemory', unsupported('measureMemory'));
  set(global, 'performance', performance);

  if (options.serialization) {
    // Shell::CreateWorkerTemplate. The Worker's C++ object is the host's id,
    // kept in a WeakMap in place of the internal field.
    const workerIds = new WeakMapCtor();
    const workerId = (self) => {
      // The FunctionTemplate signature check.
      if (!ReflectApply(weakMapHas, workerIds, [self])) throw new TypeErrorCtor('Illegal invocation');
      return ReflectApply(weakMapGet, workerIds, [self]);
    };
    // Shell::ReadSource with CodeType::kFileName as the default.
    const readSource = (args) => {
      const arg0 = args[0];
      let type = 'none';
      let workerArguments;
      const opts = args[1];
      if (args.length > 1 && ((typeof opts === 'object' && opts !== null) || typeof opts === 'function')) {
        const t = opts.type;
        if (typeof t !== 'string') type = 'invalid';
        else if (t === 'classic') type = 'file';
        else if (t === 'string') type = 'string';
        else if (t === 'function') type = 'function';
        else type = 'invalid';
        workerArguments = opts.arguments;
      }
      if (type === 'none') type = 'file';
      switch (type) {
        case 'function': {
          if (typeof arg0 !== 'function') return undefined;
          // Shell::FunctionAndArgumentsToString: ( function_to_string )( params )
          // String::Concat returns an empty handle past String::kMaxLength.
          const concat = (x, y) => {
            try {
              return x + y;
            } catch (e) {
              throw new ErrorCtor('String limit exceeded');
            }
          };
          let source = concat('(', ReflectApply(FunctionToString, arg0, []));
          source = concat(source, ')(');
          if (workerArguments !== undefined) {
            if (!ArrayIsArray(workerArguments)) throw new ErrorCtor("'arguments' must be an array");
            for (let i = 0; i < workerArguments.length; i++) {
              if (i > 0) source = concat(source, ',');
              const argument = workerArguments[i];
              let argumentString;
              try {
                argumentString = JSONStringify(argument);
              } catch (e) {
                throw new ErrorCtor('Failed to convert argument to string');
              }
              source = concat(source, argumentString);
            }
          }
          return concat(source, ')');
        }
        case 'file':
          if (typeof arg0 !== 'string') return undefined;
          return host('read', arg0);
        case 'string':
          if (typeof arg0 !== 'string') return undefined;
          return arg0;
        default:
          return undefined;
      }
    };
    const Worker = function Worker(...args) {
      if (args.length < 1 || (typeof args[0] !== 'string' && typeof args[0] !== 'function')) {
        throw new ErrorCtor('1st argument must be a string or a function');
      }
      const source = readSource(args);
      if (source === undefined) throw new ErrorCtor('Invalid argument');
      if (new.target === undefined) throw new ErrorCtor('Worker must be constructed with new');
      ReflectApply(weakMapSet, workerIds, [this, 0]);
      const id = host('workerNew', toStr(source));
      ReflectApply(weakMapSet, workerIds, [this, id]);
    };
    const proto = Worker.prototype;
    set(proto, 'terminate', method('terminate', (self) => {
      const id = workerId(self);
      if (id) host('workerTerminate', id);
    }));
    set(proto, 'terminateAndWait', method('terminateAndWait', (self) => {
      const id = workerId(self);
      if (id) host('workerTerminateAndWait', id);
    }));
    set(proto, 'postMessage', method('postMessage', (self, args) => {
      const id = workerId(self);
      if (args.length < 1) throw new ErrorCtor('Invalid argument');
      if (id) host('workerPostMessage', id, args[0], args.length >= 2 ? args[1] : undefined);
    }));
    set(proto, 'getMessage', method('getMessage', (self) => {
      const id = workerId(self);
      if (id) return host('workerGetMessage', id);
    }));
    ObjectDefineProperty(proto, 'onmessage', {
      get: method('onmessage', (self) => {
        const id = workerId(self);
        if (id) return host('workerOnMessageGet', id);
      }),
      set: method('onmessage', (self, args) => {
        const id = workerId(self);
        if (args.length < 1) throw new ErrorCtor('Invalid argument');
        if (!id || typeof args[0] !== 'function') return;
        host('workerOnMessageSet', id, args[0]);
      }),
      enumerable: false,
      configurable: true,
    });
    // ReadOnlyPrototype.
    ObjectDefineProperty(Worker, 'prototype', { writable: false });
    set(global, 'Worker', Worker);
    if (options.isWorker) {
      // Worker::ExecuteInThread installs postMessage, close and importScripts.
      set(global, 'postMessage', fn('postMessage', (...a) => {
        if (a.length < 1) throw new ErrorCtor('Invalid argument');
        host('postMessageOut', a[0], a.length >= 2 ? a[1] : undefined);
      }));
      set(global, 'close', fn('close', () => { host('workerClose'); }));
      set(global, 'importScripts', fn('importScripts', executeFile));
    }
  } else {
    set(global, 'Worker', unsupported('Worker'));
  }

  // D8Console (src/d8/d8-console.cc), the console delegate behind V8's own
  // console object: the methods d8 implements print; the rest stay no-ops.
  const console = global.console;
  if (!options.nativeConsole && console !== null && typeof console === 'object') {
    const timers = new Map();
    const origin = host('performanceNow');
    const label = (args) => args.length === 0 ? 'default' : `${args[0]}`;
    const ms = (t) => (host('performanceNow') - t).toFixed(6);
    const out = (prefix, args) => host('print', prefix === null ? join(args) : prefix + ': ' + join(args));
    set(console, 'log', fn('log', (...a) => { out(null, a); }));
    set(console, 'error', fn('error', (...a) => { host('printErr', 'console.error: ' + join(a)); }));
    set(console, 'warn', fn('warn', (...a) => { out('console.warn', a); }));
    set(console, 'info', fn('info', (...a) => { out('console.info', a); }));
    set(console, 'debug', fn('debug', (...a) => { out('console.debug', a); }));
    set(console, 'assert', fn('assert', (...a) => {
      if (a.length > 0 && a[0]) return;
      out('console.assert', a);
      throw new Error('console.assert failed');
    }));
    set(console, 'time', fn('time', (...a) => {
      const l = label(a);
      if (timers.has(l)) host('print', `console.time: Timer '${l}' already exists`);
      else timers.set(l, host('performanceNow'));
    }));
    set(console, 'timeLog', fn('timeLog', (...a) => {
      const l = label(a);
      if (!timers.has(l)) host('print', `console.timeLog: Timer '${l}' does not exist`);
      else host('print', `console.timeLog: ${l}, ${ms(timers.get(l))}`);
    }));
    set(console, 'timeEnd', fn('timeEnd', (...a) => {
      const l = label(a);
      if (!timers.has(l)) { host('print', `console.timeEnd: Timer '${l}' does not exist`); return; }
      host('print', `console.timeEnd: ${l}, ${ms(timers.get(l))}`);
      timers.delete(l);
    }));
    set(console, 'timeStamp', fn('timeStamp', (...a) => {
      host('print', `console.timeStamp: ${label(a)}, ${ms(origin)}`);
    }));
  }

  const d8 = {};
  const file = {};
  set(file, 'read', fn('read', readFile));
  set(file, 'execute', fn('execute', executeFile));
  set(file, 'exists', fn('exists', (n) => host('exists', toStr(n))));
  set(d8, 'file', file);
  const log = {};
  set(log, 'getAndStop', fn('getAndStop', () => ''));
  set(d8, 'log', log);
  const test = {};
  // An indexed loop, not for-of: the shim must not depend on the builtins
  // under test (Array.prototype[Symbol.iterator]).
  const testNames = ['verifySourcePositions', 'installConditionalFeatures', 'setFlushDenormals',
                     'createInterceptorObject', 'createAccessCheckedObject',
                     'createAccessCheckedInterceptorObject', 'setAccessPolicy'];
  for (let i = 0; i < testNames.length; i++) {
    set(test, testNames[i], unsupported('d8.test.' + testNames[i]));
  }
  set(d8, 'test', test);
  const promise = {};
  set(promise, 'setHooks', unsupported('d8.promise.setHooks'));
  set(d8, 'promise', promise);
  const debuggerObj = {};
  set(debuggerObj, 'enable', unsupported('d8.debugger.enable'));
  set(debuggerObj, 'disable', unsupported('d8.debugger.disable'));
  set(d8, 'debugger', debuggerObj);
  const serializer = {};
  if (options.serialization) {
    set(serializer, 'serialize', fn('serialize', (...a) => host('serializerSerialize', ...a)));
    set(serializer, 'deserialize', fn('deserialize', (b) => host('serializerDeserialize', b)));
  } else {
    set(serializer, 'serialize', unsupported('d8.serializer.serialize'));
    set(serializer, 'deserialize', unsupported('d8.serializer.deserialize'));
  }
  set(d8, 'serializer', serializer);
  const profiler = {};
  set(profiler, 'setOnProfileEndListener', unsupported('d8.profiler.setOnProfileEndListener'));
  set(profiler, 'triggerSample', unsupported('d8.profiler.triggerSample'));
  set(d8, 'profiler', profiler);
  const constants = {};
  set(constants, 'maxFixedArrayCapacity', options.maxFixedArrayCapacity);
  set(constants, 'maxFastArrayLength', options.maxFastArrayLength);
  set(d8, 'constants', constants);
  set(d8, 'terminateNow', fn('terminateNow', () => host('terminateNow')));
  set(d8, 'terminate', fn('terminate', () => host('terminate')));
  set(d8, 'getExtrasBindingObject', unsupported('d8.getExtrasBindingObject'));
  if (!options.omitQuit) {
    set(d8, 'quit', fn('quit', (code) => { host('quit', code === undefined ? 0 : code | 0); }));
  }
  set(global, 'd8', d8);

  if (isMainRealm && !options.noArguments) {
    set(global, 'arguments', []);
  }

  // Helpers for D8Shell, not visible to scripts.
  return {
    // ReportException: ToString(exception), or undefined if that throws.
    exceptionToString(e) {
      try { return `${e}`; } catch { return undefined; }
    },
    // v8::TryCatch::StackTrace: exception.stack when it is a string.
    exceptionStack(e) {
      try {
        if ((typeof e === 'object' && e !== null) || typeof e === 'function') {
          const s = e.stack;
          if (typeof s === 'string') return s;
        }
      } catch { }
      return undefined;
    },
    // global.onerror(message, source, lineno, colno, error) === true suppresses the report.
    callOnError(message, source, lineno, colno, error) {
      const onerror = global.onerror;
      if (typeof onerror !== 'function') return false;
      try { return onerror.call(global, message, source, lineno, colno, error) === true; } catch { return false; }
    },
    // Realm.owner: the realm whose Object.prototype/Function.prototype is on o's chain.
    isOwnedHere(o) {
      try {
        for (let p = o; p !== null; p = Object.getPrototypeOf(p)) {
          if (p === Object.prototype || p === Function.prototype) return true;
        }
      } catch { }
      return false;
    },
    // The event object of a Worker message: {data}.
    makeEvent(data) { return { data }; },
    isFunction(f) { return typeof f === 'function'; },
    // Calls a setTimeout callback with an undefined receiver.
    callTask(f) { f(); },
  };
})

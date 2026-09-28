// Installs d8's global surface (src/d8/d8.cc: Shell::CreateGlobalTemplate,
// CreateRealmTemplate, CreateD8Template, CreatePerformanceTemplate) in the
// current realm, on top of one host dispatcher: host(op, ...args).
// Evaluates to the installer; the installer returns the helpers D8Shell uses
// to report exceptions and run setTimeout callbacks.
(function install(host, isMainRealm, options) {
  'use strict';
  const global = globalThis;
  const ObjectDefineProperty = Object.defineProperty;
  const Uint8ArrayCtor = global.Uint8Array;
  const StringCtor = String;
  const charCodeAt = Function.prototype.call.bind(String.prototype.charCodeAt);

  // ObjectTemplate::Set with default attributes: writable, enumerable, configurable.
  function set(obj, name, value) {
    ObjectDefineProperty(obj, name, { value, writable: true, enumerable: true, configurable: true });
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
  set(global, 'print', fn('print', (...a) => { host('print', join(a)); }));
  set(global, 'printErr', fn('printErr', (...a) => { host('printErr', join(a)); }));
  set(global, 'write', fn('write', (...a) => { host('write', join(a)); }));
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
  set(Realm, 'create', fn('create', () => host('realmCreate', false)));
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
  set(performance, 'mark', fn('mark', (name) => ({ entryType: 'mark', name: `${name}`, startTime: host('performanceNow'), duration: 0 })));
  set(performance, 'measure', fn('measure', (name) => ({ entryType: 'measure', name: `${name}`, startTime: 0, duration: host('performanceNow') })));
  set(performance, 'measureMemory', unsupported('measureMemory'));
  set(global, 'performance', performance);

  set(global, 'Worker', unsupported('Worker'));

  // D8Console (src/d8/d8-console.cc), the console delegate behind V8's own
  // console object: the methods d8 implements print; the rest stay no-ops.
  const console = global.console;
  if (console !== null && typeof console === 'object') {
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
  set(serializer, 'serialize', unsupported('d8.serializer.serialize'));
  set(serializer, 'deserialize', unsupported('d8.serializer.deserialize'));
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
    // Calls a setTimeout callback with an undefined receiver.
    callTask(f) { f(); },
  };
})

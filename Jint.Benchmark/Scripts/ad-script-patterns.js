// The shapes of compiled ad-tech scripts that dominate a page's resize handler: polyfill dispatch, iterators driven
// by hand in try/finally, push.apply over copies, rest through arguments and apply, spread plugin dispatch, protobuf
// field reads on arrays, and a recursive schema validator.
var lib = {};
var renamed = {};
lib.poly = { Symbol: Symbol, Set: Set };
lib.get = function (o, name, opt) { if (!opt || o != null) { opt = renamed[name]; if (opt == null) return o[name]; opt = o[opt]; return opt !== void 0 ? opt : o[name]; } };
var arrayStepper = function (a) { var i = 0; return function () { return i < a.length ? { done: false, value: a[i++] } : { done: true }; }; };
lib.iter = function (a) { var f = typeof lib.poly.Symbol != "undefined" && lib.get(lib.poly.Symbol, "iterator") && a[lib.get(lib.poly.Symbol, "iterator")]; if (f) return f.call(a); if (typeof a.length == "number") return { next: arrayStepper(a) }; throw Error(String(a) + " is not iterable"); };
lib.toArray = function (a) { if (!(a instanceof Array)) { a = lib.iter(a); for (var r, out = []; !(r = a.next()).done;) out.push(r.value); a = out; } return a; };
lib.close = function (it, r) { var f; r && !r.done && (f = it["return"]) && f.call(it); };
lib.rest = function () { for (var from = Number(this), out = [], i = from; i < arguments.length; i++) out[i - from] = arguments[i]; return out; };
var isPlain = function (o) { if (typeof o !== "object" || o === null) return false; var p = Object.getPrototypeOf(o); if (p === null) return true; for (var q = p; Object.getPrototypeOf(q) !== null;) q = Object.getPrototypeOf(q); return p === q; };
var join = function (path, key) { return path ? lib.get(key, "startsWith").call(key, "[") ? path + key : path + "." + key : key; };

// Collects the issues of a value against a schema, as tag managers check every targeting call.
var check = function (schema, value, path) {
  path = path === void 0 ? "" : path;
  var issues = [], it, r, i, k, v, found;
  if (value === void 0) return schema.optional || issues.push({ path: path, kind: "MISSING" }), issues;
  switch (schema.type) {
    case "string":
    case "number":
      typeof value !== schema.type && issues.push({ path: path, kind: "WRONG_TYPE" });
      break;
    case "enum":
      lib.get(schema.values, "includes").call(schema.values, value) || issues.push({ path: path, kind: "WRONG_ENUM" });
      break;
    case "array":
      if (!Array.isArray(value)) issues.push({ path: path, kind: "NOT_ARRAY" });
      else for (i = 0; i < value.length; i++) issues.push.apply(issues, lib.toArray(check(schema.item, value[i], path + "[" + i + "]")));
      break;
    case "record":
      if (!isPlain(value)) { issues.push({ path: path, kind: "NOT_OBJECT" }); break; }
      it = lib.iter(lib.get(Object, "entries").call(Object, value));
      r = it.next();
      try {
        for (; !r.done; r = it.next()) {
          var pair = lib.iter(r.value);
          k = pair.next().value;
          v = pair.next().value;
          found = [];
          found.push.apply(found, lib.toArray(check(schema.key, k, join(path, "key(" + k + ")"))));
          found.push.apply(found, lib.toArray(check(schema.value, v, join(path, k))));
          issues.push.apply(issues, lib.toArray(found));
        }
      } finally {
        lib.close(it, r);
      }
      break;
    case "object":
      if (!isPlain(value)) { issues.push({ path: path, kind: "NOT_OBJECT" }); break; }
      it = lib.iter(lib.get(Object, "keys").call(Object, value));
      r = it.next();
      try {
        for (; !r.done; r = it.next()) Object.prototype.hasOwnProperty.call(schema.shape, r.value) || issues.push({ path: join(path, r.value), kind: "UNKNOWN" });
      } finally {
        lib.close(it, r);
      }
      for (k in schema.shape) Object.prototype.hasOwnProperty.call(schema.shape, k) && issues.push.apply(issues, lib.toArray(check(schema.shape[k], value[k], join(path, k))));
      break;
    case "union":
      it = lib.iter(schema.types);
      r = it.next();
      found = [];
      try {
        for (; !r.done; r = it.next()) { v = check(r.value, value, path); if (v.length === 0) { found = null; break; } found.push(v); }
      } finally {
        lib.close(it, r);
      }
      found && issues.push({ path: path, kind: "NO_UNION_MEMBER" });
      break;
  }
  return issues;
};

var STR = { type: "string" };
var TARGETING = { type: "record", key: STR, value: { type: "union", types: [STR, { type: "array", item: STR }] } };
var SLOT = { type: "object", shape: { unit: STR, sizes: { type: "array", item: { type: "array", item: { type: "number" } } }, div: STR, targeting: TARGETING, collapse: { type: "enum", values: ["a", "b", "c"] } } };
var SLOT_VALUE = { unit: "/1/site/front", sizes: [[728, 90], [970, 250]], div: "slot-1", collapse: "b",
  targeting: { pos: "top", kw: ["a", "b", "c"], cat: ["tech"], vp: "desktop", aud: ["x1", "x2"], seg: ["s1", "s2", "s3", "s4"] } };

// A public method wrapper: rest through arguments, apply, try/catch/finally.
var reporter = { report: function () {}, last: 0 };
var wrap = function (owner, name, fn) { return function () { var args = lib.rest.apply(0, arguments); try { return fn.apply(this, args); } catch (e) { throw owner.report(name, e), e; } finally { try { owner.last = args.length; } catch (e) {} } }; };
var command = function (kind) { return function () { return { kind: kind, args: [].concat(lib.toArray(lib.rest.apply(0, arguments))) }; }; };

// Rest/spread error wrappers and plugin dispatch.
var guarded = (fn, onError) => ((...args) => { try { let r = fn(...args); return r instanceof Promise ? r.then(x => x, x => onError(x, ...args)) : r; } catch (e) { return onError(e, ...args); } });
var hooks = [], plugins = {};
function register(fn) { hooks.push(guarded((out, ...args) => { out.push(fn(...args)); }, () => {})); }
plugins.collect = (...args) => { let out = []; for (let fn of hooks) fn(out, ...args); return out; };
for (var h = 0; h < 6; h++) register((slot, key) => key + h);
var mergeUnique = (...lists) => { let out = []; for (let list of lists) if (list != null) for (let x of list) out.includes(x) || out.push(x); return out; };

// Protobuf messages over arrays, flagged with a symbol.
var FLAGS = Symbol.for("flags");
var field = function (a, n, ext, convert) { if (n === -1) return null; var i = n + (ext ? 0 : -1), last = a.length - 1; if (!(last < 1 + (ext ? 0 : -1))) { var v; if (i >= last) { var tail = a[last]; if (tail != null && typeof tail === "object" && tail.constructor === Object) v = tail[n]; else if (i === last) v = tail; else return; } else v = a[i]; if (convert && v != null) { var c = convert(v); if (c == null) return c; if (!lib.get(Object, "is").call(Object, c, v)) return a[i] = c; } return v; } };
var read = function (m, n, ext, d, convert) { n = field(m.data, n, ext, convert); if (n !== null || d) return n; };
var readString = function (m, n) { return read(m, n, void 0, {}); };
function Message(data) { this.data = data; data[FLAGS] = (data[FLAGS] | 0) | 1; }

var CASES = {
  dispatcher: function (n) { var s = 0; for (var i = 0; i < n; i++) s += lib.get(Object, "keys") === Object.keys ? 1 : 0; return s; },
  iteratorTryFinally: function (n) { var a = ["a", "b", "c", "d"], s = 0; for (var i = 0; i < n; i++) { var it = lib.iter(a), r = it.next(); try { for (; !r.done; r = it.next()) s += r.value.length; } finally { lib.close(it, r); } } return s; },
  pushApply: function (n) { var s = 0, two = [1, 2]; for (var i = 0; i < n; i++) { var out = []; out.push.apply(out, lib.toArray(two)); out.push.apply(out, lib.toArray([])); s += out.length; } return s; },
  restThroughArguments: function (n) { var f = wrap(reporter, "m", function (a, b) { return a + b; }), s = 0; for (var i = 0; i < n; i++) s += f(i, 1); return s; },
  concatSpread: function (n) { var f = command(1), s = 0; for (var i = 0; i < n; i++) s += f(i, 2, 3).args.length; return s; },
  spreadPlugins: function (n) { var s = 0; for (var i = 0; i < n; i++) s += plugins.collect("slot", "key").length; return s; },
  mergeUnique: function (n) { var a = ["a", "b"], b = ["b", "c"], s = 0; for (var i = 0; i < n; i++) s += mergeUnique(a, b, null).length; return s; },
  protobufRead: function (n) { var m = new Message([1, "x", 3, {}]), s = 0; for (var i = 0; i < n; i++) s += readString(m, 2) === "x" ? 1 : 0; return s; },
  symbolTaggedArrays: function (n) { var s = 0; for (var i = 0; i < n; i++) s += new Message([i, "x"]).data[FLAGS]; return s; },
  forOfEntries: function (n) { var o = { a: 1, b: 2, c: 3 }, s = 0; for (var i = 0; i < n; i++) { for (let [k, v] of Object.entries(o)) s += v; } return s; },
  schemaValidator: function (n) { var s = 0; for (var i = 0; i < n; i++) s += check(SLOT, SLOT_VALUE, "slot").length + 1; return s; },
};

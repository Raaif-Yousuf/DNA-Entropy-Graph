// Bridge between the WinUI host (IgvViewerHost) and igv.js (issues #72, #73).
// Commands in (window.chrome.webview 'message'): {cmd:"load", config}, {cmd:"goto", locus},
//   {cmd:"seams", label, color, seams:[{chr, pos}]},{cmd:"track", op:"add"|"remove", config|name}, {cmd:"theme", dark:bool}, {cmd:"snapshot"}, {cmd:"focusLocus"}.
// Events out (postMessage): {evt:"ready"}, {evt:"loaded"}, {evt:"warning", message},
//   {evt:"error", message} (fatal: the run could not be drawn), {evt:"locusChanged", locus},
//   {evt:"snapshot", svg}, {evt:"trackFailed", name, message} (an added track could not load), {evt:"key", key, ctrl} (issue #247: the host decides what a key does),
//   and {evt:"loaded", locus} carries the locus first shown. Commands in also include {cmd:"keys", enabled}.
// Runs with no network: igv.js is vendored beside this file and every URL it fetches is
// https://run.deg/... (the run's output folder, mapped by the host).
(function () {
  'use strict';

  var container = document.getElementById('igv');
  var browser = null;
  var wv = window.chrome && window.chrome.webview;
  // Loads (and every other command) run one at a time, so two quick "load" commands cannot
  // leave an orphaned igv browser behind.
  var queue = Promise.resolve();

  function post(msg) {
    if (wv) { wv.postMessage(msg); }
  }

  function describe(err) {
    return String((err && err.message) || err);
  }

  // Stray page errors (a 404 track, a ResizeObserver loop) are noise, not a failed load.
  window.addEventListener('error', function (e) { post({ evt: 'warning', message: describe(e.error || e.message) }); });
  window.addEventListener('unhandledrejection', function (e) { post({ evt: 'warning', message: describe(e.reason) }); });

  // The locus the first reference frame shows, in the same form as locusChanged.
  function currentLocus() {
    var f = browser && browser.referenceFrameList && browser.referenceFrameList[0];
    return f ? f.chr + ':' + Math.floor(f.start + 1) + '-' + Math.floor(f.end) : null;
  }

  async function load(config) {
    if (browser) {
      igv.removeBrowser(browser);
      browser = null;
    }
    browser = await igv.createBrowser(container, {
      reference: config.reference,
      tracks: config.tracks || [],
      showNavigation: true,
      showSampleNames: false
    });
    browser.on('locuschange', function (frames) {
      var f = frames && frames[0];
      if (f) { post({ evt: 'locusChanged', locus: f.chr + ':' + Math.floor(f.start + 1) + '-' + Math.floor(f.end) }); }
    });
    post({ evt: 'loaded', locus: currentLocus() });
  }

  // Seam markers (issue #116): one thin region of interest per contig at the 0-based base where the
  // reverse pass hands over to the forward pass. igv.js draws a region of interest as a vertical band
  // across every track; one base wide it reads as a line once zoomed in. Replaces any earlier set.
  // The colour comes from the host (issue #195: one palette definition, in C#); none sent leaves igv.js's own region colour.
  async function markSeams(seams, label, color) {
    browser.clearROIs();
    var regions = (seams || []).map(function (s) {
      var r = { chr: s.chr, start: s.pos, end: s.pos + 1, name: label || '' };
      if (color) { r.color = color; }
      return r;
    });
    if (regions.length > 0) { await browser.loadROI(regions); }
  }


  // Issue #552: an optional track (a smoothed entropy track) is added or removed by name; the load config is untouched, so the raw
  // track stays primary and a reload starts without them. Adding twice or removing one that is absent is a no-op.
  async function changeTrack(cmd) {
    if (cmd.op === 'add' && cmd.config) {
      // A track that cannot be loaded (404, bad file) must not leave its switch ticked with nothing drawn: tell the host.
      try {
        if (browser.findTracks('name', cmd.config.name).length === 0) { await browser.loadTrack(cmd.config); }
      } catch (err) {
        post({ evt: 'trackFailed', name: cmd.config.name, message: describe(err) });
      }
    } else if (cmd.op === 'remove' && cmd.name) {
      browser.removeTrackByName(cmd.name);
    }
  }

  async function handle(cmd) {
    switch (cmd && cmd.cmd) {
      case 'load':
        try {
          await load(cmd.config);
        } catch (err) {
          post({ evt: 'error', message: describe(err) });
        }
        break;
      case 'track': if (browser) { await changeTrack(cmd); } break;
      case 'goto': if (browser) { await browser.search(cmd.locus); } break;
      case 'seams': if (browser) { await markSeams(cmd.seams, cmd.label, cmd.color); } break;      case 'theme': document.body.classList.toggle('dark', !!cmd.dark); break;
      case 'focusLocus': focusLocusBox(); break;
      case 'keys': keysOn = !!cmd.enabled; break;
      case 'snapshot': if (browser) { post({ evt: 'snapshot', svg: browser.toSVG() }); } break;
    }
  }

  function focusLocusBox() {
    var box = container.querySelector('.igv-search-input') || container.querySelector('input[type="text"]');
    if (box) { box.focus(); box.select(); }
  }

  // Issue #247: forward the navigation keys. No decision here (the host's ViewerKeyboard owns them), except to
  // stay out of the way while the user types in the locus box or an igv dialog.
  var keysOn = false; // set by the host ({cmd:"keys"}) only while a run is drawn, so no key is swallowed otherwise
  var FORWARDED = ['ArrowLeft', 'ArrowRight', '+', '=', '-', '_', 'Home', '1', '2', '3'];
  document.addEventListener('keydown', function (e) {
    if (!keysOn) { return; }
    var t = e.target;
    var typing = t && (t.tagName === 'INPUT' || t.tagName === 'TEXTAREA' || t.tagName === 'SELECT' || t.isContentEditable ||
      (t.closest && t.closest('[role="listbox"], [role="combobox"], select')));
    if (typing && !(e.ctrlKey && !e.altKey && !e.metaKey)) { return; }
    var ctrl = e.ctrlKey || e.metaKey;
    if (e.altKey || (ctrl ? String(e.key).toLowerCase() !== 'l' : FORWARDED.indexOf(e.key) < 0)) { return; }
    e.preventDefault();
    post({ evt: 'key', key: e.key, ctrl: ctrl });
  });

  if (wv) {
    wv.addEventListener('message', function (e) {
      queue = queue.then(function () { return handle(e.data); }).catch(function (err) {
        post({ evt: 'warning', message: describe(err) });
      });
    });
  }

  post({ evt: 'ready' });
}());

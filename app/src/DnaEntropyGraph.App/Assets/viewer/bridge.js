// Bridge between the WinUI host (IgvViewerHost) and igv.js (issues #72, #73).
// Commands in (window.chrome.webview 'message'): {cmd:"load", config}, {cmd:"goto", locus},
//   {cmd:"theme", dark:bool}, {cmd:"snapshot"}.
// Events out (postMessage): {evt:"ready"}, {evt:"loaded"}, {evt:"warning", message},
//   {evt:"error", message} (fatal: the run could not be drawn), {evt:"locusChanged", locus},
//   {evt:"snapshot", svg}.
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
    post({ evt: 'loaded' });
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
      case 'goto': if (browser) { await browser.search(cmd.locus); } break;
      case 'theme': document.body.classList.toggle('dark', !!cmd.dark); break;
      case 'snapshot': if (browser) { post({ evt: 'snapshot', svg: browser.toSVG() }); } break;
    }
  }

  if (wv) {
    wv.addEventListener('message', function (e) {
      queue = queue.then(function () { return handle(e.data); }).catch(function (err) {
        post({ evt: 'warning', message: describe(err) });
      });
    });
  }

  post({ evt: 'ready' });
}());

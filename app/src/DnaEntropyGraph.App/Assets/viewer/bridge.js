// Bridge between the WinUI host (IgvViewerHost) and igv.js (issues #72, #73).
// Commands in (window.chrome.webview 'message'): {cmd:"load", config}, {cmd:"goto", locus},
//   {cmd:"seams", label, seams:[{chr, pos}]}, {cmd:"theme", dark:bool}, {cmd:"snapshot"}.
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

  // Seam markers (issue #116): one thin region of interest per contig at the 0-based base where the
  // reverse pass hands over to the forward pass. igv.js draws a region of interest as a vertical band
  // across every track; one base wide it reads as a line once zoomed in. Replaces any earlier set.
  async function markSeams(seams, label) {
    browser.clearROIs();
    var regions = (seams || []).map(function (s) {
      return { chr: s.chr, start: s.pos, end: s.pos + 1, name: label || '', color: 'rgba(220, 60, 60, 0.6)' };
    });
    if (regions.length > 0) { await browser.loadROI(regions); }
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
      case 'seams': if (browser) { await markSeams(cmd.seams, cmd.label); } break;
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

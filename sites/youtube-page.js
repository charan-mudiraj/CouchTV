// Videos per row on YouTube's grids (home, subscriptions, a channel's videos). Runs among the page's own scripts
// ("world": "MAIN"), as only they can reach the grid. youtube.js puts the number on <html data-couchtv-row="5">.
//
// YouTube's grid works out how many fit (elementsPerRow) and lays out its rows and shelves from that. Each grid's
// count is made to read the chosen number, so YouTube lays itself out for it: shelves land between whole rows.
// Shorts grids keep YouTube's own count.
'use strict';

(function () {
  var wanted = 0;

  function chosen() {
    var n = parseInt(document.documentElement.getAttribute('data-couchtv-row') || '0', 10);
    return n >= 1 && n <= 8 ? n : 0;
  }

  function mainGrid(c) {
    return !(c.isShortsGrid || c.isCompactGrid || c.isShortsPanel || c.isShortsGridSlim);
  }

  /** The grid's layout object, once its count reads the chosen number; null if YouTube's changed how it works. */
  function hook(grid) {
    var c = grid.polymerController || grid;
    if (c.__couchtvRow) return c;
    if (typeof c.refreshGridLayout !== 'function') return null;
    var real = null;
    for (var p = Object.getPrototypeOf(c); p && !real; p = Object.getPrototypeOf(p)) {
      real = Object.getOwnPropertyDescriptor(p, 'elementsPerRow');
    }
    if (!real || !real.get || !real.set) return null;
    try {
      Object.defineProperty(c, 'elementsPerRow', {
        configurable: true,
        get: function () { return real.get.call(this); },
        set: function (v) { real.set.call(this, wanted && mainGrid(this) ? wanted : v); },
      });
    } catch (e) {
      return null;
    }
    c.__couchtvRow = true;
    return c;
  }

  function apply() {
    var n = chosen();
    var changed = n !== wanted;
    wanted = n;
    var grids = document.querySelectorAll('ytd-rich-grid-renderer');
    for (var i = 0; i < grids.length; i++) {
      var grid = grids[i];
      var fresh = !(grid.polymerController || grid).__couchtvRow;
      var c = hook(grid);
      if (!c) {
        // YouTube's changed inside: the plain way, widths only (a shelf may then follow a part-filled row).
        if (wanted) grid.style.setProperty('--ytd-rich-grid-items-per-row', String(wanted), 'important');
        else grid.style.removeProperty('--ytd-rich-grid-items-per-row');
        continue;
      }
      if (changed || (fresh && wanted)) {
        try { c.refreshGridLayout(true); } catch (e) { /* laid out again on the next resize */ }
      }
    }
  }

  new MutationObserver(apply).observe(document.documentElement, { attributes: true, attributeFilter: ['data-couchtv-row'] });
  setInterval(apply, 1000);   // grids come and go as pages change
  apply();
})();

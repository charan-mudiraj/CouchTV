// JioHotstar without its ads on films and shows (HotstarAds, on by default). Runs among the page's own scripts, before
// them (PageScripts puts it in as each page starts, in Edge on CouchTV's own profile), as uBlock Origin's filters for
// the site do: the player's data says where ads go (intervention_data: preroll and midroll), and those are taken out
// as the page reads it, so the player plays straight through. CouchTV also has Edge refuse JioHotstar's ad servers
// (PageScripts), and its video banners are hidden here. Live streams have their ads in the video itself: those stay.
'use strict';

(function () {
  var taken = 0;

  /** Takes out the ad slots wherever data names them (inside intervention_data); how many it took. */
  function clean(value, depth) {
    if (!value || typeof value !== 'object' || depth > 24) return 0;
    var count = 0;
    if (Array.isArray(value)) {
      for (var i = 0; i < value.length; i++) count += clean(value[i], depth + 1);
      return count;
    }
    var ads = value.intervention_data;
    if (ads && typeof ads === 'object') {
      ['interventions', 'sources'].forEach(function (list) {
        var items = ads[list];
        if (!items || typeof items !== 'object') return;
        Object.keys(items).forEach(function (k) {
          var meta = items[k] && items[k].meta;
          if (!meta || typeof meta !== 'object') return;
          ['preroll', 'midroll'].forEach(function (slot) {
            if (Object.prototype.hasOwnProperty.call(meta, slot)) {
              delete meta[slot];
              count++;
            }
          });
        });
      });
    }
    for (var key in value) {
      if (key !== 'intervention_data' && Object.prototype.hasOwnProperty.call(value, key)) count += clean(value[key], depth + 1);
    }
    return count;
  }

  function after(text, value) {
    // Only data that names ad slots is looked through (the rest goes by untouched, and quickly).
    if (typeof text !== 'string' || text.indexOf('intervention_data') === -1) return value;
    var n = clean(value, 0);
    if (n > 0) {
      taken += n;
      if (document.documentElement) document.documentElement.setAttribute('data-couchtv-ads', String(taken));
    }
    return value;
  }

  var parse = JSON.parse;
  JSON.parse = function (text) {
    return after(text, parse.apply(this, arguments));
  };

  if (typeof Response !== 'undefined' && Response.prototype && Response.prototype.text) {
    Response.prototype.json = function () {
      return this.text().then(function (text) {
        return after(text, parse(text));
      });
    };
  }

  // Its video banners and ad spots on the pages.
  function hideBanners() {
    var style = document.createElement('style');
    style.textContent = 'div[data-testid^="ad-"], div[data-testid="bbtype-video"] { display: none !important; }';
    (document.head || document.documentElement).appendChild(style);
  }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', hideBanners);
  else hideBanners();
})();

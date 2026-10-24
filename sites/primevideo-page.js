// Prime Video without its ads on films and shows (PrimeAds, on by default), as uBlock Origin's filters for it do. Runs
// among the page's own scripts, before them (PageScripts puts it in as each page starts, in Edge on CouchTV's own
// profile):
// - the player asks for its stream without "deviceAdInsertionTypeOverride", so Amazon doesn't stitch ads into it;
// - the ad breaks' cue points are taken out of the stream's data (cuepointPlaylist, vodPlaybackUrls…cuepoints);
// - Amazon's check for an ad blocker (ue_adb_chk) reads as if there were none.
// CouchTV also has Edge refuse its ad videos and ad requests (PageScripts).
'use strict';

(function () {
  var taken = 0;

  function count(n) {
    if (!n) return;
    taken += n;
    if (document.documentElement) document.documentElement.setAttribute('data-couchtv-ads', String(taken));
  }

  // Amazon's ad-blocker check: always "none".
  try {
    Object.defineProperty(window, 'ue_adb_chk', { configurable: false, get: function () { return 1; }, set: function () {} });
  } catch (e) {}

  /** The ad breaks' cue points out of a stream's data; the rest as it was. */
  function clean(value) {
    if (!value || typeof value !== 'object') return value;
    var n = 0;
    if (Object.prototype.hasOwnProperty.call(value, 'cuepointPlaylist')) {
      delete value.cuepointPlaylist;
      n++;
    }
    var urls = value.vodPlaybackUrls && value.vodPlaybackUrls.result && value.vodPlaybackUrls.result.playbackUrls;
    if (urls && typeof urls === 'object' && Object.prototype.hasOwnProperty.call(urls, 'cuepoints')) {
      delete urls.cuepoints;
      n++;
    }
    count(n);
    return value;
  }

  function after(text, value) {
    // Only data that has cue points is looked at (the rest goes by untouched).
    return typeof text === 'string' && text.indexOf('cuepoint') !== -1 ? clean(value) : value;
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

  /** A request for the stream, without the setting that lets Amazon stitch ads into it. */
  function strip(url) {
    if (typeof url !== 'string' || url.indexOf('GetPlaybackResources') === -1 || url.indexOf('deviceAdInsertionTypeOverride') === -1) return url;
    try {
      var u = new URL(url, location.href);
      u.searchParams.delete('deviceAdInsertionTypeOverride');
      count(1);
      return u.href;
    } catch (e) {
      return url;
    }
  }

  if (typeof XMLHttpRequest !== 'undefined') {
    var open = XMLHttpRequest.prototype.open;
    XMLHttpRequest.prototype.open = function (method, url) {
      var args = Array.prototype.slice.call(arguments);
      if (args.length > 1) args[1] = strip(String(url));
      return open.apply(this, args);
    };
  }

  if (typeof window.fetch === 'function') {
    var realFetch = window.fetch;
    window.fetch = function (input, init) {
      if (typeof input === 'string') input = strip(input);
      else if (input && typeof input.url === 'string' && input.url.indexOf('GetPlaybackResources') !== -1) {
        var url = strip(input.url);
        if (url !== input.url) input = new Request(url, input);
      }
      return realFetch.call(this, input, init);
    };
  }
})();

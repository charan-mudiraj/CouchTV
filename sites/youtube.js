// YouTube on the TV (with tv.js): the arrows move between videos on the home page, search results, subscriptions,
// channels and playlists, and down the list beside a playing video; OK opens one. Videos per row: youtube-page.js.
'use strict';

(function () {
  if (!CouchTV) return;   // already in this page by the other route
  var CARDS = [
    'ytd-rich-item-renderer',                 // home, subscriptions, a channel's videos
    'ytd-video-renderer',                     // search results, history
    'yt-lockup-view-model',                   // the newer cards: beside a playing video, playlists, mixes
    'ytd-compact-video-renderer', 'ytd-compact-radio-renderer', 'ytd-compact-playlist-renderer',
    'ytd-playlist-renderer', 'ytd-radio-renderer', 'ytd-channel-renderer', 'ytd-movie-renderer',
    'ytd-grid-video-renderer', 'ytd-playlist-video-renderer', 'ytd-playlist-panel-video-renderer',
    'ytm-shorts-lockup-view-model-v2', 'ytm-shorts-lockup-view-model', 'ytd-reel-item-renderer',
    'yt-chip-cloud-chip-renderer',            // the topics along the top (All, Music, Gaming...)
  ].join(',');

  var LINKS = 'a#thumbnail[href], a[href*="/watch"], a[href*="/shorts/"], a[href*="/playlist"], a[href^="/@"], ' +
              'a[href*="/channel/"], a[href]';

  function watching() { return location.pathname === '/watch'; }

  function chip(el) { return el.tagName === 'YT-CHIP-CLOUD-CHIP-RENDERER'; }

  /** One of YouTube's menus or dialogs is open: it has the arrows. */
  function popupOpen() {
    var popups = document.querySelectorAll('tp-yt-iron-dropdown, tp-yt-paper-dialog, ytd-popup-container yt-sheet-view-model');
    for (var i = 0; i < popups.length; i++) {
      var p = popups[i];
      if (p.getAttribute('aria-hidden') !== 'true' && p.style.display !== 'none' && CouchTV.shown(p)) return true;
    }
    return false;
  }

  /** The header (and the topics under it, which stay put too) cover the top of the page. */
  function top() {
    var head = document.getElementById('masthead-container');
    var bottom = head ? head.getBoundingClientRect().bottom : 56;
    var chips = document.querySelector('ytd-feed-filter-chip-bar-renderer, #chips-wrapper');
    if (chips && CouchTV.shown(chips) && getComputedStyle(chips.closest('#header, #chips-wrapper') || chips).position !== 'static') {
      bottom = Math.max(bottom, chips.getBoundingClientRect().bottom);
    }
    return Math.max(0, bottom);
  }

  /** Search suggestions are showing under the search box (Down moves through them, as always). */
  function suggestionsOpen(box) {
    var id = box.getAttribute('aria-controls');
    var list = id && document.getElementById(id);
    return !!(list && CouchTV.shown(list) && list.querySelector('[role="option"], li'));
  }

  CouchTV.site({
    radius: 16,
    top: top,

    cards: function () {
      return CouchTV.all(document, CARDS).filter(function (el) {
        return !el.querySelector('ytd-ad-slot-renderer, ytd-in-feed-ad-layout-renderer');   // ad slots (Brave hides them)
      });
    },

    // The first press: beside a playing video, the first in the list; elsewhere the first video, not a topic.
    start: function (list) {
      var videos = list.filter(function (el) { return !chip(el); });
      if (watching()) {
        var side = videos.filter(function (el) { return el.closest('#secondary, #below, #playlist'); });
        return CouchTV.first(side.length ? side : videos);
      }
      return CouchTV.first(videos);
    },

    // Beside a playing video with nothing highlighted, the arrows are YouTube's (seek, volume); Down goes to the list.
    passes: function (key, highlighted) {
      if (location.pathname.indexOf('/shorts/') === 0) return true;   // Shorts: up and down are next and previous
      if (popupOpen()) return true;
      if (watching() && !highlighted) return key === 'left' || key === 'right' || key === 'up';
      return false;
    },

    // OK with nothing highlighted, on a playing video: full screen, as a TV app plays it.
    enter: function () {
      if (!watching()) return false;
      var focused = document.activeElement;
      if (focused && focused !== document.body && !focused.closest('#movie_player')) return false;
      if (focused && /^(BUTTON|A)$/.test(focused.tagName)) return false;
      return CouchTV.click(document.querySelector('#movie_player .ytp-fullscreen-button'));
    },

    // Past the top (or left, beside a video): back to the video, or the top of the page.
    edge: function (from, dir) {
      if (watching() && (dir === 'up' || dir === 'left')) {
        CouchTV.clear();
        window.scrollTo({ top: 0, behavior: 'smooth' });
      } else if (dir === 'up') {
        window.scrollTo({ top: 0, behavior: 'smooth' });
      }
    },

    // A sideways shelf (an artist's videos in search results, the topics along the top) slides with its own arrow
    // buttons, not by scrolling.
    across: function (el, dir) {
      var shelf = el.closest('horizontal-shelf-view-model, yt-horizontal-list-renderer, ytd-horizontal-card-list-renderer, ' +
                             'ytd-feed-filter-chip-bar-renderer');
      if (!shelf) return;
      var clip = el.parentElement;
      while (clip && clip !== shelf && getComputedStyle(clip).overflowX === 'visible') clip = clip.parentElement;
      var box = (clip || shelf).getBoundingClientRect(), r = el.getBoundingClientRect();
      if (dir === 'right' ? r.right <= box.right + 2 : r.left >= box.left - 2) return;
      var buttons = shelf.querySelectorAll('button, #right-arrow, #left-arrow');
      for (var i = 0; i < buttons.length; i++) {
        var b = buttons[i], label = (b.getAttribute('aria-label') || '').trim();
        if (dir === 'right' ? label === 'Next' || b.id === 'right-arrow' : label === 'Previous' || b.id === 'left-arrow') {
          CouchTV.click(b.querySelector('button') || b);
          return;
        }
      }
    },

    open: function (el) {
      if (chip(el)) {
        CouchTV.click(el.querySelector('button, #chip-container, [role="tab"]') || el);
        return;
      }
      CouchTV.click(el.querySelector(LINKS) || el);
    },

    leaveBox: function (box) {
      return box.name === 'search_query' && !suggestionsOpen(box);
    },
  });

  // Videos per row (0: YouTube's own choice), for youtube-page.js, which runs inside the page's own scripts.
  CouchTV.onSettings(function (s) {
    var n = parseInt(s.youtubeRow, 10) || 0;
    document.documentElement.setAttribute('data-couchtv-row', String(n >= 1 && n <= 8 ? n : 0));
    CouchTV.status('youtube: arrows ' + (s.keys ? 'on' : 'off') + ', videos per row ' + (n || 'YouTube\'s choice') + ', by ' +
      document.documentElement.getAttribute('data-couchtv'));
  });
})();

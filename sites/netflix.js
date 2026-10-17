// Netflix on the TV (with tv.js): the arrows move between titles (a row pages on at its end), through a title's
// details (Play, episodes, more like it) and the profiles; OK opens one. The player keeps every key. With "Tidy
// Netflix" on, the big trailer at the top of its pages and the games rows go, so the rows start at the top.
'use strict';

(function () {
  var TITLES = '.title-card-container, .titleCard--container, .titleCardList--container, .slider-item .title-card, ' +
               '.profile-link, .billboard-links button, .billboard-links a, [data-uia="play-button"]';
  var ANYTHING = 'a[href], button, [role="button"], [role="link"], [tabindex="0"]';

  var tidy = false;

  function playing() { return location.pathname.indexOf('/watch') === 0; }

  function big(el) {
    var r = el.getBoundingClientRect();
    return r.width >= 28 && r.height >= 24;
  }

  /** A title's details (or another dialog), open over the rows: the highlight stays inside it. */
  function dialog() {
    var open = document.querySelectorAll('.previewModal--wrapper.detail-modal, [role="dialog"], [aria-modal="true"]');
    for (var i = open.length - 1; i >= 0; i--) {
      var r = open[i].getBoundingClientRect();
      if (r.width > 300 && r.height > 200 && CouchTV.shown(open[i])) return open[i];
    }
    return null;
  }

  CouchTV.site({
    radius: 6,
    pad: 4,
    wholeAcross: true,   // a row's half-shown titles at its ends are reached by paging it
    top: function () {
      var head = document.querySelector('.pinning-header-container, .main-header, [data-uia="header"]');
      return head ? Math.max(0, head.getBoundingClientRect().bottom) : 70;
    },

    cards: function () {
      var inside = dialog();
      if (inside) return CouchTV.all(inside, ANYTHING).filter(big);
      var titles = CouchTV.all(document, TITLES);
      return titles.length ? titles : CouchTV.all(document, ANYTHING).filter(big);
    },

    passes: function () { return playing(); },

    // At a row's end, page it on (or back) and land on its first (or last) title now in view.
    edge: function (from, dir) {
      if (dir !== 'left' && dir !== 'right') return;
      var row = from.closest('.lolomoRow, .rowContainer, .slider');
      var handle = row && row.querySelector(dir === 'right' ? '.handleNext' : '.handlePrev');
      if (!handle) return;
      CouchTV.click(handle);
      setTimeout(function () {
        var now = CouchTV.candidates().filter(function (el) { return row.contains(el); });
        now.sort(function (a, b) { return a.getBoundingClientRect().left - b.getBoundingClientRect().left; });
        if (now.length) CouchTV.show(dir === 'right' ? now[0] : now[now.length - 1]);
      }, 850);
    },

    open: function (el) {
      var link = el.matches('a, button, [role="button"]') ? el : el.querySelector('a[href], button, [role="button"], [role="link"]');
      CouchTV.click(link || el);
    },
  });

  // ------------------------------------------------------------------ tidy

  var style = document.createElement('style');
  style.textContent =
    'html.couchtv-tidy .billboard-row, html.couchtv-tidy .billboard-presentation-tracking,' +
    'html.couchtv-tidy [data-uia="billboard"], html.couchtv-tidy .couchtv-gone { display: none !important; }' +
    // The rows sat over the trailer's bottom edge; without it they start under the header instead.
    'html.couchtv-tidy .lolomo { margin-top: 0 !important; padding-top: 96px !important; }';
  document.documentElement.appendChild(style);

  var GAMES = /(^|\s)(mobile|netflix|party|tv|top 10 mobile) games\b|^games\b/i;

  function tidyUp() {
    if (!tidy) return;
    // The hidden trailer would still play (and be heard).
    document.querySelectorAll('.billboard-row video, .billboard video, [data-uia="billboard"] video').forEach(function (v) {
      if (!v.paused || !v.muted) { v.muted = true; v.pause(); }
    });
    document.querySelectorAll('.lolomoRow').forEach(function (row) {
      var context = (row.getAttribute('data-list-context') || '').toLowerCase();
      var head = row.querySelector('.rowHeader, .row-header-title, h2');
      if (context.indexOf('game') >= 0 || (head && GAMES.test(head.textContent.trim()))) row.classList.add('couchtv-gone');
    });
  }

  CouchTV.onSettings(function (s) {
    tidy = !!s.netflixTidy;
    document.documentElement.classList.toggle('couchtv-tidy', tidy);
    if (!tidy) document.querySelectorAll('.couchtv-gone').forEach(function (row) { row.classList.remove('couchtv-gone'); });
    tidyUp();
  });
  setInterval(tidyUp, 1000);
})();

// Shared site behavior: mobile header menu, collapsible docs sidebar, and the
// "On this page" list. Everything here is progressive enhancement -- with JS off,
// the header keeps its GitHub button, the docs sidebar shows in full, and the
// footer links every page.
(function () {
  "use strict";

  var NARROW_NAV = window.matchMedia("(max-width: 980px)");
  var NARROW_SIDEBAR = window.matchMedia("(max-width: 860px)");

  function onChange(mq, fn) {
    if (mq.addEventListener) mq.addEventListener("change", fn);
    else if (mq.addListener) mq.addListener(fn);
  }

  // ---- Header menu ----
  var header = document.querySelector(".site-header");
  var toggle = header && header.querySelector(".nav-toggle");
  var nav = header && header.querySelector(".site-nav");
  if (header && toggle && nav) {
    header.classList.add("has-menu");
    toggle.hidden = false;

    var setOpen = function (open, returnFocus) {
      header.classList.toggle("menu-open", open);
      toggle.setAttribute("aria-expanded", open ? "true" : "false");
      if (!open && returnFocus) toggle.focus();
    };

    toggle.addEventListener("click", function () {
      setOpen(toggle.getAttribute("aria-expanded") !== "true", false);
    });
    nav.addEventListener("click", function (e) {
      if (e.target.closest("a")) setOpen(false, false);
    });
    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape" && header.classList.contains("menu-open")) setOpen(false, true);
    });
    document.addEventListener("click", function (e) {
      if (header.classList.contains("menu-open") && !header.contains(e.target)) setOpen(false, false);
    });
    onChange(NARROW_NAV, function (mq) { if (!mq.matches) setOpen(false, false); });
  }

  // ---- Docs sidebar ----
  var sidebar = document.querySelector(".docs-sidebar");
  var sideToggle = sidebar && sidebar.querySelector(".sidebar-toggle");
  if (sidebar && sideToggle) {
    sidebar.classList.add("has-toggle");
    sideToggle.hidden = false;
    var setSide = function (open) {
      sidebar.classList.toggle("is-open", open);
      sideToggle.setAttribute("aria-expanded", open ? "true" : "false");
    };
    sideToggle.addEventListener("click", function () {
      setSide(sideToggle.getAttribute("aria-expanded") !== "true");
    });
    document.addEventListener("keydown", function (e) {
      if (e.key === "Escape" && sidebar.classList.contains("is-open") && NARROW_SIDEBAR.matches) {
        setSide(false);
        sideToggle.focus();
      }
    });
    onChange(NARROW_SIDEBAR, function (mq) { if (!mq.matches) setSide(false); });
  }

  // ---- On this page ----
  var toc = document.querySelector(".docs-toc");
  var doc = document.querySelector(".doc");
  if (toc && doc) {
    var headings = Array.prototype.slice.call(doc.querySelectorAll("h2[id]"));
    if (headings.length < 2) {
      toc.hidden = true;
      return;
    }
    var list = document.createElement("ul");
    var links = headings.map(function (h) {
      var li = document.createElement("li");
      var a = document.createElement("a");
      a.href = "#" + h.id;
      a.textContent = h.textContent;
      li.appendChild(a);
      list.appendChild(li);
      return a;
    });
    toc.appendChild(list);

    if ("IntersectionObserver" in window) {
      var visible = new Map();
      var observer = new IntersectionObserver(function (entries) {
        entries.forEach(function (entry) { visible.set(entry.target.id, entry.isIntersecting); });
        var current = null;
        for (var i = 0; i < headings.length; i++) {
          if (visible.get(headings[i].id)) { current = headings[i].id; break; }
        }
        if (!current) return;
        links.forEach(function (a) { a.classList.toggle("is-active", a.getAttribute("href") === "#" + current); });
      }, { rootMargin: "-70px 0px -55% 0px" });
      headings.forEach(function (h) { observer.observe(h); });
    }
  }
})();

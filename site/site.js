// Search, the "On this page" outline, copy buttons and image zoom. Everything here is an
// enhancement: with scripts off, every page still reads top to bottom and every link works.
(() => {
  // data-index is relative to the page, and result links are relative to the index.
  const indexUrl = new URL(document.currentScript.dataset.index, location.href);
  const docsBase = new URL(".", indexUrl);

  document.addEventListener("DOMContentLoaded", () => {
    search();
    outline();
    copyButtons();
    zoom();
    // The contents list collapses on narrow screens, where it sits above the page.
    const sidebar = document.querySelector(".sidebar");
    if (sidebar && matchMedia("(max-width: 860px)").matches) sidebar.open = false;
  });

  // ---- search ----------------------------------------------------------------------------------
  function search() {
    const input = document.getElementById("q");
    const list = document.getElementById("results");
    if (!input || !list) return;

    let entries = null;
    let active = -1;

    const load = async () => {
      if (entries) return entries;
      try {
        entries = await (await fetch(indexUrl)).json();
      } catch {
        entries = [];
      }
      return entries;
    };

    const escape = (s) => s.replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);

    // Every term has to appear somewhere; a hit in the page title or section heading outranks one
    // in the body, and an exact phrase outranks scattered words.
    const rank = (entry, terms, phrase) => {
      const title = entry.t.toLowerCase(), head = entry.h.toLowerCase(), body = entry.x.toLowerCase();
      let score = 0;
      for (const term of terms) {
        const inTitle = title.includes(term), inHead = head.includes(term), inBody = body.includes(term);
        if (!inTitle && !inHead && !inBody) return 0;
        score += (inTitle ? 6 : 0) + (inHead ? 4 : 0) + (inBody ? 1 : 0);
      }
      if (terms.length > 1 && (head.includes(phrase) || body.includes(phrase))) score += 5;
      return score;
    };

    const snippet = (text, terms) => {
      const lower = text.toLowerCase();
      const at = Math.max(0, Math.min(...terms.map((t) => lower.indexOf(t)).filter((i) => i >= 0)) - 50);
      let out = escape((at > 0 ? "…" : "") + text.slice(at, at + 160) + (text.length > at + 160 ? "…" : ""));
      for (const term of terms) {
        out = out.replace(new RegExp(term.replace(/[.*+?^${}()|[\]\\]/g, "\\$&"), "gi"), (m) => `<mark>${m}</mark>`);
      }
      return out;
    };

    const show = async () => {
      const query = input.value.trim().toLowerCase();
      active = -1;
      if (query.length < 2) {
        list.hidden = true;
        return;
      }
      const terms = query.split(/\s+/);
      const hits = (await load())
        .map((e) => [rank(e, terms, query), e])
        .filter(([score]) => score > 0)
        .sort((a, b) => b[0] - a[0])
        .slice(0, 10);

      list.innerHTML = hits.length
        ? hits.map(([, e]) => {
            const href = new URL(e.p + (e.a ? "#" + e.a : ""), docsBase).href;
            return `<li><a href="${href}"><span class="where">${escape(e.t)}${e.h ? " › " + escape(e.h) : ""}</span>`
              + `<span class="what">${snippet(e.x, terms)}</span></a></li>`;
          }).join("")
        : `<li class="none">Nothing matches “${escape(input.value.trim())}”.</li>`;
      list.hidden = false;
    };

    const move = (by) => {
      const items = [...list.querySelectorAll("a")];
      if (!items.length) return;
      active = (active + by + items.length) % items.length;
      items.forEach((a, i) => a.classList.toggle("active", i === active));
      items[active].scrollIntoView({ block: "nearest" });
    };

    input.addEventListener("focus", load, { once: true });
    input.addEventListener("input", show);
    input.addEventListener("keydown", (e) => {
      if (e.key === "ArrowDown") { e.preventDefault(); move(1); }
      else if (e.key === "ArrowUp") { e.preventDefault(); move(-1); }
      else if (e.key === "Enter") {
        const target = list.querySelectorAll("a")[Math.max(active, 0)];
        if (target) location.href = target.href;
      } else if (e.key === "Escape") { list.hidden = true; input.blur(); }
    });
    document.addEventListener("keydown", (e) => {
      if (e.key === "/" && document.activeElement !== input && !/input|textarea/i.test(document.activeElement.tagName)) {
        e.preventDefault();
        input.focus();
      }
    });
    document.addEventListener("click", (e) => {
      if (!e.target.closest(".search")) list.hidden = true;
    });
  }

  // ---- outline ---------------------------------------------------------------------------------
  // Marks the section being read. The heading nearest the top of the viewport wins.
  function outline() {
    const links = [...document.querySelectorAll(".toc a")];
    if (!links.length) return;
    const targets = links.map((a) => document.getElementById(decodeURIComponent(a.hash.slice(1)))).filter(Boolean);

    const update = () => {
      let current = targets[0];
      for (const t of targets) {
        if (t.getBoundingClientRect().top <= 100) current = t;
      }
      links.forEach((a) => a.classList.toggle("current", a.hash.slice(1) === current?.id));
    };
    addEventListener("scroll", update, { passive: true });
    update();
  }

  // ---- copy buttons ----------------------------------------------------------------------------
  function copyButtons() {
    document.querySelectorAll(".prose pre").forEach((block) => {
      if (block.querySelector(".copy")) return;
      const button = document.createElement("button");
      button.type = "button";
      button.className = "copy";
      button.textContent = "Copy";
      button.addEventListener("click", async () => {
        const code = block.querySelector("code") ?? block;
        try {
          await navigator.clipboard.writeText(code.innerText.trimEnd());
          button.textContent = "Copied";
        } catch {
          button.textContent = "Select and copy";
        }
        setTimeout(() => (button.textContent = "Copy"), 1500);
      });
      block.appendChild(button);
    });
  }

  // ---- image zoom ------------------------------------------------------------------------------
  // Screenshots are linked to themselves in the wiki; here they open over the page instead.
  function zoom() {
    const overlay = document.createElement("div");
    overlay.className = "zoom";
    overlay.hidden = true;
    overlay.innerHTML = "<img alt=''>";
    overlay.addEventListener("click", () => (overlay.hidden = true));
    addEventListener("keydown", (e) => { if (e.key === "Escape") overlay.hidden = true; });
    document.body.appendChild(overlay);

    document.querySelectorAll(".prose img, .shot img, .gallery img").forEach((img) => {
      const link = img.closest("a");
      (link ?? img).addEventListener("click", (e) => {
        e.preventDefault();
        overlay.querySelector("img").src = link?.href ?? img.src;
        overlay.querySelector("img").alt = img.alt;
        overlay.hidden = false;
      });
      img.style.cursor = "zoom-in";
    });
  }
})();

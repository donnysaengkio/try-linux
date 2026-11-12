// Choose a Linux OS (the first step of Create): a search over the built-in five and the repository's list,
// then your own installer.
import { Fragment, useEffect, useMemo, useRef, useState } from "react";
import { toast } from "sonner";
import { ask } from "./Ask";
import { Header } from "./Header";
import { Logo } from "./Logo";
import { moreDistros, RECOMMENDED, type Distro } from "./distros";
import { openPage } from "./tauri";

const OWN = "own";
let more: Distro[] | null = null; // the repository's catalog, once loaded (kept between visits)

export function Choose({ onBack, onNext, onOwn }: { onBack: (() => void) | null; onNext: (d: Distro) => void; onOwn: () => void }) {
  const [query, setQuery] = useState("");
  const [loaded, setLoaded] = useState(more);
  const [chosen, setChosen] = useState<string | null>(RECOMMENDED[0].id);
  const search = useRef<HTMLInputElement>(null);

  // Ctrl+F: the search box.
  useEffect(() => {
    const key = (e: KeyboardEvent) => {
      if (e.ctrlKey && !e.altKey && e.key.toLowerCase() === "f") { e.preventDefault(); search.current?.focus(); search.current?.select(); }
    };
    addEventListener("keydown", key);
    return () => removeEventListener("keydown", key);
  }, []);

  useEffect(() => {
    search.current?.focus();
    if (!more) moreDistros().then(d => { more = d; setLoaded(d); });
  }, []);

  // By name, description or id, every word; names that start with the text come first.
  const q = query.trim().toLowerCase(), words = q.split(/\s+/).filter(Boolean);
  const shown = useMemo(() => {
    if (words.length === 0) return RECOMMENDED;
    const all = RECOMMENDED.concat((loaded ?? []).filter(d => !RECOMMENDED.some(r => r.id === d.id)));
    return all.filter(d => words.every(w => `${d.name} ${d.about ?? ""} ${d.id}`.toLowerCase().includes(w)))
      .sort((a, b) => Number(b.name.toLowerCase().startsWith(q)) - Number(a.name.toLowerCase().startsWith(q)));
  }, [q, loaded]);

  // The choice follows the search to its first result, unless it's your own installer.
  useEffect(() => {
    if (chosen !== OWN) setChosen(shown[0]?.id ?? null);
  }, [shown]);

  const picked = shown.find(d => d.id === chosen);
  // A distribution Try Linux can't download itself: offer its website, then Use Your Own Installer for the
  // ISO once it's downloaded.
  const next = async () => {
    if (chosen === OWN) return onOwn();
    if (!picked) return toast("Choose a distribution first.");
    if (!picked.base) {
      if (picked.home && await ask(`Download ${picked.name} from its website?`,
        `Try Linux can't fetch the ${picked.name} ISO file itself. Open its website to download the ISO, then choose it in Use Your Own Installer to install it.`,
        "Open Website")) { openPage(picked.home); onOwn(); }
      return;
    }
    onNext(picked);
  };

  return (
    <Fragment>
      <Header />
      <main className="page">
        <div className="column">
          <Logo />
          <h1 className="center">Choose a Linux OS</h1>
          <div className="subtitle">Try Linux downloads the official installer and checks it before use.</div>
          <input ref={search} className="search" placeholder="Search distributions, e.g. KDE, Xfce or Ubuntu"
            spellCheck={false} value={query} onChange={e => setQuery(e.target.value)}
            onKeyDown={e => { if (e.key === "Enter") next(); }} />
          <div className="heading">
            {words.length === 0 ? "Recommended" : shown.length + (shown.length === 1 ? " result" : " results")}
          </div>
          <div className="box list" role="listbox" aria-label="Distributions">
            {shown.length ? shown.map(d => (
              <div key={d.id} role="option" aria-selected={d.id === chosen}
                className={"option" + (d.id === chosen ? " selected" : "")} onClick={() => setChosen(d.id)}>
                <div className="text">
                  <div className="title">{d.name}</div>
                  <div className="about">{d.about}</div>
                </div>
                {d.home && <span className="link" onClick={e => { e.stopPropagation(); openPage(d.home!); }}>Website ↗</span>}
              </div>
            )) : (
              <div className="empty">
                No distribution matches "{query.trim()}".<br />Try another name, or use your own installer below.
              </div>
            )}
          </div>
          <div className="box own">
            <div role="option" aria-selected={chosen === OWN} className={"option" + (chosen === OWN ? " selected" : "")}
              onClick={() => setChosen(OWN)}>
              <div className="text">
                <div className="title">Use Your Own Installer</div>
                <div className="about">An existing virtual disk to import, an .iso file on this PC, or a URL link to one</div>
              </div>
            </div>
          </div>
          <div className="nav">
            {onBack ? <button className="btn" data-back onClick={onBack}>Back</button> : <span />}
            <button className="btn primary" onClick={next}>Next</button>
          </div>
        </div>
      </main>
    </Fragment>
  );
}

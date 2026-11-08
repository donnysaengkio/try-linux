"""Choose a Linux OS: the recommended distributions, search across the whole list, and Use Your Own Installer.

The results are a Gtk.ListView, which only builds rows for what's on screen, so typing stays quick with
hundreds of matches."""
import threading

from gi.repository import Adw, Gio, GObject, Gtk, Pango

from .. import catalog, paths
from . import widgets as W


class Item(GObject.Object):
    def __init__(self, d, order):
        super().__init__()
        self.d = d
        self.order = order


class ChoosePage:
    def __init__(self, app, root=False):
        self.app = app
        self.own_selected = False
        arm = paths.arch() == "aarch64"
        self.store = Gio.ListStore(item_type=Item)
        self.fill()
        self.query = ""
        self.filter = Gtk.CustomFilter.new(self.match)
        self.sorter = Gtk.CustomSorter.new(self.compare)
        model = Gtk.SortListModel(model=Gtk.FilterListModel(model=self.store, filter=self.filter), sorter=self.sorter)
        # No autoselect: with it, unselecting (when Use Your Own Installer is chosen) picked a row straight back.
        self.sel = Gtk.SingleSelection(model=model, autoselect=False, can_unselect=True)
        if self.sel.get_n_items():
            self.sel.set_selected(0)
        self.sel.connect("notify::selected", self.on_selected)

        factory = Gtk.SignalListItemFactory()
        factory.connect("setup", self.setup_row)
        factory.connect("bind", self.bind_row)
        self.view = Gtk.ListView(model=self.sel, factory=factory, single_click_activate=False)
        self.view.add_css_class("navigation-sidebar")
        self.view.connect("activate", lambda *_: self.next())
        results = Gtk.ScrolledWindow(child=self.view, min_content_height=330, max_content_height=330,
                                     hscrollbar_policy=Gtk.PolicyType.NEVER, propagate_natural_height=False)
        results.set_size_request(-1, 330)
        frame = Gtk.Frame(child=results)
        frame.add_css_class("results")

        self.search = Gtk.SearchEntry(placeholder_text="Search distributions, e.g. KDE, Xfce or Ubuntu")
        self.search.connect("search-changed", lambda *_: self.on_search())
        self.heading = Gtk.Label(label="Recommended", xalign=0)
        self.heading.add_css_class("heading")
        self.hint = Gtk.Label(wrap=True, visible=False, justify=Gtk.Justification.CENTER)
        self.hint.add_css_class("dim-label")

        # Use Your Own Installer: a row like the results' (no box of its own), in a frame like theirs.
        self.own = Gtk.ListBox(selection_mode=Gtk.SelectionMode.SINGLE)
        self.own.add_css_class("navigation-sidebar")
        own_row = Adw.ActionRow(title="Use Your Own Installer", activatable=True,
                                subtitle="An existing virtual disk to import, an .iso file on this PC, or a URL link to one")
        self.own.append(own_row)
        self.own.connect("row-selected", self.on_own)
        self.own.connect("row-activated", lambda *_: self.next())
        own_frame = Gtk.Frame(child=self.own)
        own_frame.add_css_class("results")

        sub = "Try Linux downloads the official %sinstaller and checks it before use." % ("ARM64 " if arm else "")
        scroller, _ = W.column(W.logo(7), W.heading("Choose a Linux OS", sub), self.search, self.heading,
                               frame, self.hint, own_frame, spacing=12)
        next_ = W.button("Next", self.next, primary=True, pill=True)
        self.page = W.page("Choose a Linux OS", scroller, tag="choose", can_pop=not root,
                           bottom=W.bottom_bar(None, next_))
        self.page.chooser = self   # for the self-test
        # The lists are checked every time this page opens, the first one too: right after installing there's
        # no saved copy yet, so until they arrive search only has the recommended distributions.
        if not app.selftest:
            threading.Thread(target=self.refresh_catalog, daemon=True).start()

    # ---- the list

    def fill(self):
        self.store.remove_all()
        for i, d in enumerate(catalog.all_distros()):
            self.store.append(Item(d, i))

    def refresh_catalog(self, source=None):
        """Checks the lists (in the background) and shows what changed, keeping what's typed in search."""
        catalog.refresh(source)
        W.ui(self.update_list)

    def update_list(self):
        have = [self.store.get_item(i).d.id for i in range(self.store.get_n_items())]
        if have != [d.id for d in catalog.all_distros()]:
            self.fill()
            self.on_search()

    def match(self, item, *_):
        words = self.query.lower().split()
        if not words:
            return item.d.builtin
        hay = ("%s %s %s" % (item.d.name, item.d.about, item.d.id)).lower()
        return all(w in hay for w in words)

    def compare(self, a, b, *_):
        q = self.query.lower()
        sa, sb = a.d.name.lower().startswith(q) if q else False, b.d.name.lower().startswith(q) if q else False
        if sa != sb:
            return -1 if sa else 1
        return (a.order > b.order) - (a.order < b.order)

    def on_search(self):
        self.query = self.search.get_text().strip()
        self.filter.changed(Gtk.FilterChange.DIFFERENT)
        self.sorter.changed(Gtk.SorterChange.DIFFERENT)
        n = self.sel.get_n_items()
        self.heading.set_label("Recommended" if not self.query else "%d result%s" % (n, "" if n == 1 else "s"))
        self.hint.set_visible(n == 0)
        self.hint.set_label('No distribution matches "%s".\nTry another name, or use your own installer below.' % self.query)
        if n and not self.own_selected:
            self.sel.set_selected(0)

    def setup_row(self, _f, li):
        box = Gtk.Box(spacing=12, margin_top=8, margin_bottom=8, margin_start=10, margin_end=6)
        text = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=3, hexpand=True)
        title = Gtk.Label(xalign=0, ellipsize=Pango.EllipsizeMode.END)
        title.add_css_class("heading")
        sub = Gtk.Label(xalign=0, wrap=True, lines=2, ellipsize=Pango.EllipsizeMode.END, max_width_chars=80)
        sub.add_css_class("dim-label")
        sub.add_css_class("caption")
        text.append(title)
        text.append(sub)
        # The home page in its own column, centred on the row, past a divider line.
        divider = Gtk.Separator(orientation=Gtk.Orientation.VERTICAL, margin_top=4, margin_bottom=4)
        link = Gtk.LinkButton(label="Website ↗", valign=Gtk.Align.CENTER)
        box.append(text)
        box.append(divider)
        box.append(link)
        box.parts = (title, sub, link, divider)
        li.set_child(box)

    def bind_row(self, _f, li):
        d = li.get_item().d
        title, sub, link, divider = li.get_child().parts
        title.set_label(d.name)
        sub.set_label(d.about)
        link.set_visible(bool(d.home))
        divider.set_visible(bool(d.home))
        if d.home:
            link.set_uri(d.home)
            link.set_tooltip_text(d.home)

    # ---- one selection across the list and Use Your Own Installer

    def on_selected(self, *_):
        if self.sel.get_selected_item() is not None and self.own_selected:
            self.own_selected = False
            self.own.unselect_all()

    def on_own(self, _l, row):
        if row is not None:
            self.own_selected = True
            self.sel.set_selected(Gtk.INVALID_LIST_POSITION)   # no search result selected

    def chosen(self):
        if self.own_selected:
            return "own"
        item = self.sel.get_selected_item()
        return item.d if item else None

    def next(self):
        from .create import InstallPlan, OwnInstallerPage, GeneralPage
        c = self.chosen()
        if c is None:
            return
        if c == "own":
            self.app.nav.push(OwnInstallerPage(self.app).page)
            return
        if c.website_only:
            # A distribution Try Linux can't download itself: its website, then Use Your Own Installer.
            arch = " (ARM64)" if paths.arch() == "aarch64" else ""

            def go():
                W.open_uri(self.app.window, c.home)
                self.app.nav.push(OwnInstallerPage(self.app).page)

            W.ask(self.app.window, "Download %s from its website?" % c.name,
                  "Try Linux can't fetch the %s%s ISO file itself. Open its website to download the ISO, then choose it in "
                  "Use Your Own Installer (An ISO File On This PC) to install it." % (c.name, arch),
                  "Open Website", on_yes=go)
            return
        self.app.nav.push(GeneralPage(self.app, InstallPlan(distro=c, label=c.name)).page)

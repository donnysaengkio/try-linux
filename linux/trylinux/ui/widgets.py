"""The look shared by every screen: Try Linux's colours on top of libadwaita, the pixel logo, and helpers
for dialogs, files and links."""
from gi.repository import Adw, Gdk, Gio, GLib, Gtk

# The Windows and Mac editions' palettes: Dark is Firefox's dark greys with lavender, Light is white with
# Proton's purple.
PALETTES = {
    "dark": dict(bg="#111113", card="#252429", box="#1C1B22", border="#39383D", text="#FBFBFE", muted="#BFBFC9",
                 accent="#B89CFE", accent_fg="#15141A", highlight="#C7B1FF", highlight2="#B89CFE", accent2="#5E4898",
                 success="#7ED68A", danger="#F26D6D", warning="#F9D949", selected="#352E48"),
    "light": dict(bg="#FCF9FE", card="#FFFFFF", box="#F4F1F9", border="#DEDBE8", text="#0C0C14", muted="#6B6880",
                  accent="#6D4AFF", accent_fg="#FFFFFF", highlight="#5B2BD1", highlight2="#3A1F99", accent2="#3A1F99",
                  success="#1C8A4E", danger="#D6332E", warning="#B38600", selected="#EEE8FF"),
}

_provider = None
current = "dark"


def palette():
    return PALETTES[current]


def apply_theme(name, follow_system):
    """Sets light or dark (or follows the system) and Try Linux's colours for it."""
    global _provider, current
    sm = Adw.StyleManager.get_default()
    sm.set_color_scheme(Adw.ColorScheme.DEFAULT if follow_system else
                        Adw.ColorScheme.FORCE_LIGHT if name == "light" else Adw.ColorScheme.FORCE_DARK)
    current = ("dark" if sm.get_dark() else "light") if follow_system else name
    p = PALETTES[current]
    named = {
        "accent_bg_color": p["accent"], "accent_fg_color": p["accent_fg"], "accent_color": p["highlight"],
        "window_bg_color": p["bg"], "window_fg_color": p["text"], "view_bg_color": p["bg"], "view_fg_color": p["text"],
        "headerbar_bg_color": p["bg"], "headerbar_fg_color": p["text"], "card_bg_color": p["card"], "card_fg_color": p["text"],
        "popover_bg_color": p["card"], "popover_fg_color": p["text"], "dialog_bg_color": p["box"], "dialog_fg_color": p["text"],
        "destructive_bg_color": p["danger"], "destructive_color": p["danger"], "success_color": p["success"],
        "warning_color": p["warning"], "error_color": p["danger"],
    }
    css = "".join("@define-color %s %s;\n" % kv for kv in named.items())
    if Adw.get_minor_version() >= 6:
        css += ":root {" + "".join("--%s: %s;" % (k.replace("_", "-"), v) for k, v in named.items()) + "}\n"
    css += """
.logo-title { font-size: 20pt; font-weight: 800; }
.results { background: %(box)s; border-radius: 12px; border: 1px solid %(border)s; }
.results row { border-radius: 8px; margin: 2px 6px; }
.results row:selected { background: alpha(%(accent)s, 0.22); }
.status-on { color: %(success)s; }
.status-off { color: %(muted)s; }
.stat { font-feature-settings: "tnum"; }
.boxed-list row:selected { background: alpha(%(accent)s, 0.22); }
""" % p
    display = Gdk.Display.get_default()
    if _provider is None:
        _provider = Gtk.CssProvider()
        Gtk.StyleContext.add_provider_for_display(display, _provider, Gtk.STYLE_PROVIDER_PRIORITY_APPLICATION)
    if hasattr(_provider, "load_from_string"):
        _provider.load_from_string(css)
    else:
        _provider.load_from_data(css, -1)


def _rgb(hexs):
    hexs = hexs.lstrip("#")
    return tuple(int(hexs[i:i + 2], 16) / 255 for i in (0, 2, 4))


GLYPHS = {
    "T": ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
    "R": ["####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"],
    "Y": ["#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."],
    "L": ["#....", "#....", "#....", "#....", "#....", "#....", "#####"],
    "I": ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####"],
    "N": ["#...#", "##..#", "##..#", "#.#.#", "#..##", "#..##", "#...#"],
    "U": ["#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
    "X": ["#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"],
}


class Logo(Gtk.Widget):
    """The pixel logo: the letters in a gradient of the highlight colours, with a drop shadow. Drawn as
    rectangles in GTK's own scene graph (no cairo bindings needed)."""
    __gtype_name__ = "TryLinuxLogo"

    def __init__(self, px=7, word="TRY LINUX"):
        super().__init__(halign=Gtk.Align.CENTER)
        self.px, self.word = px, word
        self.cols = len(word) * 6 - 1
        self.shadow = max(1, px // 3)

    def do_measure(self, orientation, for_size):
        n = self.cols * self.px + self.shadow if orientation == Gtk.Orientation.HORIZONTAL else 7 * self.px + self.shadow
        return n, n, -1, -1

    def do_snapshot(self, snap):
        from gi.repository import Graphene
        p, px = palette(), self.px
        x0 = (self.get_width() - self.cols * px - self.shadow) // 2
        y0 = (self.get_height() - 7 * px - self.shadow) // 2

        def rgba(rgb):
            c = Gdk.RGBA()
            c.red, c.green, c.blue, c.alpha = rgb[0], rgb[1], rgb[2], 1.0
            return c

        def word_at(x, y, color_for_row):
            for i, ch in enumerate(self.word):
                g = GLYPHS.get(ch)
                if not g:
                    continue
                for r, row in enumerate(g):
                    color = rgba(color_for_row(r))
                    for c, bit in enumerate(row):
                        if bit == "#":
                            snap.append_color(color, Graphene.Rect().init(x + (i * 6 + c) * px, y + r * px, px, px))

        word_at(x0 + self.shadow, y0 + self.shadow, lambda r: _rgb(p["accent2"]))
        a, b = _rgb(p["highlight"]), _rgb(p["highlight2"])
        word_at(x0, y0, lambda r: tuple(a[k] + (b[k] - a[k]) * r / 6 for k in range(3)))


def logo(px=7, word="TRY LINUX"):
    return Logo(px, word)


def heading(text, sub=None):
    box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=4)
    t = Gtk.Label(label=text)
    t.add_css_class("title-2")
    box.append(t)
    if sub:
        s = Gtk.Label(label=sub, wrap=True, justify=Gtk.Justification.CENTER)
        s.add_css_class("dim-label")
        box.append(s)
    return box


def column(*children, spacing=18, width=760):
    """A centred column, scrolling when the window is short."""
    box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=spacing, margin_top=18, margin_bottom=18,
                  margin_start=16, margin_end=16)
    for c in children:
        if c is not None:
            box.append(c)
    clamp = Adw.Clamp(maximum_size=width, child=box)
    return Gtk.ScrolledWindow(child=clamp, vexpand=True, hscrollbar_policy=Gtk.PolicyType.NEVER), box


def button(label, action=None, primary=False, destructive=False, pill=False):
    # Centred, at its own height: beside a row's text a button would otherwise stretch to the row's height.
    b = Gtk.Button(label=label, valign=Gtk.Align.CENTER)
    if primary:
        b.add_css_class("suggested-action")
    if destructive:
        b.add_css_class("destructive-action")
    if pill:
        b.add_css_class("pill")
    if action:
        b.connect("clicked", lambda *_: action())
    return b


def bottom_bar(left=None, right=None):
    """Back on the left and the next step on the right, under the page."""
    bar = Gtk.Box(spacing=12, margin_top=12, margin_bottom=16, margin_start=16, margin_end=16)
    if left:
        bar.append(left)
    spacer = Gtk.Box(hexpand=True)
    bar.append(spacer)
    if right:
        bar.append(right)
    return Adw.Clamp(maximum_size=760, child=bar)


def page(title, content, tag=None, header_end=None, bottom=None, can_pop=True, title_widget=None, header_start=None):
    """A screen: a header bar (with Back when there's a screen to go back to) over `content`."""
    tv = Adw.ToolbarView()
    hb = Adw.HeaderBar()
    if title_widget:
        hb.set_title_widget(title_widget)
    for w in header_start or []:
        hb.pack_start(w)
    for w in header_end or []:
        hb.pack_end(w)
    tv.add_top_bar(hb)
    tv.set_content(content)
    if bottom:
        tv.add_bottom_bar(bottom)
    p = Adw.NavigationPage(title=title, child=tv)
    if tag:
        p.set_tag(tag)
    p.set_can_pop(can_pop)
    return p


def ask(parent, heading_, body, yes, no="Cancel", destructive=False, on_yes=None, on_no=None, extra=None):
    """A question with two answers (three with `extra` = (id, label, callback))."""
    d = Adw.AlertDialog(heading=heading_, body=body or "")
    d.add_response("no", no)
    if extra:
        d.add_response(extra[0], extra[1])
    d.add_response("yes", yes)
    d.set_response_appearance("yes", Adw.ResponseAppearance.DESTRUCTIVE if destructive else Adw.ResponseAppearance.SUGGESTED)
    d.set_default_response("yes")
    d.set_close_response("no")

    def done(_d, r):
        if r == "yes" and on_yes:
            on_yes()
        elif r == "no" and on_no:
            on_no()
        elif extra and r == extra[0]:
            extra[2]()

    d.connect("response", done)
    d.present(parent)
    return d


def tell(parent, heading_, body, ok="OK", on_ok=None):
    d = Adw.AlertDialog(heading=heading_, body=body or "")
    d.add_response("ok", ok)
    d.set_default_response("ok")
    d.set_close_response("ok")
    if on_ok:
        d.connect("response", lambda *_: on_ok())
    d.present(parent)
    return d


def _filters(name, patterns):
    store = Gio.ListStore(item_type=Gtk.FileFilter)
    f = Gtk.FileFilter(name=name)
    for p in patterns:
        f.add_pattern(p)
        f.add_pattern(p.upper())
    store.append(f)
    a = Gtk.FileFilter(name="All files")
    a.add_pattern("*")
    store.append(a)
    return store


def open_file(parent, title, on_file, name=None, patterns=None):
    d = Gtk.FileDialog(title=title, modal=True)
    if patterns:
        d.set_filters(_filters(name or "Files", patterns))

    def done(dlg, res):
        try:
            f = dlg.open_finish(res)
        except GLib.Error:
            return
        if f and f.get_path():
            on_file(f.get_path())

    d.open(parent, None, done)


def open_folder(parent, title, on_folder):
    d = Gtk.FileDialog(title=title, modal=True)

    def done(dlg, res):
        try:
            f = dlg.select_folder_finish(res)
        except GLib.Error:
            return
        if f and f.get_path():
            on_folder(f.get_path())

    d.select_folder(parent, None, done)


def open_uri(parent, uri):
    try:
        Gtk.UriLauncher(uri=uri).launch(parent, None, None, None)
    except (AttributeError, GLib.Error):
        Gio.AppInfo.launch_default_for_uri(uri, None)


def link(uri, text="Website ↗"):
    b = Gtk.LinkButton(uri=uri, label=text, valign=Gtk.Align.CENTER)
    return b


def ui(fn, *a):
    """Runs `fn` on the main loop (from a background thread)."""
    GLib.idle_add(lambda: (fn(*a), False)[1])

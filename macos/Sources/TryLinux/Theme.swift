// Colors, fonts and the flat controls shared by every page (the same look as Try Linux for Windows).
import AppKit
import SwiftUI

// Colors follow Proton's palette (proton.me/blog/new-visual-universe) on a Proton Pass-style dark UI.
// "Depth": a deep background with surfaces shaded from top to bottom, and a purple glow on what's
// selected or open, the same look as Try Linux for Windows.
enum Theme {
    static let bg = rgb(12, 12, 20)              // #0C0C14
    static let card = rgb(26, 23, 38)            // fields and flat fills
    static let cardHover = rgb(36, 32, 54)
    static let cardSelected = rgb(42, 35, 66)    // #2A2342
    static let cardTop = rgb(29, 26, 43)         // #1D1A2B, shaded surfaces: top
    static let cardBottom = rgb(22, 20, 31)      // #16141F, and bottom
    static let hoverTop = rgb(37, 33, 55)
    static let hoverBottom = rgb(27, 24, 40)
    static let selectedTop = rgb(42, 35, 66)     // #2A2342
    static let selectedBottom = rgb(28, 24, 48)  // #1C1830
    static let box = rgb(18, 17, 28)             // containers that group rows
    static let border = rgb(42, 37, 64)          // #2A2540
    static let text = rgb(245, 244, 242)         // #F5F4F2
    static let muted = rgb(155, 151, 179)        // #9B97B3
    // Accent is the main call to action only (Start, Next, Save). Highlight is the logo and active
    // form elements: selection, tabs, checkboxes, progress, section titles.
    static let accent = rgb(109, 74, 255)        // #6D4AFF
    static let accentHover = rgb(138, 110, 255)  // #8A6EFF
    static let accent2 = rgb(56, 37, 128)        // #382580
    static let highlight = rgb(196, 181, 255)    // #C4B5FF
    static let highlight2 = rgb(138, 110, 255)   // #8A6EFF
    static let success = rgb(102, 222, 177)      // #66DEB1, a running system
    static let warning = rgb(249, 217, 73)       // #F9D949
    static let danger = rgb(235, 80, 141)        // #EB508D

    static func rgb(_ r: Double, _ g: Double, _ b: Double) -> Color {
        Color(red: r / 255, green: g / 255, blue: b / 255)
    }

    // Blend from highlight (top of the logo) to highlight2 (bottom).
    static func logoRow(_ t: Double) -> Color {
        rgb(167 + (138 - 167) * t, 146 + (110 - 146) * t, 255)
    }

    // The serif display font (New York) is for headings: page, section and dialog titles. Everything
    // else (system names, descriptions, labels, buttons, tabs) is the system's sans-serif font.
    static func display(_ size: CGFloat, _ weight: Font.Weight = .semibold) -> Font {
        .system(size: size, weight: weight, design: .serif)
    }

    static func mono(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        .system(size: size, weight: weight, design: .monospaced)
    }

    static func sans(_ size: CGFloat, _ weight: Font.Weight = .regular) -> Font {
        .system(size: size, weight: weight)
    }
}

// A bordered container that groups related rows, a shade darker than the rows inside it.
struct BoxStyle: ViewModifier {
    func body(content: Content) -> some View {
        content.padding(6).background(Theme.box).overlay(Rectangle().stroke(Theme.border, lineWidth: 1))
    }
}

// A shaded surface: lighter at the top than the bottom, with a border. Selected (or open) surfaces get
// the accent border and a soft purple glow just inside their edge.
struct Surface: ViewModifier {
    var selected = false
    var hover = false

    func body(content: Content) -> some View {
        let colors = selected ? [Theme.selectedTop, Theme.selectedBottom]
            : hover ? [Theme.hoverTop, Theme.hoverBottom] : [Theme.cardTop, Theme.cardBottom]
        return content
            .background(LinearGradient(colors: colors, startPoint: .top, endPoint: .bottom))
            .overlay(Rectangle().stroke(Theme.accent.opacity(selected ? 0.55 : 0), lineWidth: 8).blur(radius: 6).clipped())
            .overlay(Rectangle().stroke(selected ? Theme.accent : Theme.border, lineWidth: selected ? 1.5 : 1))
    }
}

extension View {
    func surface(selected: Bool = false, hover: Bool = false) -> some View { modifier(Surface(selected: selected, hover: hover)) }
}

struct PixelLogo: View {
    var word = "TRY LINUX"
    var px: CGFloat = 7

    static let glyphs: [Character: [String]] = [
        "T": ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "..#.."],
        "R": ["####.", "#...#", "#...#", "####.", "#.#..", "#..#.", "#...#"],
        "Y": ["#...#", "#...#", ".#.#.", "..#..", "..#..", "..#..", "..#.."],
        "L": ["#....", "#....", "#....", "#....", "#....", "#....", "#####"],
        "I": ["#####", "..#..", "..#..", "..#..", "..#..", "..#..", "#####"],
        "N": ["#...#", "##..#", "##..#", "#.#.#", "#..##", "#..##", "#...#"],
        "U": ["#...#", "#...#", "#...#", "#...#", "#...#", "#...#", ".###."],
        "X": ["#...#", "#...#", ".#.#.", "..#..", ".#.#.", "#...#", "#...#"],
    ]

    var body: some View {
        Canvas { ctx, size in
            let cols = CGFloat(word.count * 6 - 1)
            let shadow = max(1, (px / 3).rounded(.down))
            // Center the letters plus their drop shadow.
            let x0 = ((size.width - cols * px - shadow) / 2).rounded(.down)
            let y0 = ((size.height - 7 * px - shadow) / 2).rounded(.down)
            draw(ctx, x0 + shadow, y0 + shadow) { _ in Theme.accent2 }
            draw(ctx, x0, y0) { row in Theme.logoRow(row / 6) }
        }
        .frame(maxWidth: .infinity)
        .frame(height: 56)
    }

    private func draw(_ ctx: GraphicsContext, _ x0: CGFloat, _ y0: CGFloat, _ color: (Double) -> Color) {
        for (i, ch) in word.enumerated() {
            guard let glyph = Self.glyphs[ch] else { continue }
            for (r, row) in glyph.enumerated() {
                for (c, cell) in row.enumerated() where cell == "#" {
                    let rect = CGRect(x: x0 + CGFloat(i * 6 + c) * px, y: y0 + CGFloat(r) * px, width: px, height: px)
                    ctx.fill(Path(rect), with: .color(color(Double(r))))
                }
            }
        }
    }
}

struct FlatButton: ButtonStyle {
    var primary = false
    var danger = false
    var accent = false      // highlight-colored text and outline, for secondary offers

    func makeBody(configuration: Configuration) -> some View {
        FlatButtonBody(configuration: configuration, primary: primary, danger: danger, accent: accent)
    }
}

private struct FlatButtonBody: View {
    let configuration: ButtonStyle.Configuration
    let primary: Bool
    let danger: Bool
    let accent: Bool
    @Environment(\.isEnabled) private var enabled
    @State private var hover = false

    var body: some View {
        // The main button is a purple gradient, lighter on hover and darker while pressed; the others are shaded surfaces.
        let fill: [Color] = primary
            ? (configuration.isPressed ? [Theme.accent, Theme.accent2] : (hover ? [Theme.highlight, Theme.accentHover] : [Theme.accentHover, Theme.accent]))
            : (configuration.isPressed || hover ? [Theme.hoverTop, Theme.hoverBottom] : [Theme.cardTop, Theme.cardBottom])
        configuration.label
            .font(Theme.sans(13, .semibold))
            .foregroundColor(primary ? .white : (danger ? Theme.danger : (accent ? Theme.highlight : Theme.text)))
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(LinearGradient(colors: fill, startPoint: .top, endPoint: .bottom))
            .overlay(Rectangle().stroke(primary ? Theme.accent : accent ? Theme.highlight : (danger ? Theme.danger : Theme.border), lineWidth: 1))
            .contentShape(Rectangle())
            .opacity(enabled ? 1 : 0.45)
            .onHover { hover = $0 }
    }
}

struct OptionRow: View {
    let title: String
    let subtitle: String
    let selected: Bool
    var twoLine = false
    var status = ""                  // shown on the right, centred on the row
    var statusColor = Theme.muted
    // A system's values (CPU, memory, disk), shown as columns on the right with the status in its own
    // column before them. The column headers are above the list (see StatHeaders).
    var stats: [String] = []
    @State private var hover = false

    static let statusW: CGFloat = 76, statW: CGFloat = 86, gap: CGFloat = 10, trailing: CGFloat = 18

    var body: some View {
        HStack(spacing: 0) {
            // The glow marks the selected row, so there's no stripe on its left.
            VStack(alignment: .leading, spacing: 7) {
                Text(title).font(Theme.sans(14, .semibold)).foregroundColor(Theme.text).lineLimit(1)
                Text(subtitle).font(Theme.sans(11.5)).foregroundColor(Theme.muted)
                    .lineLimit(twoLine ? 2 : 1)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.leading, 18)
            .padding(.trailing, 18)
            .padding(.vertical, 9)
            Spacer(minLength: 0)
            if !stats.isEmpty {
                Text(status).font(Theme.sans(11.5)).foregroundColor(statusColor).lineLimit(1)
                    .fixedSize().frame(minWidth: Self.statusW, alignment: .leading)
                Rectangle().fill(Theme.border).frame(width: 1, height: 30)
                Spacer().frame(width: Self.gap - 1)
                ForEach(stats.indices, id: \.self) { i in
                    Text(stats[i]).font(Theme.sans(13, .semibold)).foregroundColor(Theme.text).lineLimit(1)
                        .frame(width: Self.statW, alignment: .leading)
                }
                Spacer().frame(width: Self.trailing)
            } else if !status.isEmpty {
                Text(status).font(Theme.sans(11.5)).foregroundColor(statusColor).lineLimit(1)
                    .fixedSize().padding(.trailing, 18)
            }
        }
        .frame(maxWidth: .infinity, minHeight: twoLine ? 70 : 54, alignment: .leading)
        .fixedSize(horizontal: false, vertical: true)   // keep to its content; don't stretch into free space
        .surface(selected: selected, hover: hover)
        .contentShape(Rectangle())
        .onHover { hover = $0 }
    }
}

struct Segmented: View {
    let options: [String]
    @Binding var selection: Int

    var body: some View {
        HStack(spacing: 0) {
            ForEach(options.indices, id: \.self) { i in
                Text(options[i])
                    .font(Theme.sans(13, .semibold))
                    .foregroundColor(i == selection ? Theme.bg : Theme.text)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .background(LinearGradient(colors: i == selection ? [Theme.highlight, Theme.accentHover] : [Theme.cardTop, Theme.cardBottom],
                                               startPoint: .top, endPoint: .bottom))
                    .contentShape(Rectangle())
                    .onTapGesture { selection = i }
            }
        }
        .overlay(Rectangle().stroke(Theme.border, lineWidth: 1))
    }
}

struct TabStrip: View {
    let tabs: [String]
    @Binding var selection: Int

    var body: some View {
        HStack(spacing: 6) {
            ForEach(tabs.indices, id: \.self) { i in
                Text(tabs[i])
                    .font(Theme.sans(13, .semibold))
                    .foregroundColor(i == selection ? Theme.highlight : Theme.text)
                    .frame(maxWidth: .infinity, maxHeight: .infinity)
                    .surface(selected: i == selection)
                    .contentShape(Rectangle())
                    .onTapGesture { selection = i }
            }
        }
        .frame(height: 34)
    }
}

struct CheckRow: View {
    let title: String
    @Binding var on: Bool
    @Environment(\.isEnabled) private var enabled

    var body: some View {
        Button { on.toggle() } label: {
            HStack(spacing: 8) {
                ZStack {
                    RoundedRectangle(cornerRadius: 3).fill(on ? Theme.highlight : Theme.card)
                    RoundedRectangle(cornerRadius: 3).stroke(Theme.border, lineWidth: 1)
                    if on {
                        Image(systemName: "checkmark").font(.system(size: 10, weight: .bold)).foregroundColor(Theme.bg)
                    }
                }
                .frame(width: 16, height: 16)
                Text(title).font(Theme.sans(13)).foregroundColor(Theme.text)
                Spacer(minLength: 0)
            }
            .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .opacity(enabled ? 1 : 0.45)
        .frame(height: 24)
    }
}

struct NumberField: View {
    @Binding var value: Int
    let range: ClosedRange<Int>
    var step = 1
    @Environment(\.isEnabled) private var enabled

    var body: some View {
        HStack(spacing: 0) {
            Text("\(value)")
                .font(Theme.sans(13))
                .foregroundColor(enabled ? Theme.text : Theme.muted)
                .frame(width: 58, alignment: .leading)
                .padding(.leading, 8)
            stepButton("minus") { value = max(range.lowerBound, value - step) }
            stepButton("plus") { value = min(range.upperBound, value + step) }
        }
        .frame(height: 26)
        .background(Theme.card)
        .overlay(Rectangle().stroke(Theme.border, lineWidth: 1))
        .opacity(enabled ? 1 : 0.6)
        .fixedSize()
    }

    private func stepButton(_ symbol: String, _ action: @escaping () -> Void) -> some View {
        Button(action: action) {
            Image(systemName: symbol)
                .font(.system(size: 9, weight: .bold))
                .foregroundColor(Theme.text)
                .frame(width: 24, height: 26)
                .contentShape(Rectangle())
        }
        .buttonStyle(.plain)
        .overlay(Rectangle().frame(width: 1).foregroundColor(Theme.border), alignment: .leading)
    }
}

struct TextInput: View {
    @Binding var text: String
    var placeholder = ""

    var body: some View {
        TextField(placeholder, text: $text)
            .textFieldStyle(.plain)
            .font(Theme.sans(13))
            .foregroundColor(Theme.text)
            .padding(.horizontal, 8)
            .frame(height: 26)
            .background(Theme.card)
            .overlay(Rectangle().stroke(Theme.border, lineWidth: 1))
    }
}

struct Bar: View {
    // 0...1, or nil for a moving (indeterminate) bar.
    let value: Double?

    var body: some View {
        GeometryReader { geo in
            ZStack(alignment: .leading) {
                Rectangle().fill(Theme.card)
                if let v = value {
                    Rectangle().fill(Theme.highlight).frame(width: geo.size.width * min(1, max(0, v)))
                } else {
                    TimelineView(.animation) { tl in
                        let t = tl.date.timeIntervalSinceReferenceDate.truncatingRemainder(dividingBy: 1.6) / 1.6
                        Rectangle().fill(Theme.highlight)
                            .frame(width: geo.size.width * 0.3)
                            .offset(x: geo.size.width * (t * 1.3 - 0.3))
                    }
                }
            }
            .clipped()
        }
        .frame(height: 10)
    }
}

// A full-window page with the logo and a centered fixed-width column, like a setup screen.
struct Page<Content: View>: View {
    var logo = true
    @ViewBuilder let content: () -> Content

    var body: some View {
        VStack(spacing: 0) {
            Spacer(minLength: 12)
            VStack(alignment: .leading, spacing: 0) {
                if logo { PixelLogo() }
                content()
            }
            .frame(width: 640)
            Spacer(minLength: 12)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
        .background(Theme.bg)
    }
}

struct Heading: View {
    let text: String
    var centered = false

    var body: some View {
        Text(text)
            .font(Theme.display(22))
            .foregroundColor(Theme.text)
            .frame(maxWidth: .infinity, alignment: centered ? .center : .leading)
    }
}

struct Note: View {
    let text: String
    var color = Theme.muted

    var body: some View {
        Text(text)
            .font(Theme.sans(11.5))
            .foregroundColor(color)
            .fixedSize(horizontal: false, vertical: true)
            .frame(maxWidth: .infinity, alignment: .leading)
    }
}

// A label column on the left with controls to its right.
struct FormRow<Content: View>: View {
    let label: String
    @ViewBuilder let content: () -> Content

    var body: some View {
        HStack(alignment: .center, spacing: 10) {
            Text(label).font(Theme.sans(13)).foregroundColor(Theme.text).frame(width: 180, alignment: .leading)
            content()
            Spacer(minLength: 0)
        }
    }
}

struct SectionTitle: View {
    let text: String

    var body: some View {
        // Twice the space above each section as before.
        Text(text).font(Theme.display(15)).foregroundColor(Theme.highlight).padding(.top, 28).padding(.bottom, 4)
    }
}

// A smaller heading inside an open section ("Capacity", "Disk information").
struct SubHeading: View {
    let text: String

    var body: some View {
        Text(text).font(Theme.sans(13, .semibold)).foregroundColor(Theme.text).padding(.top, 8)
    }
}

// A settings section: a bar that opens and closes it. A closed bar shows a summary of its settings on
// the right. Every word of its title starts with a capital letter ("Camera And USB").
struct SectionBar<Content: View>: View {
    let title: String
    var summary = ""
    @Binding var open: Bool
    @ViewBuilder let content: () -> Content
    @State private var hover = false

    static func titleCase(_ s: String) -> String {
        s.split(separator: " ", omittingEmptySubsequences: false)
            .map { w in w.isEmpty ? "" : w.prefix(1).uppercased() + w.dropFirst() }
            .joined(separator: " ")
    }

    var body: some View {
        VStack(alignment: .leading, spacing: 10) {
            Button { open.toggle() } label: {
                HStack(spacing: 0) {
                    Text(Self.titleCase(title)).font(Theme.display(15)).foregroundColor(open ? Theme.highlight : Theme.text)
                        .lineLimit(1).fixedSize()
                    Spacer(minLength: 24)
                    if !open {
                        Text(summary).font(Theme.sans(11.5)).foregroundColor(Theme.muted).lineLimit(1).truncationMode(.tail)
                    }
                    Image(systemName: open ? "chevron.down" : "chevron.right")
                        .font(.system(size: 11, weight: .semibold)).foregroundColor(Theme.muted)
                        .frame(width: 20).padding(.leading, 10)
                }
                .padding(.horizontal, 14)
                .frame(height: 42)
                .surface(selected: open, hover: hover)
                .contentShape(Rectangle())
            }
            .buttonStyle(.plain)
            .onHover { hover = $0 }
            if open {
                VStack(alignment: .leading, spacing: 10) { content() }
                    .padding(.horizontal, 4)
                    .padding(.bottom, 8)
            }
        }
    }
}

// A line of text with its button on the right, in from the bar's edge. Every such button on a tab
// gets the same `width`, so they look equal.
struct ActionRow: View {
    let text: String
    let button: String
    var width: CGFloat = 130
    var danger = false
    let action: () -> Void

    var body: some View {
        HStack(spacing: 10) {
            Text(text).font(Theme.sans(13)).foregroundColor(Theme.text).lineLimit(1)
            Spacer(minLength: 0)
            Button(button, action: action).buttonStyle(FlatButton(danger: danger)).frame(width: width, height: 30)
        }
        .padding(.trailing, 12)
    }

    // A button width that fits the longest of `titles`, with a little room each side.
    static func width(_ titles: [String]) -> CGFloat {
        let font = NSFont.systemFont(ofSize: 13, weight: .semibold)
        let widest = titles.map { ($0 as NSString).size(withAttributes: [.font: font]).width }.max() ?? 0
        return max(80, ceil(widest) + 32)
    }
}

// A size dropdown you can also type into (memory, CPU cores, disk size). The list holds common sizes in
// its range plus the maximum. A typed value that fits takes effect at once; leaving the field clamps it
// to the range and rounds it up to the step.
struct SizeField: View {
    @Binding var value: Int
    let presets: [Int]
    let range: ClosedRange<Int>
    var step = 1
    var width: CGFloat = 100
    @State private var text = ""
    @FocusState private var focused: Bool
    @Environment(\.isEnabled) private var enabled

    private var top: Int { step > 1 ? max(range.lowerBound, range.upperBound - range.upperBound % step) : range.upperBound }

    private var list: [Int] {
        var l = presets.filter { range.contains($0) && $0 % step == 0 }
        if !l.contains(top) { l.append(top) }
        return l.sorted()
    }

    var body: some View {
        HStack(spacing: 0) {
            TextField("", text: $text)
                .textFieldStyle(.plain)
                .font(Theme.sans(13))
                .foregroundColor(enabled ? Theme.text : Theme.muted)
                .padding(.leading, 8)
                .focused($focused)
                .onSubmit { commit() }
                .onChange(of: focused) { f in if !f { commit() } }
                .onChange(of: text) { t in
                    let digits = t.filter(\.isNumber)
                    if digits != t { text = digits; return }
                    if let n = Int(digits), range.contains(n), n % step == 0, n != value { value = n }
                }
            Menu {
                ForEach(list, id: \.self) { v in Button("\(v)") { value = v; text = "\(v)" } }
            } label: {
                Image(systemName: "chevron.down").font(.system(size: 9, weight: .bold)).foregroundColor(Theme.text)
            }
            .menuStyle(.borderlessButton)
            .menuIndicator(.hidden)
            .frame(width: 24, height: 26)
            .overlay(Rectangle().frame(width: 1).foregroundColor(Theme.border), alignment: .leading)
        }
        .frame(width: width, height: 26)
        .background(Theme.card)
        .overlay(Rectangle().stroke(Theme.border, lineWidth: 1))
        .opacity(enabled ? 1 : 0.6)
        .onAppear { text = "\(value)" }
        .onChange(of: value) { v in if !focused { text = "\(v)" } }
    }

    private func commit() {
        var n = min(range.upperBound, max(range.lowerBound, Int(text) ?? value))
        if step > 1, n % step != 0 { n = min(top, n + step - n % step) }
        value = n
        text = "\(n)"
    }
}

// The sizes the dropdowns offer.
enum Sizes {
    static let ram = [1, 2, 4, 6, 8, 12, 16, 20, 24, 32, 40, 48, 64, 96, 128, 192, 256, 384, 512]
    static let disk = [16, 32, 64, 128, 256, 512, 1024, 2048, 4096]
    static func cores(_ max: Int) -> [Int] { max <= 1 ? [1] : Array(stride(from: 2, through: max, by: 2)) }
    static func coreStep(_ max: Int) -> Int { max <= 1 ? 1 : 2 }
}

enum Icons {
    static let glyph = ["........", "##......", ".##.....", "..##....", ".##.....", "##......", "....####", "........"]

    static func image(size: CGFloat) -> NSImage {
        NSImage(size: NSSize(width: size, height: size), flipped: true) { rect in
            let inset = size * 0.08
            let box = rect.insetBy(dx: inset, dy: inset)
            let path = NSBezierPath(roundedRect: box, xRadius: box.width * 0.22, yRadius: box.width * 0.22)
            NSGradient(starting: NSColor(Theme.accent), ending: NSColor(Theme.accent2))?.draw(in: path, angle: 90)
            NSColor(Theme.bg).setFill()
            let cell = box.width / 10, off = box.minX + box.width * 0.1
            for y in 0..<8 {
                for (x, ch) in glyph[y].enumerated() where ch == "#" {
                    NSRect(x: off + CGFloat(x) * cell, y: box.minY + box.width * 0.1 + CGFloat(y) * cell, width: cell + 0.5, height: cell + 0.5).fill()
                }
            }
            return true
        }
    }

    // Writes PNGs named for iconutil (AppIcon.iconset), used when building the app bundle.
    static func writeIconset(to dir: URL) {
        try? FileManager.default.createDirectory(at: dir, withIntermediateDirectories: true)
        for base in [16, 32, 128, 256, 512] {
            for scale in [1, 2] {
                let px = base * scale
                let name = scale == 1 ? "icon_\(base)x\(base).png" : "icon_\(base)x\(base)@2x.png"
                guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: px, pixelsHigh: px, bitsPerSample: 8,
                                                 samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                                 colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { continue }
                NSGraphicsContext.saveGraphicsState()
                NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
                image(size: CGFloat(px)).draw(in: NSRect(x: 0, y: 0, width: px, height: px))
                NSGraphicsContext.restoreGraphicsState()
                try? rep.representation(using: .png, properties: [:])?.write(to: dir.appendingPathComponent(name))
            }
        }
    }
}

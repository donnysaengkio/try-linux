// Colors, fonts and the flat controls shared by every page (the same look as Try Linux for Windows).
import AppKit
import SwiftUI

// Colors follow Proton's palette (proton.me/blog/new-visual-universe) on a Proton Pass-style dark UI.
enum Theme {
    static let bg = rgb(25, 25, 39)              // #191927
    static let card = rgb(34, 33, 52)
    static let cardHover = rgb(42, 40, 62)
    static let cardSelected = rgb(48, 45, 69)    // #302D45
    static let box = rgb(29, 28, 45)             // containers that group rows
    static let border = rgb(56, 48, 88)          // #383058
    static let text = rgb(245, 244, 242)         // #F5F4F2
    static let muted = rgb(150, 146, 176)
    // Accent is the main call to action only (Start, Next, Save). Highlight is the logo and active
    // form elements: selection, tabs, checkboxes, progress, section titles.
    static let accent = rgb(109, 74, 255)        // #6D4AFF
    static let accentHover = rgb(138, 110, 255)  // #8A6EFF
    static let accent2 = rgb(56, 37, 128)        // #382580
    static let highlight = rgb(167, 146, 255)    // #A792FF
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

    // The monospaced font is for headings only (page and section titles, system names); everything else
    // (descriptions, labels, buttons, tabs) is the system's sans-serif font.
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
        let fill: Color = primary
            ? (configuration.isPressed ? Theme.accent2 : (hover ? Theme.accentHover : Theme.accent))
            : (configuration.isPressed || hover ? Theme.cardHover : Theme.card)
        configuration.label
            .font(Theme.sans(13, .semibold))
            .foregroundColor(primary ? .white : (danger ? Theme.danger : (accent ? Theme.highlight : Theme.text)))
            .frame(maxWidth: .infinity, maxHeight: .infinity)
            .background(fill)
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
            Rectangle().fill(selected ? Theme.highlight : Color.clear).frame(width: 4)
            VStack(alignment: .leading, spacing: 7) {
                Text(title).font(Theme.mono(13, .bold)).foregroundColor(Theme.text).lineLimit(1)
                Text(subtitle).font(Theme.sans(11.5)).foregroundColor(Theme.muted)
                    .lineLimit(twoLine ? 2 : 1)
                    .fixedSize(horizontal: false, vertical: true)
            }
            .padding(.leading, 14)
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
        .background(selected ? Theme.cardSelected : (hover ? Theme.cardHover : Theme.card))
        .overlay(Rectangle().stroke(selected ? Theme.highlight : Theme.border, lineWidth: 1))
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
                    .background(i == selection ? Theme.highlight : Theme.card)
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
                    .background(i == selection ? Theme.cardSelected : Theme.card)
                    .overlay(Rectangle().stroke(i == selection ? Theme.highlight : Theme.border, lineWidth: 1))
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
            .font(Theme.mono(19, .bold))
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
        Text(text).font(Theme.mono(13, .bold)).foregroundColor(Theme.highlight).padding(.top, 28).padding(.bottom, 4)
    }
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

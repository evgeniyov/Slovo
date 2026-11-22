// Draws the Slovo app icon into an .iconset folder: a warm dark squircle with a
// cream waveform whose centre bar is the orange of the keycap badge.
//
// Usage: swift Scripts/make_icon.swift <output.iconset>

import AppKit
import Foundation

func fail(_ message: String) -> Never {
    FileHandle.standardError.write(Data("make_icon: \(message)\n".utf8))
    exit(1)
}

func color(_ hex: UInt32, alpha: CGFloat = 1) -> CGColor {
    CGColor(srgbRed: CGFloat((hex >> 16) & 0xFF) / 255, green: CGFloat((hex >> 8) & 0xFF) / 255,
            blue: CGFloat(hex & 0xFF) / 255, alpha: alpha)
}

/// Renders the icon at an exact pixel size.
func drawIcon(pixels: Int) -> Data {
    let side = CGFloat(pixels)
    guard let context = CGContext(data: nil, width: pixels, height: pixels, bitsPerComponent: 8, bytesPerRow: 0,
                                  space: CGColorSpace(name: CGColorSpace.sRGB) ?? CGColorSpaceCreateDeviceRGB(),
                                  bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
        fail("cannot create a \(pixels) px context")
    }

    // macOS icon grid: the squircle fills about 80% of the canvas.
    let inset = side * 0.098
    let tile = CGRect(x: inset, y: inset, width: side - inset * 2, height: side - inset * 2)
    let radius = tile.width * 0.225
    let tilePath = CGPath(roundedRect: tile, cornerWidth: radius, cornerHeight: radius, transform: nil)

    // Soft shadow under the tile.
    context.saveGState()
    context.setShadow(offset: CGSize(width: 0, height: -side * 0.012), blur: side * 0.03, color: color(0x000000, alpha: 0.35))
    context.addPath(tilePath)
    context.setFillColor(color(0x1E1A17))
    context.fillPath()
    context.restoreGState()

    // Warm gradient from the banner colours.
    context.saveGState()
    context.addPath(tilePath)
    context.clip()
    let colors = [color(0x2A231F), color(0x4A3A30)] as CFArray
    if let gradient = CGGradient(colorsSpace: CGColorSpace(name: CGColorSpace.sRGB), colors: colors, locations: [0, 1]) {
        context.drawLinearGradient(gradient, start: CGPoint(x: tile.minX, y: tile.maxY),
                                   end: CGPoint(x: tile.maxX, y: tile.minY), options: [])
    }
    context.restoreGState()

    // Waveform: seven rounded bars, symmetric, the middle one orange.
    let heights: [CGFloat] = [0.20, 0.36, 0.54, 0.70, 0.54, 0.36, 0.20]
    let barWidth = tile.width * 0.074
    let gap = tile.width * 0.046
    let totalWidth = CGFloat(heights.count) * barWidth + CGFloat(heights.count - 1) * gap
    var x = tile.midX - totalWidth / 2
    for (index, fraction) in heights.enumerated() {
        let height = tile.height * fraction
        let bar = CGRect(x: x, y: tile.midY - height / 2, width: barWidth, height: height)
        context.addPath(CGPath(roundedRect: bar, cornerWidth: barWidth / 2, cornerHeight: barWidth / 2, transform: nil))
        context.setFillColor(index == heights.count / 2 ? color(0xF4A259) : color(0xF2EDE4))
        context.fillPath()
        x += barWidth + gap
    }

    guard let image = context.makeImage(),
          let png = NSBitmapImageRep(cgImage: image).representation(using: .png, properties: [:]) else {
        fail("cannot encode the \(pixels) px image")
    }
    return png
}

let arguments = CommandLine.arguments
guard arguments.count == 2 else {
    fail("usage: swift make_icon.swift <output.iconset>")
}
let output = URL(fileURLWithPath: arguments[1], isDirectory: true)
do {
    try FileManager.default.createDirectory(at: output, withIntermediateDirectories: true)
    for points in [16, 32, 128, 256, 512] {
        try drawIcon(pixels: points).write(to: output.appendingPathComponent("icon_\(points)x\(points).png"))
        try drawIcon(pixels: points * 2).write(to: output.appendingPathComponent("icon_\(points)x\(points)@2x.png"))
    }
} catch {
    fail(error.localizedDescription)
}

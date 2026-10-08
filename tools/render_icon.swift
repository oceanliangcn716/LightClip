import CoreGraphics
import Foundation
import ImageIO

private let canvasSize: CGFloat = 1024
private let rgbSpace = CGColorSpaceCreateDeviceRGB()

private func rgba(_ red: CGFloat, _ green: CGFloat, _ blue: CGFloat, _ alpha: CGFloat = 1) -> CGColor {
    CGColor(colorSpace: rgbSpace, components: [red, green, blue, alpha])!
}

private func roundedPath(_ rect: CGRect, radius: CGFloat) -> CGPath {
    CGPath(
        roundedRect: rect,
        cornerWidth: radius,
        cornerHeight: radius,
        transform: nil)
}

private func gradient(_ colors: [CGColor], locations: [CGFloat] = [0, 1]) -> CGGradient {
    CGGradient(colorsSpace: rgbSpace, colors: colors as CFArray, locations: locations)!
}

private func drawGradient(
    _ context: CGContext,
    path: CGPath,
    gradient: CGGradient,
    start: CGPoint,
    end: CGPoint) {
    context.saveGState()
    context.addPath(path)
    context.clip()
    context.drawLinearGradient(gradient, start: start, end: end, options: [])
    context.restoreGState()
}

private func drawIcon(in context: CGContext, size: Int) {
    let scale = CGFloat(size) / canvasSize
    context.saveGState()
    // Work in the same top-left coordinate system as the SVG master.
    context.translateBy(x: 0, y: CGFloat(size))
    context.scaleBy(x: scale, y: -scale)

    context.clear(CGRect(x: 0, y: 0, width: canvasSize, height: canvasSize))

    let backgroundPath = roundedPath(CGRect(x: 40, y: 40, width: 944, height: 944), radius: 236)
    let backgroundGradient = gradient([
        rgba(0.09, 0.435, 0.824),
        rgba(0.086, 0.561, 0.796),
        rgba(0.067, 0.698, 0.624),
    ], locations: [0, 0.52, 1])
    drawGradient(
        context,
        path: backgroundPath,
        gradient: backgroundGradient,
        start: CGPoint(x: 96, y: 80),
        end: CGPoint(x: 928, y: 944))

    context.saveGState()
    context.addPath(roundedPath(CGRect(x: 40.75, y: 40.75, width: 942.5, height: 942.5), radius: 235.25))
    context.setStrokeColor(rgba(1, 1, 1, 0.17))
    context.setLineWidth(1.5)
    context.strokePath()
    context.restoreGState()

    // Two quiet, rounded arrows sit behind the clipboard.
    context.saveGState()
    context.setStrokeColor(rgba(1, 1, 1, 0.88))
    context.setLineWidth(38)
    context.setLineCap(.round)
    context.setLineJoin(.round)

    let upper = CGMutablePath()
    upper.move(to: CGPoint(x: 687, y: 306))
    upper.addCurve(
        to: CGPoint(x: 900, y: 486),
        control1: CGPoint(x: 814, y: 294),
        control2: CGPoint(x: 900, y: 372))
    context.addPath(upper)
    context.strokePath()

    let upperHead = CGMutablePath()
    upperHead.move(to: CGPoint(x: 900, y: 486))
    upperHead.addLine(to: CGPoint(x: 844, y: 446))
    upperHead.move(to: CGPoint(x: 900, y: 486))
    upperHead.addLine(to: CGPoint(x: 892, y: 415))
    context.addPath(upperHead)
    context.strokePath()

    let lower = CGMutablePath()
    lower.move(to: CGPoint(x: 337, y: 718))
    lower.addCurve(
        to: CGPoint(x: 143, y: 540),
        control1: CGPoint(x: 211, y: 728),
        control2: CGPoint(x: 126, y: 651))
    context.addPath(lower)
    context.strokePath()

    let lowerHead = CGMutablePath()
    lowerHead.move(to: CGPoint(x: 143, y: 540))
    lowerHead.addLine(to: CGPoint(x: 204, y: 549))
    lowerHead.move(to: CGPoint(x: 143, y: 540))
    lowerHead.addLine(to: CGPoint(x: 170, y: 596))
    context.addPath(lowerHead)
    context.strokePath()
    context.restoreGState()

    let paperPath = roundedPath(CGRect(x: 329, y: 216, width: 366, height: 563), radius: 48)
    let paperGradient = gradient([
        rgba(1, 1, 1),
        rgba(0.933, 0.973, 0.98),
    ])
    context.saveGState()
    context.setShadow(offset: CGSize(width: 0, height: -18), blur: 18, color: rgba(0.027, 0.235, 0.455, 0.25))
    drawGradient(
        context,
        path: paperPath,
        gradient: paperGradient,
        start: CGPoint(x: 348, y: 232),
        end: CGPoint(x: 676, y: 786))
    context.restoreGState()

    context.saveGState()
    let tabPath = CGPath(
        roundedRect: CGRect(x: 424, y: 152, width: 176, height: 102),
        cornerWidth: 39,
        cornerHeight: 39,
        transform: nil)
    context.setFillColor(rgba(1, 1, 1))
    context.addPath(tabPath)
    context.fillPath()
    context.setFillColor(rgba(0.09, 0.435, 0.824, 0.92))
    context.addPath(roundedPath(CGRect(x: 469, y: 184, width: 86, height: 34), radius: 17))
    context.fillPath()
    context.restoreGState()

    context.saveGState()
    context.setFillColor(rgba(0.086, 0.561, 0.796, 0.92))
    for (x, y, width) in [(392, 363, 240), (392, 440, 188), (392, 517, 222)] {
        context.addPath(roundedPath(CGRect(x: CGFloat(x), y: CGFloat(y), width: CGFloat(width), height: 30), radius: 15))
        context.fillPath()
    }
    context.setFillColor(rgba(0.067, 0.698, 0.624, 0.76))
    context.addPath(roundedPath(CGRect(x: 392, y: 624, width: 128, height: 24), radius: 12))
    context.fillPath()
    context.restoreGState()

    context.restoreGState()
}

private func pngData(for image: CGImage) throws -> Data {
    let data = NSMutableData()
    guard let destination = CGImageDestinationCreateWithData(
        data as CFMutableData,
        "public.png" as CFString,
        1,
        nil) else {
        throw RenderError.message("无法创建 PNG 编码器")
    }
    CGImageDestinationAddImage(destination, image, nil)
    guard CGImageDestinationFinalize(destination) else {
        throw RenderError.message("PNG 编码失败")
    }
    return data as Data
}

private func renderPNG(size: Int) throws -> Data {
    guard let context = CGContext(
        data: nil,
        width: size,
        height: size,
        bitsPerComponent: 8,
        bytesPerRow: size * 4,
        space: rgbSpace,
        bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue) else {
        throw RenderError.message("无法创建 (size)x(size) 绘图上下文")
    }
    drawIcon(in: context, size: size)
    guard let image = context.makeImage() else {
        throw RenderError.message("无法生成 (size)x(size) 图像")
    }
    return try pngData(for: image)
}

private func write(_ data: Data, to url: URL) throws {
    try data.write(to: url, options: .atomic)
}

private func littleEndian(_ value: UInt16) -> [UInt8] {
    [UInt8(value & 0xFF), UInt8((value >> 8) & 0xFF)]
}

private func littleEndian(_ value: UInt32) -> [UInt8] {
    [
        UInt8(value & 0xFF),
        UInt8((value >> 8) & 0xFF),
        UInt8((value >> 16) & 0xFF),
        UInt8((value >> 24) & 0xFF),
    ]
}

private func makeICO(images: [(size: Int, data: Data)]) -> Data {
    var ico = Data()
    ico.append(contentsOf: littleEndian(UInt16(0)))
    ico.append(contentsOf: littleEndian(UInt16(1)))
    ico.append(contentsOf: littleEndian(UInt16(images.count)))

    var offset = 6 + images.count * 16
    for image in images {
        let dimension = UInt8(image.size == 256 ? 0 : image.size)
        ico.append(dimension)
        ico.append(dimension)
        ico.append(0) // palette entries; PNG payloads use their own color data.
        ico.append(0)
        ico.append(contentsOf: littleEndian(UInt16(1))) // color planes
        ico.append(contentsOf: littleEndian(UInt16(32))) // RGBA
        ico.append(contentsOf: littleEndian(UInt32(image.data.count)))
        ico.append(contentsOf: littleEndian(UInt32(offset)))
        offset += image.data.count
    }

    for image in images {
        ico.append(image.data)
    }
    return ico
}

private func run(_ executable: String, arguments: [String]) throws {
    let process = Process()
    process.executableURL = URL(fileURLWithPath: executable)
    process.arguments = arguments
    let errorPipe = Pipe()
    process.standardError = errorPipe
    try process.run()
    process.waitUntilExit()
    if process.terminationStatus != 0 {
        let output = String(data: errorPipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
        throw RenderError.message("\(executable) 执行失败：\(output.trimmingCharacters(in: .whitespacesAndNewlines))")
    }
}

private enum RenderError: Error, CustomStringConvertible {
    case message(String)

    var description: String {
        switch self {
        case .message(let message): return message
        }
    }
}

private func renderAll(to assetsURL: URL) throws {
    let fileManager = FileManager.default
    try fileManager.createDirectory(at: assetsURL, withIntermediateDirectories: true)

    let previewData = try renderPNG(size: 1024)
    try write(previewData, to: assetsURL.appendingPathComponent("LightClip-preview.png"))

    let iconsetURL = fileManager.temporaryDirectory
        .appendingPathComponent("LightClip-\(UUID().uuidString).iconset")
    try fileManager.createDirectory(at: iconsetURL, withIntermediateDirectories: true)
    defer { try? fileManager.removeItem(at: iconsetURL) }

    let iconsetEntries: [(String, Int)] = [
        ("icon_16x16.png", 16),
        ("icon_16x16@2x.png", 32),
        ("icon_32x32.png", 32),
        ("icon_32x32@2x.png", 64),
        ("icon_128x128.png", 128),
        ("icon_128x128@2x.png", 256),
        ("icon_256x256.png", 256),
        ("icon_256x256@2x.png", 512),
        ("icon_512x512.png", 512),
        ("icon_512x512@2x.png", 1024),
    ]

    var cache: [Int: Data] = [1024: previewData]
    for (name, size) in iconsetEntries {
        if cache[size] == nil {
            cache[size] = try renderPNG(size: size)
        }
        try write(cache[size]!, to: iconsetURL.appendingPathComponent(name))
    }

    try run(
        "/usr/bin/iconutil",
        arguments: ["-c", "icns", "-o", assetsURL.appendingPathComponent("LightClip.icns").path, iconsetURL.path])

    let icoSizes = [16, 24, 32, 48, 64, 128, 256]
    var icoImages: [(size: Int, data: Data)] = []
    for size in icoSizes {
        if cache[size] == nil {
            cache[size] = try renderPNG(size: size)
        }
        icoImages.append((size, cache[size]!))
    }
    try write(
        makeICO(images: icoImages),
        to: assetsURL.appendingPathComponent("LightClip.ico"))
}

let defaultAssetsURL = URL(fileURLWithPath: #filePath)
    .deletingLastPathComponent()
    .deletingLastPathComponent()
    .appendingPathComponent("assets", isDirectory: true)
let assetsURL = CommandLine.arguments.count > 1
    ? URL(fileURLWithPath: CommandLine.arguments[1], isDirectory: true)
    : defaultAssetsURL

do {
    try renderAll(to: assetsURL)
    print("已生成 LightClip 图标资源：\(assetsURL.path)")
} catch {
    FileHandle.standardError.write(Data("图标生成失败：\(error)\n".utf8))
    Foundation.exit(1)
}

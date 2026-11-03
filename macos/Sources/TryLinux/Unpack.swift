// Decompresses a zstd stream split across several files into a sparse raw disk image.
import CZstd
import Foundation

enum Unpack {
    static func zstd(parts: [URL], to output: URL, cancelled: () -> Bool, report: (Double, Int64) -> Void) throws {
        guard let stream = ZSTD_createDStream() else { throw AppError("Couldn't start the decompressor.") }
        defer { ZSTD_freeDStream(stream) }
        _ = ZSTD_initDStream(stream)

        let fm = FileManager.default
        try? fm.removeItem(at: output)
        guard fm.createFile(atPath: output.path, contents: nil) else { throw AppError("Couldn't create \(output.lastPathComponent).") }
        let out = try FileHandle(forWritingTo: output)
        defer { try? out.close() }

        let totalIn = parts.reduce(Int64(0)) { $0 + Int64((try? $1.resourceValues(forKeys: [.fileSizeKey]).fileSize) ?? 0) }
        var consumed: Int64 = 0, written: Int64 = 0, lastResult = 0
        let inSize = ZSTD_DStreamInSize(), outSize = max(ZSTD_DStreamOutSize(), 4 << 20)
        let outBuf = UnsafeMutableRawPointer.allocate(byteCount: outSize, alignment: 16)
        defer { outBuf.deallocate() }
        var tick = 0

        for part in parts {
            let input = try FileHandle(forReadingFrom: part)
            defer { try? input.close() }
            while let chunk = try input.read(upToCount: max(inSize, 1 << 20)), !chunk.isEmpty {
                if cancelled() { throw CancellationError() }
                try chunk.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
                    var inBuf = ZSTD_inBuffer(src: raw.baseAddress, size: raw.count, pos: 0)
                    while inBuf.pos < inBuf.size {
                        var o = ZSTD_outBuffer(dst: outBuf, size: outSize, pos: 0)
                        let r = ZSTD_decompressStream(stream, &o, &inBuf)
                        if ZSTD_isError(r) != 0 {
                            throw AppError("The image is damaged (\(String(cString: ZSTD_getErrorName(r)))). Delete it with Clean up and try again.")
                        }
                        lastResult = r
                        try writeSparse(out, UnsafeRawBufferPointer(start: outBuf, count: o.pos), at: &written)
                    }
                }
                consumed += Int64(chunk.count)
                tick += 1
                if tick % 16 == 0 { report(Double(consumed) / Double(max(1, totalIn)), written) }
            }
        }
        // 0 means the last frame was fully decoded. Otherwise flush what the decoder still holds; if it
        // produces nothing and still isn't done, the input really ended mid-frame.
        while lastResult != 0 {
            var empty = ZSTD_inBuffer(src: nil, size: 0, pos: 0)
            var o = ZSTD_outBuffer(dst: outBuf, size: outSize, pos: 0)
            let r = ZSTD_decompressStream(stream, &o, &empty)
            if ZSTD_isError(r) != 0 { throw AppError("The image is damaged (\(String(cString: ZSTD_getErrorName(r)))).") }
            try writeSparse(out, UnsafeRawBufferPointer(start: outBuf, count: o.pos), at: &written)
            lastResult = r
            if o.pos == 0 { break }
        }
        if lastResult != 0 { throw AppError("The image ended early; a part may be missing or incomplete. Try again.") }
        try out.truncate(atOffset: UInt64(written))   // sets the full size even if it ends in zeros
        report(1, written)
    }

    // Writes non-zero 64 KB blocks and skips zero ones, leaving holes on APFS.
    private static func writeSparse(_ h: FileHandle, _ buf: UnsafeRawBufferPointer, at offset: inout Int64) throws {
        let block = 64 << 10
        var i = 0
        while i < buf.count {
            let n = min(block, buf.count - i)
            let slice = UnsafeRawBufferPointer(rebasing: buf[i..<(i + n)])
            if !isZero(slice) {
                try h.seek(toOffset: UInt64(offset))
                try h.write(contentsOf: Data(bytesNoCopy: UnsafeMutableRawPointer(mutating: slice.baseAddress!), count: n, deallocator: .none))
            }
            offset += Int64(n)
            i += n
        }
    }

    private static func isZero(_ p: UnsafeRawBufferPointer) -> Bool {
        let words = p.count / 8
        let w = p.baseAddress!.assumingMemoryBound(to: UInt64.self)
        for k in 0..<words where w[k] != 0 { return false }
        for k in (words * 8)..<p.count where p[k] != 0 { return false }
        return true
    }
}

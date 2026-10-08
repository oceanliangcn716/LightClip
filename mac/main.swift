import AppKit
import Foundation
import CryptoKit

if CommandLine.arguments.contains("--stream-self-test") {
    do { try StreamTest.run(); exit(0) } catch { fputs("Stream test failed: \(type(of: error))\n", stderr); exit(1) }
}
if CommandLine.arguments.contains("--stream-big-test") {
    do { try StreamTest.big(); exit(0) } catch { fputs("Big stream test failed: \(type(of: error))\n", stderr); exit(1) }
}
if CommandLine.arguments.contains("--stream-server") { StreamTest.server(); exit(0) }
if CommandLine.arguments.contains("--stream-client") { StreamTest.client(); exit(0) }
if CommandLine.arguments.contains("--group-self-test") {
    do { try GroupTest.run(); exit(0) }
    catch { fputs("Group test failed: \(type(of: error))\n", stderr); exit(1) }
}
if CommandLine.arguments.contains("--pair-server") { GroupTest.pairServer(); exit(0) }
if CommandLine.arguments.contains("--pair-client") { GroupTest.pairClient(); exit(0) }
if CommandLine.arguments.contains("--self-test") {
    do { try SelfTest.run(); exit(0) }
    catch { fputs("Self-test failed: \(type(of: error))\n", stderr); exit(1) }
}
if CommandLine.arguments.contains("--interop-server") {
    SelfTest.interopServer()
    exit(0)
}
if CommandLine.arguments.contains("--interop-client") {
    SelfTest.interopClient()
    exit(0)
}
let application = NSApplication.shared
// Bundle identifier ensures launching the app twice reuses the existing process.
let delegate = App()
application.delegate = delegate
application.run()

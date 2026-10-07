import AppKit
import Foundation

final class ReadingView: NSView {
    override var isFlipped: Bool { true }
    override func draw(_ dirtyRect: NSRect) {
        NSColor.white.setFill(); bounds.fill()
        ("A document in another application" as NSString).draw(at: NSPoint(x: 36,y: 24), withAttributes:[.font:NSFont.systemFont(ofSize:16),.foregroundColor:NSColor.darkGray])
        ("significant" as NSString).draw(at: NSPoint(x:36,y:76),withAttributes:[.font:NSFont.systemFont(ofSize:38),.foregroundColor:NSColor.black])
        ("confidence interval" as NSString).draw(at:NSPoint(x:36,y:176),withAttributes:[.font:NSFont.systemFont(ofSize:28),.foregroundColor:NSColor.black])
    }
}
let app = NSApplication.shared
app.setActivationPolicy(.regular)
let view = ReadingView(frame:NSRect(x:0,y:0,width:660,height:320))
let window = NSWindow(contentRect:view.bounds,styleMask:[.titled,.closable],backing:.buffered,defer:false)
window.title="Luma external gesture test document"
window.contentView=view
window.center()
window.makeKeyAndOrderFront(nil)
app.activate(ignoringOtherApps:true)
DispatchQueue.main.asyncAfter(deadline:.now()+0.5) {
    func quartz(_ p:NSPoint)->[String:Double] {
        let screen=window.convertPoint(toScreen:view.convert(p,to:nil))
        return ["x":Double(screen.x),"y":Double(CGDisplayBounds(CGMainDisplayID()).height-screen.y)]
    }
    let coordinates=["word":quartz(NSPoint(x:124,y:99)),"start":quartz(NSPoint(x:24,y:170)),"end":quartz(NSPoint(x:365,y:216)),
                     "lineStart":quartz(NSPoint(x:24,y:195)),"lineEnd":quartz(NSPoint(x:365,y:195))]
    let data=try! JSONSerialization.data(withJSONObject:coordinates)
    try! data.write(to:URL(fileURLWithPath:CommandLine.arguments[1]))
}
app.run()

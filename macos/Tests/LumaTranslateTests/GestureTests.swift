import XCTest
import AppKit
@testable import LumaTranslate

final class GestureTests: XCTestCase {
    func testCoveredOwnWindowDoesNotBlockDocument() {
        func item(_ pid:Int,_ number:Int)->[String:Any] {
            [kCGWindowOwnerPID as String:pid,kCGWindowNumber as String:number,
             kCGWindowBounds as String:CGRect(x:0,y:0,width:800,height:600).dictionaryRepresentation,
             kCGWindowAlpha as String:1.0]
        }
        let document=item(2,20), luma=item(1,10), badge=item(1,11)
        let point=CGPoint(x:200,y:200)
        XCTAssertFalse(WindowHitTesting.isOwnInteractiveWindow(at:point,windows:[document,luma],ownPID:1,interactiveNumbers:[10]))
        XCTAssertTrue(WindowHitTesting.isOwnInteractiveWindow(at:point,windows:[luma,document],ownPID:1,interactiveNumbers:[10]))
        XCTAssertFalse(WindowHitTesting.isOwnInteractiveWindow(at:point,windows:[badge,document,luma],ownPID:1,interactiveNumbers:[10]))
    }

    @MainActor func testDoubleClickAndImmediateRightDrag() {
        let controller=MouseGestureController()
        var clicked:CGPoint?
        var selected:CGRect?
        controller.onPointGesture={ clicked=$0 }
        controller.onSelectionFinished={ selected=$0; _ = $1 }
        func send(_ type:CGEventType,_ p:CGPoint) {
            let event=CGEvent(mouseEventSource:nil,mouseType:type,mouseCursorPosition:p,mouseButton:.right)!
            XCTAssertTrue(controller.handle(type:type,event:event))
        }
        let p=CGPoint(x:100,y:100)
        send(.rightMouseDown,p); send(.rightMouseUp,p)
        send(.rightMouseDown,p); send(.rightMouseUp,p)
        XCTAssertEqual(clicked,p)
        send(.rightMouseDown,p)
        send(.rightMouseDragged,CGPoint(x:260,y:140))
        send(.rightMouseUp,CGPoint(x:260,y:140))
        XCTAssertEqual(selected,CGRect(x:100,y:100,width:160,height:40))
    }
}

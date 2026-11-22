import ApplicationServices
@testable import SlovoCore
import XCTest

final class InputFocusedInputTests: XCTestCase {
    private func classify(_ role: String?, subrole: String? = nil, editable: Bool = false,
                          settable: Bool = false, selection: Bool = false) -> InputFocus {
        FocusedInputWatcher.classify(role: role, subrole: subrole, editable: editable,
                                     valueSettable: settable, hasSelection: selection)
    }

    func testTextRolesAreFields() {
        XCTAssertEqual(classify(kAXTextFieldRole), .textInput)
        XCTAssertEqual(classify(kAXTextAreaRole), .textInput)
        XCTAssertEqual(classify(kAXComboBoxRole), .textInput)
        XCTAssertEqual(classify("AXSearchField"), .textInput)
    }

    func testAPasswordFieldIsNotAPlaceToDictate() {
        XCTAssertEqual(classify(kAXTextFieldRole, subrole: kAXSecureTextFieldSubrole), .other)
    }

    func testAnEditableWebElementIsAField() {
        XCTAssertEqual(classify("AXGroup", editable: true), .textInput)
    }

    func testAGenericElementThatTakesTextIsAField() {
        XCTAssertEqual(classify("AXGroup", settable: true, selection: true), .textInput)
    }

    func testASettableValueAloneIsNotEnough() {
        // Sliders and checkboxes have settable values too.
        XCTAssertEqual(classify(kAXSliderRole, settable: true), .other)
    }

    func testButtonsAndListsAreNotFields() {
        XCTAssertEqual(classify(kAXButtonRole), .other)
        XCTAssertEqual(classify(kAXListRole), .other)
    }

    func testAnEditorContainerCannotProveThatTypingIsUnavailable() {
        // Brave's ChatGPT composer can expose AXGroup/AXApplicationDialog with
        // a selection range, but without an editable or settable value attribute.
        XCTAssertEqual(classify(kAXGroupRole, subrole: "AXApplicationDialog", selection: true), .unknown)
        XCTAssertEqual(classify("AXWebArea"), .unknown)
        XCTAssertEqual(classify(kAXWindowRole), .unknown)
        XCTAssertEqual(classify(kAXScrollAreaRole), .unknown)
    }

    func testNothingReadableIsUnknown() {
        XCTAssertEqual(classify(nil), .unknown)
    }

    func testMissingAccessibilityFocusDoesNotHideChatGPTsComposer() {
        // Observed in the native ChatGPT app while a text field takes typing.
        XCTAssertEqual(FocusProbe.focusWithoutElement(.noValue), .unknown)
        XCTAssertEqual(FocusProbe.focusWithoutElement(.success), .unknown)
    }

    func testAnAppThatDoesNotAnswerMightStillHaveAField() {
        XCTAssertEqual(FocusProbe.focusWithoutElement(.cannotComplete), .unknown)
        XCTAssertEqual(FocusProbe.focusWithoutElement(.apiDisabled), .unknown)
        XCTAssertEqual(FocusProbe.focusWithoutElement(.notImplemented), .unknown)
    }
}

using Slovo.Platform.Windows.Input;

namespace Slovo.Platform.Windows.Tests;

/// <summary>The focused-field rule (InputFocusedInputTests.swift) with UI Automation's control types.</summary>
public class FocusClassifierTests
{
    static InputFocus Classify(int? controlType, bool password = false, bool focusable = true, bool value = false,
        bool readOnly = false, bool text = false) =>
        FocusClassifier.Classify(new FocusedElement(controlType, password, focusable, value, readOnly, text));

    [Fact]
    public void EditBoxesAreFields()
    {
        Assert.Equal(InputFocus.TextInput, Classify(FocusClassifier.EditControlType));
        Assert.Equal(InputFocus.TextInput, Classify(FocusClassifier.EditControlType, value: true, text: true));
    }

    [Fact]
    public void AReadOnlyEditBoxIsNotAField() =>
        Assert.Equal(InputFocus.Other, Classify(FocusClassifier.EditControlType, value: true, readOnly: true));

    [Fact]
    public void APasswordFieldIsNotAPlaceToDictate() =>
        Assert.Equal(InputFocus.Other, Classify(FocusClassifier.EditControlType, password: true, value: true));

    [Fact]
    public void AnEditableComboBoxIsAFieldAndADropDownIsNot()
    {
        Assert.Equal(InputFocus.TextInput, Classify(FocusClassifier.ComboBoxControlType, value: true));
        Assert.Equal(InputFocus.Other, Classify(FocusClassifier.ComboBoxControlType, value: true, readOnly: true));
        Assert.Equal(InputFocus.Other, Classify(FocusClassifier.ComboBoxControlType));
    }

    [Fact]
    public void DocumentsTakeTextUnlessTheyAreReadOnlyPages()
    {
        // Word and rich editors: a focusable document with text and no value pattern.
        Assert.Equal(InputFocus.TextInput, Classify(FocusClassifier.DocumentControlType, text: true));
        // Notepad's document: a settable value.
        Assert.Equal(InputFocus.TextInput, Classify(FocusClassifier.DocumentControlType, value: true, text: true));
        // A web page: its value (the address) is read-only.
        Assert.Equal(InputFocus.Other, Classify(FocusClassifier.DocumentControlType, value: true, readOnly: true, text: true));
        Assert.Equal(InputFocus.Other, Classify(FocusClassifier.DocumentControlType, focusable: false, text: true));
    }

    [Fact]
    public void AGenericElementThatTakesTextIsAField()
    {
        Assert.Equal(InputFocus.TextInput, Classify(FocusClassifier.GroupControlType, value: true, text: true));
        Assert.Equal(InputFocus.TextInput, Classify(FocusClassifier.CustomControlType, value: true, text: true));
        Assert.Equal(InputFocus.TextInput, Classify(FocusClassifier.PaneControlType, value: true, text: true));
    }

    [Fact]
    public void ASettableValueAloneIsNotEnough()
    {
        // Spinners and sliders have settable values too.
        Assert.Equal(InputFocus.Other, Classify(FocusClassifier.SpinnerControlType, value: true));
        Assert.Equal(InputFocus.Other, Classify(FocusClassifier.SliderControlType, value: true));
        Assert.Equal(InputFocus.Other, Classify(FocusClassifier.GroupControlType, value: true, text: true, focusable: false));
    }

    [Fact]
    public void ButtonsAndListsAreNotFields()
    {
        Assert.Equal(InputFocus.Other, Classify(FocusClassifier.ButtonControlType));
        Assert.Equal(InputFocus.Other, Classify(FocusClassifier.ListControlType));
        Assert.Equal(InputFocus.Other, Classify(FocusClassifier.PaneControlType, text: true));
    }

    [Fact]
    public void NothingReadableIsUnknown()
    {
        Assert.Equal(InputFocus.Unknown, Classify(null));
        Assert.Equal(InputFocus.Unknown, FocusClassifier.Classify(null));
    }

    [Fact]
    public void AnAppThatSaysNothingIsFocusedHasNoField() => Assert.Equal(InputFocus.Other, FocusClassifier.FocusWithoutElement(0));

    [Fact]
    public void AnAppThatDoesNotAnswerMightStillHaveAField()
    {
        Assert.Equal(InputFocus.Unknown, FocusClassifier.FocusWithoutElement(unchecked((int)0x80131505))); // UIA_E_TIMEOUT
        Assert.Equal(InputFocus.Unknown, FocusClassifier.FocusWithoutElement(unchecked((int)0x80040201))); // UIA_E_ELEMENTNOTAVAILABLE
        Assert.Equal(InputFocus.Unknown, FocusClassifier.FocusWithoutElement(unchecked((int)0x80004005))); // E_FAIL
    }

    [Fact]
    public void TheInterfaceAnswer()
    {
        Assert.True(FocusClassifier.ToTextInputFocused(InputFocus.TextInput));
        Assert.False(FocusClassifier.ToTextInputFocused(InputFocus.Other));
        Assert.Null(FocusClassifier.ToTextInputFocused(InputFocus.Unknown));
    }
}

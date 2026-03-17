using System;
using System.Windows.Automation;

class Program {
    static void Main() {
        try {
            var el = AutomationElement.FocusedElement;
            if (el != null) {
                Console.WriteLine("Type: " + el.Current.ControlType.ProgrammaticName);
                if (el.TryGetCurrentPattern(ValuePattern.Pattern, out var vpObj)) {
                    var vp = (ValuePattern)vpObj;
                    Console.WriteLine("Value: " + vp.Current.Value);
                }
                if (el.TryGetCurrentPattern(TextPattern.Pattern, out var tpObj)) {
                    var tp = (TextPattern)tpObj;
                    var text = tp.DocumentRange.GetText(-1);
                    Console.WriteLine("Text: " + text);
                    var sels = tp.GetSelection();
                    if (sels.Length > 0) {
                        var rangeBefore = tp.DocumentRange.Clone();
                        rangeBefore.MoveEndpointByRange(TextPatternRangeEndpoint.End, sels[0], TextPatternRangeEndpoint.Start);
                        Console.WriteLine("Cursor pos: " + rangeBefore.GetText(-1).Length);
                    }
                }
            }
        } catch (Exception ex) {
            Console.WriteLine(ex.Message);
        }
    }
}

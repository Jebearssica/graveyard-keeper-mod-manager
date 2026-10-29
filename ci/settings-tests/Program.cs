using System;
using System.Collections.Generic;
using System.Globalization;
using GKModManager;

namespace GkmmSettingsTests
{
    internal enum TestMode
    {
        Alpha,
        Beta,
        Gamma
    }

    internal static class Program
    {
        private static int _checks;
        private static int _failures;

        private static void Check(bool ok, string name)
        {
            _checks++;
            if (!ok) _failures++;
            Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name);
        }

        private static void CheckEqual(object expected, object actual, string name)
        {
            var ok = Equals(expected, actual);
            Check(ok, ok ? name : name + "  (expected " + (expected ?? "null") + ", got " + (actual ?? "null") + ")");
        }

        /// <summary>A number field backed by a boxed value, the way the scanner wires it up.</summary>
        private static SettingField Number(Type type, object initial, bool isInteger, double? min, double? max,
            double step)
        {
            var boxed = initial;
            return new SettingField
            {
                Kind = SettingKind.Number,
                ValueType = type,
                IsInteger = isInteger,
                Min = min ?? double.NegativeInfinity,
                Max = max ?? double.PositiveInfinity,
                Step = step,
                Read = () => boxed,
                Write = value => boxed = SettingValues.ConvertNumber(value, type)
            };
        }

        private static int Main()
        {
            // ---- numeric stepping -------------------------------------------
            var intField = Number(typeof(int), 5, true, 0, 10, 1);
            CheckEqual(1.0, intField.Step, "int step defaults to 1");
            intField.StepBy(1);
            CheckEqual(6, intField.Read(), "int step increments");
            intField.StepBy(-1);
            CheckEqual(5, intField.Read(), "int step decrements");
            Check(intField.Read() is int, "stepping an int keeps it an int");

            for (var i = 0; i < 20; i++) intField.StepBy(1);
            CheckEqual(10, intField.Read(), "int stepping clamps to the maximum");
            for (var i = 0; i < 30; i++) intField.StepBy(-1);
            CheckEqual(0, intField.Read(), "int stepping clamps to the minimum");

            var floatField = Number(typeof(float), 0.5f, false, 0, 1, SettingValues.NiceStep(1.0 / 50));
            CheckEqual(0.02, floatField.Step, "float step is derived from the range");
            floatField.StepBy(1);
            Check(Math.Abs((float)floatField.Read() - 0.52f) < 1e-5, "float step increments");
            Check(floatField.Read() is float, "stepping a float keeps it a float");

            floatField.TryApplyText("0.75");
            Check(Math.Abs((float)floatField.Read() - 0.75f) < 1e-5, "typed float is applied");
            floatField.TryApplyText("5");
            Check(Math.Abs((float)floatField.Read() - 1f) < 1e-5, "typed float is clamped");

            var wideField = Number(typeof(int), 1000, true, null, null, 1);
            for (var i = 0; i < 5; i++) wideField.StepBy(1);
            CheckEqual(1005, wideField.Read(), "fields without a range are not clamped");

            // ---- numeric text entry ------------------------------------------
            Check(intField.TryApplyText("7"), "typed int is accepted");
            CheckEqual(7, intField.Read(), "typed int is applied");
            Check(!intField.TryApplyText("not a number"), "invalid number is rejected");
            CheckEqual(7, intField.Read(), "invalid input leaves the value alone");
            Check(!intField.TryApplyText(null), "null input is rejected");
            intField.TryApplyText("9999");
            CheckEqual(10, intField.Read(), "typed out-of-range int is clamped");

            var invariant = 1.5.ToString(CultureInfo.InvariantCulture);
            Check(floatField.TryApplyText(invariant), "invariant decimal separator is accepted");

            // ---- display formatting ------------------------------------------
            CheckEqual("6", Number(typeof(int), 6, true, null, null, 1).CurrentText(), "ints show without decimals");
            CheckEqual("0.75", Number(typeof(float), 0.75f, false, null, null, 0.1).CurrentText(), "floats show three decimals");
            CheckEqual("12.35", Number(typeof(float), 12.345f, false, null, null, 0.1).CurrentText(), "large floats show two decimals");
            CheckEqual("1235", Number(typeof(float), 1234.6f, false, null, null, 0.1).CurrentText(), "x >= 1000 shows no decimals");

            // ---- choices -----------------------------------------------------
            var choices = new object[] { TestMode.Alpha, TestMode.Beta, TestMode.Gamma };
            var choiceBox = (object)TestMode.Beta;
            var choice = new SettingField
            {
                Kind = SettingKind.Choice,
                ValueType = typeof(TestMode),
                Choices = choices,
                Read = () => choiceBox,
                Write = value => choiceBox = value
            };

            CheckEqual("Beta", choice.CurrentText(), "enum shows its name");
            choice.Cycle(1);
            CheckEqual(TestMode.Gamma, choice.Read(), "choice cycles forward");
            choice.Cycle(1);
            CheckEqual(TestMode.Alpha, choice.Read(), "choice cycling wraps forward");
            choice.Cycle(-1);
            CheckEqual(TestMode.Gamma, choice.Read(), "choice cycles backwards");

            Check(choice.TryApplyText("beta"), "choice accepts a typed name");
            CheckEqual(TestMode.Beta, choice.Read(), "typed choice is applied");
            Check(!choice.TryApplyText("Delta"), "unknown choice name is rejected");

            var single = new SettingField
            {
                Kind = SettingKind.Choice,
                Choices = new object[] { 1 },
                Read = () => 1,
                Write = _ => { }
            };
            single.Cycle(1); // must not divide by zero
            CheckEqual(1, single.Read(), "a single-choice field is stable");

            // ---- toggles and text -------------------------------------------
            var boolBox = (object)true;
            var toggle = new SettingField
            {
                Kind = SettingKind.Toggle,
                Read = () => boolBox,
                Write = value => boolBox = value
            };
            CheckEqual("On", toggle.CurrentText(), "toggle shows On");
            boolBox = false;
            CheckEqual("Off", toggle.CurrentText(), "toggle shows Off");
            Check(!toggle.TryApplyText("true"), "toggles are not edited as text");

            var textBox = (object)"hello";
            var text = new SettingField
            {
                Kind = SettingKind.Text,
                Read = () => textBox,
                Write = value => textBox = value
            };
            Check(text.TryApplyText("bye"), "text accepts input");
            CheckEqual("bye", text.Read(), "text writes back");
            Check(text.TryApplyText(null), "text accepts empty input");
            CheckEqual(string.Empty, text.Read(), "null text becomes an empty string");
            CheckEqual("-", text.CurrentText(), "empty text shows a dash");

            // ---- read-only ---------------------------------------------------
            var lockedBox = (object)3;
            var locked = new SettingField
            {
                Kind = SettingKind.Number,
                IsInteger = true,
                Read = () => lockedBox,
                Write = value => lockedBox = value,
                ReadOnly = true
            };
            locked.StepBy(1);
            CheckEqual(3, locked.Read(), "read-only ignores stepping");
            Check(!locked.TryApplyText("4"), "read-only rejects typed values");
            Check(!locked.HasArrows, "read-only shows no arrows");

            // ---- step rounding ------------------------------------------------
            CheckEqual(0.02, SettingValues.NiceStep(0.02), "NiceStep keeps an exact 1/2/5 step");
            CheckEqual(0.02, SettingValues.NiceStep(0.015), "NiceStep rounds up to 0.02");
            CheckEqual(0.05, SettingValues.NiceStep(0.03), "NiceStep rounds up to 0.05");
            CheckEqual(0.1, SettingValues.NiceStep(0.06), "NiceStep rounds up to 0.1");
            CheckEqual(0.2, SettingValues.NiceStep(0.11), "NiceStep rounds up to 0.2");
            CheckEqual(1.0, SettingValues.NiceStep(0.9), "NiceStep rounds up to 1");
            CheckEqual(0.1, SettingValues.NiceStep(0), "NiceStep falls back for a zero step");
            CheckEqual(0.1, SettingValues.NiceStep(double.NaN), "NiceStep falls back for NaN");

            // ---- type mapping -------------------------------------------------
            CheckEqual(2, (int)SettingValues.ConvertNumber(2.4, typeof(int)), "int conversion rounds");
            CheckEqual(3, (int)SettingValues.ConvertNumber(2.6, typeof(int)), "int conversion rounds up");
            CheckEqual(2L, (long)SettingValues.ConvertNumber(2.4, typeof(long)), "long conversion works");
            CheckEqual((byte)7, (byte)SettingValues.ConvertNumber(7.2, typeof(byte)), "byte conversion works");
            CheckEqual(1.5f, (float)SettingValues.ConvertNumber(1.5, typeof(float)), "float conversion works");
            CheckEqual(1.5d, (double)SettingValues.ConvertNumber(1.5, typeof(double)), "double conversion works");
            CheckEqual(1.5m, (decimal)SettingValues.ConvertNumber(1.5, typeof(decimal)), "decimal conversion works");

            Check(SettingValues.IsNumeric(typeof(int)), "int is numeric");
            Check(SettingValues.IsNumeric(typeof(float)), "float is numeric");
            Check(!SettingValues.IsNumeric(typeof(string)), "string is not numeric");
            Check(SettingValues.IsInteger(typeof(int)), "int is an integer type");
            Check(!SettingValues.IsInteger(typeof(float)), "float is not an integer type");

            Console.WriteLine();
            CheckClickZones();

            Console.WriteLine();
            Console.WriteLine((_checks - _failures) + "/" + _checks + " checks passed.");
            return _failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// The click-zone rules. The important guarantee is that clicking the load/unload check box
        /// can never open the options screen, so the toggle strip is checked exhaustively.
        /// </summary>
        private static void CheckClickZones()
        {
            const int rowWidth = 498;
            var toggleEnd = 34.0 / rowWidth;
            var modZones = RowZones.ModRow(toggleEnd);

            Check(Math.Abs(toggleEnd - 0.0683) < 0.001, "the toggle strip is ~7% of a 498-unit row");
            CheckEqual(RowAction.Toggle, RowHitTest.ResolveModRow(0.0, modZones, true), "click at the very left toggles");
            CheckEqual(RowAction.Toggle, RowHitTest.ResolveModRow(toggleEnd, modZones, true), "the toggle strip's right edge still toggles");
            CheckEqual(RowAction.Toggle, RowHitTest.ResolveModRow(toggleEnd - 0.0001, modZones, true), "just inside the strip toggles");
            CheckEqual(RowAction.Open, RowHitTest.ResolveModRow(toggleEnd + 0.0001, modZones, true), "just outside the strip opens options");
            CheckEqual(RowAction.Open, RowHitTest.ResolveModRow(0.5, modZones, true), "the middle of the row opens options");
            CheckEqual(RowAction.Open, RowHitTest.ResolveModRow(1.0, modZones, true), "the right edge opens options");
            CheckEqual(RowAction.Toggle, RowHitTest.ResolveModRow(0.5, modZones, false), "a mod without options still toggles");
            CheckEqual(RowAction.Ignore, RowHitTest.ResolveModRow(-0.2, modZones, true), "a click left of the row is ignored");
            CheckEqual(RowAction.Ignore, RowHitTest.ResolveModRow(1.2, modZones, true), "a click right of the row is ignored");
            CheckEqual(RowAction.Ignore, RowHitTest.ResolveModRow(double.NaN, modZones, true), "an unknown position is ignored");

            // Exhaustive: no position inside the toggle strip may ever open the options.
            var openedInStrip = 0;
            for (var i = 0; i <= 2000; i++)
            {
                var t = toggleEnd * i / 2000.0;
                if (RowHitTest.ResolveModRow(t, modZones, true) != RowAction.Toggle) openedInStrip++;
            }
            CheckEqual(0, openedInStrip, "no point inside the toggle strip opens the options (2001 samples)");

            // Settings rows: 498 units wide, arrows at 43 and 21 from the right, value 150 wide.
            const double width = 498.0;
            var arrowsStart = 1.0 - 52.0 / width;
            var arrowsMiddle = 1.0 - 32.0 / width;
            var valueStart = arrowsStart - 150.0 / width;
            var zones = new RowZones(34.0 / width, arrowsStart, valueStart, arrowsMiddle);

            CheckEqual(RowAction.Decrease, RowHitTest.ResolveSettingRow(arrowsStart, zones, SettingKind.Number, false), "the left arrow steps down");
            CheckEqual(RowAction.Decrease, RowHitTest.ResolveSettingRow(arrowsMiddle - 0.0001, zones, SettingKind.Number, false), "the left of the gap steps down");
            CheckEqual(RowAction.Increase, RowHitTest.ResolveSettingRow(arrowsMiddle, zones, SettingKind.Number, false), "the right of the gap steps up");
            CheckEqual(RowAction.Increase, RowHitTest.ResolveSettingRow(1.0, zones, SettingKind.Choice, false), "the right arrow cycles forward");
            CheckEqual(RowAction.Open, RowHitTest.ResolveSettingRow(0.5, zones, SettingKind.Number, false), "the row body steps a number");
            CheckEqual(RowAction.Open, RowHitTest.ResolveSettingRow(valueStart, zones, SettingKind.Number, false), "the value column starts editing");
            CheckEqual(RowAction.Open, RowHitTest.ResolveSettingRow(valueStart, zones, SettingKind.Text, false), "the value column starts typing");
            CheckEqual(RowAction.Toggle, RowHitTest.ResolveSettingRow(0.01, zones, SettingKind.Toggle, false), "the check box flips a boolean");
            CheckEqual(RowAction.Open, RowHitTest.ResolveSettingRow(0.01, zones, SettingKind.Number, false), "the left edge of a number row is not a toggle");
            CheckEqual(RowAction.Ignore, RowHitTest.ResolveSettingRow(0.5, zones, SettingKind.Number, true), "read-only rows ignore clicks");
            CheckEqual(RowAction.Ignore, RowHitTest.ResolveSettingRow(arrowsStart, zones, SettingKind.Number, true), "read-only arrows are inert");
            CheckEqual(RowAction.Ignore, RowHitTest.ResolveSettingRow(-0.5, zones, SettingKind.Number, false), "clicks outside the row are ignored");

            var arrowLeak = 0;
            for (var i = 0; i <= 1000; i++)
            {
                var t = arrowsStart + (1.0 - arrowsStart) * i / 1000.0;
                var action = RowHitTest.ResolveSettingRow(t, zones, SettingKind.Number, false);
                if (action != RowAction.Decrease && action != RowAction.Increase) arrowLeak++;
            }
            CheckEqual(0, arrowLeak, "every point in the arrow zone steps the value (1001 samples)");
        }
    }
}

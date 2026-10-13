using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace CouchTV
{
    /// <summary>A text box with the keyboard focus: what the phone needs to pop up its keyboard for it.</summary>
    internal sealed class TypingField
    {
        public bool Password;
        public string Name = "";    // what it's called, e.g. "Search"
        public string Value = "";   // what's in it now: never a password's, and empty when it's too long to show
    }

    /// <summary>
    /// Watches which control has the keyboard focus, in any app (UI Automation, Windows' accessibility interface), and
    /// says when it's a text box you can type into, so the phone remote pops up its keyboard. Only runs while asked (a
    /// phone is connected): watching makes browsers build their accessibility trees, which costs them a little.
    /// CouchTV's own windows are left out (its search has its own box on the phone).
    /// </summary>
    internal sealed class TypingWatch
    {
        const int OffAfterMs = 400;   // focus passes through other things between text boxes: don't flicker
        const int MostValue = 500;

        /// <summary>The focused text box, or null when there's none. On a background thread.</summary>
        public event Action<TypingField> Changed;

        readonly object _lock = new object(), _applying = new object();
        readonly int _ownProcess = Process.GetCurrentProcess().Id;
        readonly Timer _off;
        AutomationFocusChangedEventHandler _handler;
        bool _wanted, _added, _reportedField;
        int[] _lastElement;
        DateTime _lastReport;

        public TypingWatch()
        {
            _off = new Timer(state => Report(null), null, Timeout.Infinite, Timeout.Infinite);
            _handler = (sender, e) => Check(sender as AutomationElement);
        }

        public void Start() { Want(true); }

        public void Stop() { Want(false); }

        // Adding and removing the handler can take a moment (and mustn't run on the UI thread, whose windows UI
        // Automation may need to ask): done in the background, always ending at the latest wish.
        void Want(bool on)
        {
            lock (_lock)
            {
                if (_wanted == on) return;
                _wanted = on;
            }
            ThreadPool.QueueUserWorkItem(state =>
            {
                lock (_applying)
                {
                    bool want;
                    lock (_lock) want = _wanted;
                    try
                    {
                        if (want && !_added)
                        {
                            Automation.AddAutomationFocusChangedEventHandler(_handler);
                            _added = true;
                            Log.Info("Typing on the phone: watching for text boxes");
                            Check(AutomationElement.FocusedElement);
                        }
                        else if (!want && _added)
                        {
                            Automation.RemoveAutomationFocusChangedEventHandler(_handler);
                            _added = false;
                            _off.Change(Timeout.Infinite, Timeout.Infinite);
                            Report(null);
                        }
                    }
                    catch (Exception ex) { Log.Info("Typing on the phone: " + ex.Message); }
                }
            });
        }

        void Check(AutomationElement element)
        {
            TypingField field = null;
            int[] id = null;
            try
            {
                field = Describe(element);
                if (field != null) id = element.GetRuntimeId();
            }
            catch (ElementNotAvailableException) { }   // it went away meanwhile
            catch (Exception ex) { Log.Info("Typing on the phone: " + ex.Message); }
            if (field == null)
            {
                _off.Change(OffAfterMs, Timeout.Infinite);
                return;
            }
            _off.Change(Timeout.Infinite, Timeout.Infinite);
            lock (_lock)
            {
                // Browsers sometimes say the same thing twice in a row.
                if (_reportedField && Same(id, _lastElement) && (DateTime.UtcNow - _lastReport).TotalMilliseconds < 500) return;
                _lastElement = id;
            }
            Report(field);
        }

        /// <summary>A text box you can type into, or null: an edit box, an editable combo box (a search box with suggestions), or an editable page.</summary>
        internal TypingField Describe(AutomationElement element)
        {
            if (element == null) return null;
            AutomationElement.AutomationElementInformation info = element.Current;
            if (info.ProcessId == _ownProcess) return null;
            ControlType type = info.ControlType;
            if (type != ControlType.Edit && type != ControlType.ComboBox && type != ControlType.Document) return null;
            bool password = info.IsPassword;
            string value = "";
            object pattern;
            if (element.TryGetCurrentPattern(ValuePattern.Pattern, out pattern))
            {
                var v = (ValuePattern)pattern;
                if (v.Current.IsReadOnly) return null;
                if (!password)   // a password box won't say (and mustn't)
                {
                    try { value = v.Current.Value ?? ""; }
                    catch (InvalidOperationException) { }
                }
            }
            else if (type != ControlType.Edit) return null;
            return new TypingField { Password = password, Name = Tidy(info.Name, 60), Value = value.Length > MostValue ? "" : value };
        }

        void Report(TypingField field)
        {
            lock (_lock)
            {
                if (field == null && !_reportedField) return;
                _reportedField = field != null;
                _lastReport = DateTime.UtcNow;
                if (field == null) _lastElement = null;
            }
            Action<TypingField> handler = Changed;
            if (handler != null) handler(field);
        }

        static bool Same(int[] a, int[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static string Tidy(string text, int most)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var tidy = new StringBuilder();
            foreach (char c in text) tidy.Append(char.IsControl(c) ? ' ' : c);
            string result = tidy.ToString().Trim();
            return result.Length > most ? result.Substring(0, most) : result;
        }
    }
}

using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace CouchTV
{
    /// <summary>
    /// The web page in front, as Windows' accessibility sees it (each element's type, HTML class, id, text and place):
    /// for working out a site's layout on the TV itself when it changes (Netflix's, for the websites' touches in Sites.cs).
    /// Asked for by a paired phone or the test tool ("INSPECT" in ir-receiver/PROTOCOL.md). Slow: call off the UI thread.
    /// </summary>
    internal static class PageInspect
    {
        static readonly Condition Documents = new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Document);

        /// <summary>The page's elements, one a line, indented by depth; from the skip'th, at most max of them.</summary>
        public static string Dump(IntPtr hwnd, int max, int skip)
        {
            var text = new StringBuilder();
            try
            {
                AutomationElement window = AutomationElement.FromHandle(hwnd);
                text.AppendLine("Window: " + window.Current.Name);
                AutomationElement page = null;
                for (int look = 0; look < 6 && (page == null || TreeWalker.RawViewWalker.GetFirstChild(page) == null); look++)
                {
                    if (look > 0) Thread.Sleep(900);   // the browser's accessibility, just woken
                    page = window.FindFirst(TreeScope.Descendants, Documents);
                }
                if (page == null) return text.Append("No web page in it").ToString();
                int seen = 0;
                Walk(page, 0, text, max, skip, ref seen);
                text.AppendLine("(" + seen + " elements" + (seen >= skip + max ? ", more after these" : "") + ")");
            }
            catch (Exception ex) { text.AppendLine("Failed: " + ex.Message); }
            return text.ToString();
        }

        static void Walk(AutomationElement e, int depth, StringBuilder text, int max, int skip, ref int seen)
        {
            if (seen >= skip + max || depth > 60) return;
            if (seen++ >= skip)
            {
                try
                {
                    AutomationElement.AutomationElementInformation c = e.Current;
                    System.Windows.Rect r = c.BoundingRectangle;
                    string name = (c.Name ?? "").Replace('\n', ' ');
                    if (name.Length > 60) name = name.Substring(0, 60) + "…";
                    text.Append(' ', Math.Min(depth, 40)).Append(c.ControlType.ProgrammaticName.Replace("ControlType.", ""));
                    if (!string.IsNullOrEmpty(c.ClassName)) text.Append(" .").Append(c.ClassName.Trim().Replace(' ', '.'));
                    if (!string.IsNullOrEmpty(c.AutomationId)) text.Append(" #").Append(c.AutomationId);
                    if (name.Length > 0) text.Append(" \"").Append(name).Append('"');
                    text.Append(r.IsEmpty ? " off" : " " + (int)r.X + "," + (int)r.Y + " " + (int)r.Width + "x" + (int)r.Height).AppendLine();
                }
                catch (ElementNotAvailableException) { text.AppendLine("(gone)"); }
            }
            for (AutomationElement child = TreeWalker.RawViewWalker.GetFirstChild(e); child != null && seen < skip + max;
                 child = TreeWalker.RawViewWalker.GetNextSibling(child))
            {
                Walk(child, depth + 1, text, max, skip, ref seen);
            }
        }

        /// <summary>The dump made small for one line to a phone: gzipped, then base64.</summary>
        public static string Pack(string dump)
        {
            using (var memory = new MemoryStream())
            {
                using (var zip = new GZipStream(memory, CompressionMode.Compress))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(dump);
                    zip.Write(bytes, 0, bytes.Length);
                }
                return Convert.ToBase64String(memory.ToArray());
            }
        }
    }
}

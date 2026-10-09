// Port of d8's PrintMessageCallback (src/d8/d8.cc) for the message levels
// other than errors: warnings, info, debug and log messages (the asm.js
// validation and linking messages) print as "file:line: message".
using V8Sharp.Objects;

namespace V8Sharp.D8;

public static class D8MessageListener
{
    /// <summary>
    /// Shell::PrintMessageCallback for non-error levels, writing through
    /// <paramref name="write"/> (d8's printf).
    /// </summary>
    public static Action<Isolate, JSMessageObject> Create(Action<string> write) => (isolate, message) =>
    {
        string msg = MessageHandler.GetLocalizedMessage(isolate, message);
        string filename = message.Script.Name.HeapObjectOrNull is JSString name ? name.ToString() : "undefined";
        int linenum = message.GetLineNumber();
        write(filename + ":" + linenum.ToString(System.Globalization.CultureInfo.InvariantCulture) + ": " + msg + "\n");
    };
}

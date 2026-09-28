// Port of Runtime_NewClosure / Runtime_NewClosure_Tenured
// (src/runtime/runtime-scopes.cc) and the FastNewClosure builtin: a
// JSFunction for a SharedFunctionInfo in the current context, sharing the
// closure's feedback cell.
namespace V8Sharp.Runtime;

public static class RuntimeClosures
{
    /// <summary>Factory::JSFunctionBuilder{isolate, shared, context}.set_feedback_cell(cell).Build().</summary>
    public static JSFunction NewClosure(Isolate isolate, SharedFunctionInfo shared, Context context, FeedbackCell feedbackCell) =>
        isolate.Factory.NewFunction(shared, context, null, feedbackCell);
}

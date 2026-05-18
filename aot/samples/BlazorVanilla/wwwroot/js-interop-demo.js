// Tiny ES module imported by JsInteropDemo.razor via
// `await JS.InvokeAsync<IJSObjectReference>("import", ...)`.
// Demonstrates a string-in/string-out round trip.

export function greet(name) {
    return `Hello, ${name}! (from js-interop-demo.js at ${new Date().toISOString()})`;
}

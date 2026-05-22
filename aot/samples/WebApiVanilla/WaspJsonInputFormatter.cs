using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.Net.Http.Headers;

namespace WaspSample.WebApiVanilla;

// gh #96 (M4.S9.5b) — drop-in replacement for the framework
// SystemTextJsonInputFormatter. The framework formatter calls
// `JsonSerializer.DeserializeAsync(PipeReader, Type, JsonSerializerOptions,
// CancellationToken)` which NativeAOT-LLVM wasm32-wasi trims away. We
// route through `DeserializeAsync(Stream, ...)` which stays reachable
// under the trim profile.
//
// Wired in Program.cs via:
//   builder.Services.Configure<MvcOptions>(o => {
//       var stj = o.InputFormatters.OfType<SystemTextJsonInputFormatter>().FirstOrDefault();
//       if (stj is not null) o.InputFormatters.Remove(stj);
//       o.InputFormatters.Insert(0, new WaspJsonInputFormatter(jsonOpts));
//   });
internal sealed class WaspJsonInputFormatter : TextInputFormatter
{
    private readonly JsonSerializerOptions _options;

    public WaspJsonInputFormatter(JsonSerializerOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        SupportedEncodings.Add(UTF8EncodingWithoutBOM);
        SupportedEncodings.Add(UTF16EncodingLittleEndian);

        SupportedMediaTypes.Add(new MediaTypeHeaderValue("application/json"));
        SupportedMediaTypes.Add(new MediaTypeHeaderValue("text/json"));
        SupportedMediaTypes.Add(new MediaTypeHeaderValue("application/*+json"));
    }

    public override async Task<InputFormatterResult> ReadRequestBodyAsync(
        InputFormatterContext context, Encoding encoding)
    {
        if (context is null) throw new ArgumentNullException(nameof(context));
        if (encoding is null) throw new ArgumentNullException(nameof(encoding));

        var stream = context.HttpContext.Request.Body;

        // The framework version transcodes to UTF-8 if needed; we accept
        // only UTF-8 here (the SupportedEncodings declare more but stock
        // canister requests are always UTF-8 JSON). If a non-UTF-8 body
        // ever lands, this will throw a JsonException with the right
        // diagnostic.

        try
        {
            var model = await JsonSerializer.DeserializeAsync(
                stream, context.ModelType, _options, context.HttpContext.RequestAborted);

            if (model is null && !context.TreatEmptyInputAsDefaultValue)
            {
                return InputFormatterResult.NoValue();
            }

            return InputFormatterResult.Success(model);
        }
        catch (JsonException ex)
        {
            context.ModelState.AddModelError(context.ModelName, ex.Message);
            return InputFormatterResult.Failure();
        }
        catch (Exception ex) when (ex is FormatException || ex is OverflowException)
        {
            context.ModelState.AddModelError(context.ModelName, ex.Message);
            return InputFormatterResult.Failure();
        }
    }
}

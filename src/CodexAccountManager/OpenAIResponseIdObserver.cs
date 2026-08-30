using System.Text;
using System.Text.Json;

namespace CodexAccountManager;

internal enum OpenAIResponseWireFormat
{
    Json,
    ServerSentEvents
}

internal sealed record OpenAIResponseIdObservation(
    string? ResponseId,
    bool SawTerminalSuccess,
    bool SawTerminalFailure,
    bool IsAmbiguous,
    bool IsComplete,
    bool CanConfirm,
    bool CanConfirmSession);

/// <summary>
/// Incrementally observes a Responses API JSON or SSE body while the gateway copies
/// the original bytes downstream. It retains only bounded parser state, accepts only
/// response identifiers shaped as resp_*, and never confirms a stream containing a
/// failure terminal.
/// </summary>
internal sealed class OpenAIResponseIdObserver
{
    private const int MaximumSseLineBytes = 256 * 1024;
    private const int MaximumSseEventDataBytes = 256 * 1024;
    private const int MaximumJsonCarryBytes = 256 * 1024;

    private readonly OpenAIResponseWireFormat _format;
    private bool _completed;
    private bool _invalid;
    private bool _ambiguous;
    private bool _sawTerminalSuccess;
    private bool _sawTerminalFailure;
    private string? _responseId;
    private OpenAIResponseIdObservation? _result;

    // JSON incremental parser state.
    private JsonReaderState _jsonState;
    private byte[] _jsonCarry = [];
    private readonly List<JsonScope> _jsonScopes = [];
    private JsonProperty _pendingJsonProperty;
    private bool _jsonRootSeen;
    private bool _jsonRootCompleted;

    // SSE framing state. Lines and event data are bounded independently so a hostile
    // upstream cannot turn observation into unbounded buffering.
    private byte[] _sseLine = new byte[1024];
    private int _sseLineLength;
    private bool _sseLineOverflow;
    private byte[] _sseEventData = new byte[4096];
    private int _sseEventDataLength;
    private bool _sseEventDataOverflow;
    private string? _sseEventType;

    internal OpenAIResponseIdObserver(OpenAIResponseWireFormat format)
    {
        _format = format;
    }

    internal OpenAIResponseIdObserver(string? contentType)
        : this(contentType?.Contains(
                   "text/event-stream",
                   StringComparison.OrdinalIgnoreCase) == true
            ? OpenAIResponseWireFormat.ServerSentEvents
            : OpenAIResponseWireFormat.Json)
    {
    }

    internal OpenAIResponseWireFormat Format => _format;

    /// <summary>
    /// Feeds one upstream chunk into the observer. Malformed or oversized observation
    /// state disables confirmation but never throws into the response-copying path.
    /// </summary>
    internal void Observe(ReadOnlySpan<byte> bytes)
    {
        if (_completed || bytes.IsEmpty || _invalid)
        {
            return;
        }
        try
        {
            if (_format == OpenAIResponseWireFormat.ServerSentEvents)
            {
                ObserveSse(bytes);
            }
            else
            {
                ProcessJson(bytes, isFinalBlock: false);
            }
        }
        catch (Exception ex) when (
            ex is JsonException or DecoderFallbackException or
            ArgumentException or InvalidOperationException or OverflowException)
        {
            _invalid = true;
        }
    }

    /// <summary>
    /// Finalizes parser state after upstream EOF. The operation is idempotent.
    /// SSE requires a completed/done terminal; a complete non-failure JSON object with
    /// a valid id is confirmable even when its optional status field is absent.
    /// </summary>
    internal OpenAIResponseIdObservation Complete()
    {
        if (_result != null)
        {
            return _result;
        }

        if (!_completed)
        {
            try
            {
                if (_format == OpenAIResponseWireFormat.ServerSentEvents)
                {
                    CompleteSse();
                }
                else if (!_invalid)
                {
                    ProcessJson([], isFinalBlock: true);
                }
            }
            catch (Exception ex) when (
                ex is JsonException or DecoderFallbackException or
                ArgumentException or InvalidOperationException or OverflowException)
            {
                _invalid = true;
            }
            _completed = true;
        }

        var structurallyComplete = _format == OpenAIResponseWireFormat.ServerSentEvents
            ? !_invalid
            : !_invalid && _jsonRootSeen && _jsonRootCompleted && _jsonScopes.Count == 0;
        var canConfirm = structurallyComplete &&
                         !_ambiguous &&
                         !_sawTerminalFailure &&
                         _responseId != null &&
                         (_format == OpenAIResponseWireFormat.Json || _sawTerminalSuccess);
        var canConfirmSession = structurallyComplete &&
                                !_ambiguous &&
                                !_sawTerminalFailure &&
                                (_format == OpenAIResponseWireFormat.Json || _sawTerminalSuccess);
        _result = new OpenAIResponseIdObservation(
            _responseId,
            _sawTerminalSuccess,
            _sawTerminalFailure,
            _ambiguous,
            structurallyComplete,
            canConfirm,
            canConfirmSession);
        return _result;
    }

    internal static bool IsValidResponseId(string? value)
    {
        if (value is not { Length: >= 6 and <= 256 } ||
            !value.StartsWith("resp_", StringComparison.Ordinal))
        {
            return false;
        }
        return value.AsSpan(5).IndexOfAnyExcept(
            "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-".AsSpan()) < 0;
    }

    private void ProcessJson(ReadOnlySpan<byte> bytes, bool isFinalBlock)
    {
        var totalLength = checked(_jsonCarry.Length + bytes.Length);
        byte[] buffer;
        if (_jsonCarry.Length == 0)
        {
            buffer = bytes.ToArray();
        }
        else
        {
            buffer = new byte[totalLength];
            _jsonCarry.CopyTo(buffer, 0);
            bytes.CopyTo(buffer.AsSpan(_jsonCarry.Length));
        }

        var reader = new Utf8JsonReader(buffer, isFinalBlock, _jsonState);
        while (reader.Read())
        {
            ObserveJsonToken(ref reader);
        }
        var consumed = checked((int)reader.BytesConsumed);
        _jsonState = reader.CurrentState;
        var remaining = buffer.Length - consumed;
        if (remaining > MaximumJsonCarryBytes)
        {
            _invalid = true;
            _jsonCarry = [];
            return;
        }
        _jsonCarry = remaining == 0
            ? []
            : buffer.AsSpan(consumed, remaining).ToArray();

        if (!isFinalBlock)
        {
            return;
        }
        if (_jsonCarry.Length != 0 || _jsonScopes.Count != 0 || !_jsonRootCompleted)
        {
            _invalid = true;
        }
    }

    private void ObserveJsonToken(ref Utf8JsonReader reader)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.StartObject:
            {
                var isRoot = _jsonScopes.Count == 0;
                var isResponse = _jsonScopes.Count == 1 &&
                                 _jsonScopes[0].IsRoot &&
                                 _pendingJsonProperty == JsonProperty.Response;
                _jsonScopes.Add(new JsonScope(IsObject: true, isRoot, isResponse));
                _pendingJsonProperty = JsonProperty.None;
                if (isRoot)
                {
                    _jsonRootSeen = true;
                }
                break;
            }
            case JsonTokenType.StartArray:
            {
                var isRoot = _jsonScopes.Count == 0;
                _jsonScopes.Add(new JsonScope(IsObject: false, isRoot, IsResponse: false));
                _pendingJsonProperty = JsonProperty.None;
                if (isRoot)
                {
                    _jsonRootSeen = true;
                }
                break;
            }
            case JsonTokenType.EndObject:
            case JsonTokenType.EndArray:
            {
                if (_jsonScopes.Count == 0)
                {
                    _invalid = true;
                    return;
                }
                var ended = _jsonScopes[^1];
                _jsonScopes.RemoveAt(_jsonScopes.Count - 1);
                _pendingJsonProperty = JsonProperty.None;
                if (ended.IsRoot)
                {
                    _jsonRootCompleted = true;
                }
                break;
            }
            case JsonTokenType.PropertyName:
                _pendingJsonProperty = ClassifyJsonProperty(ref reader);
                break;
            case JsonTokenType.String:
                ObserveJsonStringValue(ref reader);
                _pendingJsonProperty = JsonProperty.None;
                break;
            case JsonTokenType.Number:
            case JsonTokenType.True:
            case JsonTokenType.False:
            case JsonTokenType.Null:
                _pendingJsonProperty = JsonProperty.None;
                break;
        }
    }

    private JsonProperty ClassifyJsonProperty(ref Utf8JsonReader reader)
    {
        if (_jsonScopes.Count == 0 || !_jsonScopes[^1].IsObject)
        {
            return JsonProperty.Other;
        }
        var scope = _jsonScopes[^1];
        if (scope.IsRoot && reader.ValueTextEquals("response"u8))
        {
            return JsonProperty.Response;
        }
        if ((scope.IsRoot || scope.IsResponse) && reader.ValueTextEquals("id"u8))
        {
            return JsonProperty.Id;
        }
        if ((scope.IsRoot || scope.IsResponse) && reader.ValueTextEquals("status"u8))
        {
            return JsonProperty.Status;
        }
        if (scope.IsRoot && reader.ValueTextEquals("type"u8))
        {
            return JsonProperty.Type;
        }
        return JsonProperty.Other;
    }

    private void ObserveJsonStringValue(ref Utf8JsonReader reader)
    {
        var value = reader.GetString();
        switch (_pendingJsonProperty)
        {
            case JsonProperty.Id:
                ConsiderResponseId(value);
                break;
            case JsonProperty.Type:
                ClassifyEventType(value);
                break;
            case JsonProperty.Status:
                ClassifyResponseStatus(value);
                break;
        }
    }

    private void ObserveSse(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)'\n');
            if (newline < 0)
            {
                AppendSseLine(bytes);
                return;
            }
            AppendSseLine(bytes[..newline]);
            DispatchSseLine();
            bytes = bytes[(newline + 1)..];
        }
    }

    private void AppendSseLine(ReadOnlySpan<byte> bytes)
    {
        if (_sseLineOverflow || bytes.IsEmpty)
        {
            return;
        }
        if (_sseLineLength + bytes.Length > MaximumSseLineBytes)
        {
            _sseLineOverflow = true;
            _sseLineLength = 0;
            _invalid = true;
            return;
        }
        EnsureBufferCapacity(ref _sseLine, _sseLineLength + bytes.Length, MaximumSseLineBytes);
        bytes.CopyTo(_sseLine.AsSpan(_sseLineLength));
        _sseLineLength += bytes.Length;
    }

    private void DispatchSseLine()
    {
        if (_sseLineOverflow)
        {
            _sseLineOverflow = false;
            _sseLineLength = 0;
            _sseEventDataOverflow = true;
            return;
        }

        var length = _sseLineLength;
        if (length > 0 && _sseLine[length - 1] == (byte)'\r')
        {
            length--;
        }
        var line = _sseLine.AsSpan(0, length);
        _sseLineLength = 0;
        if (line.IsEmpty)
        {
            DispatchSseEvent();
            return;
        }
        if (line[0] == (byte)':')
        {
            return;
        }

        var colon = line.IndexOf((byte)':');
        var name = colon < 0 ? line : line[..colon];
        var value = colon < 0 ? ReadOnlySpan<byte>.Empty : line[(colon + 1)..];
        if (!value.IsEmpty && value[0] == (byte)' ')
        {
            value = value[1..];
        }
        if (name.SequenceEqual("event"u8))
        {
            _sseEventType = value.Length <= 128
                ? Encoding.UTF8.GetString(value).Trim()
                : null;
        }
        else if (name.SequenceEqual("data"u8))
        {
            AppendSseEventData(value);
        }
    }

    private void AppendSseEventData(ReadOnlySpan<byte> value)
    {
        if (_sseEventDataOverflow)
        {
            return;
        }
        var separatorLength = _sseEventDataLength == 0 ? 0 : 1;
        if (_sseEventDataLength + separatorLength + value.Length > MaximumSseEventDataBytes)
        {
            _sseEventDataOverflow = true;
            _sseEventDataLength = 0;
            _invalid = true;
            return;
        }
        var required = _sseEventDataLength + separatorLength + value.Length;
        EnsureBufferCapacity(ref _sseEventData, required, MaximumSseEventDataBytes);
        if (separatorLength != 0)
        {
            _sseEventData[_sseEventDataLength++] = (byte)'\n';
        }
        value.CopyTo(_sseEventData.AsSpan(_sseEventDataLength));
        _sseEventDataLength += value.Length;
    }

    private void DispatchSseEvent()
    {
        var eventType = _sseEventType;
        // The SSE framing is authoritative evidence too. Classify it before looking
        // at the payload so a failure frame cannot be hidden by a later [DONE] marker
        // or by a contradictory JSON `type` field.
        ClassifyEventType(eventType);
        if (_sseEventDataOverflow)
        {
            _invalid = true;
        }
        else if (_sseEventDataLength > 0)
        {
            var data = _sseEventData.AsMemory(0, _sseEventDataLength);
            if (data.Span.SequenceEqual("[DONE]"u8))
            {
                // Some trusted OpenAI-compatible endpoints use the legacy [DONE]
                // marker instead of a response.completed event. It is safe to treat
                // that marker as success only after this stream has established one
                // unambiguous Responses id and has not reported a terminal failure.
                // A bare [DONE] never creates a response-id binding; it may still confirm
                // the ordinary session route because the stream reached its terminal.
                if (!_ambiguous && !_sawTerminalFailure)
                {
                    // A legacy [DONE] frame proves that the stream reached its normal
                    // terminal even when the endpoint omitted a Responses id. It is
                    // sufficient to refresh/confirm the ordinary session binding, but
                    // CanConfirm remains false without a concrete response id.
                    _sawTerminalSuccess = true;
                }
            }
            else
            {
                try
                {
                    using var document = JsonDocument.Parse(data);
                    ObserveSseJson(document.RootElement, eventType);
                }
                catch (JsonException)
                {
                    // The bytes have already been proxied, but a malformed event makes
                    // the observed response incomplete. In particular, never let an
                    // earlier id plus `event: response.completed` create a durable binding.
                    _invalid = true;
                }
            }
        }
        else if (IsTerminalEventType(eventType))
        {
            // A terminal frame without data is not enough evidence to bind a response id.
            _invalid = true;
        }
        _sseEventType = null;
        _sseEventDataLength = 0;
        _sseEventDataOverflow = false;
    }

    private void ObserveSseJson(JsonElement root, string? framedEventType)
    {
        // Keep framed and payload event types independent. A payload must never
        // overwrite a failure classification supplied by the SSE envelope.
        ClassifyEventType(framedEventType);
        if (root.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        if (root.TryGetProperty("type", out var typeElement) &&
            typeElement.ValueKind == JsonValueKind.String)
        {
            ClassifyEventType(typeElement.GetString());
        }

        var response = root;
        if (root.TryGetProperty("response", out var responseElement) &&
            responseElement.ValueKind == JsonValueKind.Object)
        {
            response = responseElement;
        }
        if (response.TryGetProperty("id", out var idElement) &&
            idElement.ValueKind == JsonValueKind.String)
        {
            ConsiderResponseId(idElement.GetString());
        }
        if (response.TryGetProperty("status", out var statusElement) &&
            statusElement.ValueKind == JsonValueKind.String)
        {
            ClassifyResponseStatus(statusElement.GetString());
        }
    }

    private void CompleteSse()
    {
        // A final event is complete only after its blank-line delimiter. Dispatching a
        // half-written terminal at EOF could otherwise confirm an earlier response id.
        if (_sseLineLength > 0 || _sseLineOverflow ||
            _sseEventType != null || _sseEventDataLength > 0 || _sseEventDataOverflow)
        {
            _invalid = true;
        }
    }

    private void ConsiderResponseId(string? candidate)
    {
        if (!IsValidResponseId(candidate))
        {
            return;
        }
        if (_responseId == null)
        {
            _responseId = candidate;
        }
        else if (!_responseId.Equals(candidate, StringComparison.Ordinal))
        {
            _ambiguous = true;
        }
    }

    private void ClassifyEventType(string? value)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "response.completed":
            case "response.done":
                _sawTerminalSuccess = true;
                break;
            case "response.failed":
            case "response.cancelled":
            case "response.canceled":
            case "error":
                _sawTerminalFailure = true;
                break;
        }
    }

    private static bool IsTerminalEventType(string? value) =>
        value?.Trim().ToLowerInvariant() is
            "response.completed" or "response.done" or
            "response.failed" or "response.cancelled" or
            "response.canceled" or "error";

    private void ClassifyResponseStatus(string? value)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "completed":
                _sawTerminalSuccess = true;
                break;
            case "failed":
            case "cancelled":
            case "canceled":
                _sawTerminalFailure = true;
                break;
        }
    }

    private static void EnsureBufferCapacity(
        ref byte[] buffer,
        int required,
        int maximum)
    {
        if (required <= buffer.Length)
        {
            return;
        }
        var next = Math.Min(maximum, Math.Max(required, checked(buffer.Length * 2)));
        Array.Resize(ref buffer, next);
    }

    internal static void Validate()
    {
        static OpenAIResponseIdObservation Observe(
            string payload,
            OpenAIResponseWireFormat format,
            int chunkSize)
        {
            var observer = new OpenAIResponseIdObserver(format);
            var bytes = Encoding.UTF8.GetBytes(payload);
            for (var offset = 0; offset < bytes.Length; offset += chunkSize)
            {
                observer.Observe(bytes.AsSpan(offset, Math.Min(chunkSize, bytes.Length - offset)));
            }
            return observer.Complete();
        }

        var json = Observe(
            "{\"id\":\"resp_json_fixture\",\"status\":\"completed\",\"output\":[{\"id\":\"resp_nested_fake\",\"content\":\"\\\"id\\\":\\\"resp_text_fake\\\"\"}]}",
            OpenAIResponseWireFormat.Json,
            chunkSize: 1);
        if (!json.CanConfirm ||
            json.ResponseId != "resp_json_fixture" ||
            json.IsAmbiguous ||
            !json.SawTerminalSuccess)
        {
            throw new InvalidOperationException("Incremental JSON response-id observation failed.");
        }

        var envelope = Observe(
            "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp_envelope_fixture\",\"status\":\"completed\"}}",
            OpenAIResponseWireFormat.Json,
            chunkSize: 7);
        if (!envelope.CanConfirm || envelope.ResponseId != "resp_envelope_fixture")
        {
            throw new InvalidOperationException("JSON response envelope observation failed.");
        }

        var failedJson = Observe(
            "{\"type\":\"response.failed\",\"response\":{\"id\":\"resp_failed_fixture\",\"status\":\"failed\"}}",
            OpenAIResponseWireFormat.Json,
            chunkSize: 3);
        if (failedJson.CanConfirm || !failedJson.SawTerminalFailure)
        {
            throw new InvalidOperationException("Failed JSON response was incorrectly confirmable.");
        }

        var malformed = Observe(
            "{\"id\":\"resp_truncated_fixture\"",
            OpenAIResponseWireFormat.Json,
            chunkSize: 2);
        if (malformed.CanConfirm || malformed.IsComplete)
        {
            throw new InvalidOperationException("Truncated JSON response was incorrectly complete.");
        }

        const string successfulSse =
            "event: response.created\r\n" +
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_sse_fixture\",\"status\":\"in_progress\"}}\r\n\r\n" +
            ": keepalive\r\n\r\n" +
            "event: response.completed\r\n" +
            "data: {\"type\":\"response.completed\",\r\n" +
            "data: \"response\":{\"id\":\"resp_sse_fixture\",\"status\":\"completed\"}}\r\n\r\n";
        var sse = Observe(
            successfulSse,
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 1);
        if (!sse.CanConfirm ||
            sse.ResponseId != "resp_sse_fixture" ||
            !sse.SawTerminalSuccess ||
            sse.SawTerminalFailure)
        {
            throw new InvalidOperationException("Incremental SSE response-id observation failed.");
        }

        const string failedSse =
            "event: response.created\n" +
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_sse_failed\"}}\n\n" +
            "event: response.failed\n" +
            "data: {\"type\":\"response.failed\",\"response\":{\"id\":\"resp_sse_failed\",\"status\":\"failed\"}}\n\n";
        var failed = Observe(
            failedSse,
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 5);
        if (failed.CanConfirm || !failed.SawTerminalFailure)
        {
            throw new InvalidOperationException("Failed SSE response was incorrectly confirmable.");
        }

        var noTerminal = Observe(
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_no_terminal\"}}\n\n",
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 64);
        if (noTerminal.CanConfirm || noTerminal.ResponseId != "resp_no_terminal")
        {
            throw new InvalidOperationException("SSE without a success terminal was confirmed.");
        }

        const string doneTerminatedSse =
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_done_fixture\"}}\n\n" +
            "data: [DONE]\n\n";
        var doneTerminated = Observe(
            doneTerminatedSse,
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 2);
        if (!doneTerminated.CanConfirm ||
            doneTerminated.ResponseId != "resp_done_fixture" ||
            !doneTerminated.SawTerminalSuccess ||
            doneTerminated.SawTerminalFailure)
        {
            throw new InvalidOperationException(
                "A [DONE]-terminated SSE stream with a unique response id was not confirmed.");
        }

        var doneWithoutId = Observe(
            "data: [DONE]\n\n",
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 1);
        if (doneWithoutId.CanConfirm ||
            doneWithoutId.ResponseId != null ||
            !doneWithoutId.SawTerminalSuccess ||
            !doneWithoutId.CanConfirmSession)
        {
            throw new InvalidOperationException(
                "A bare [DONE] marker did not safely confirm the ordinary session binding.");
        }

        const string failureThenDoneSse =
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_failed_done_fixture\"}}\n\n" +
            "data: {\"type\":\"response.failed\",\"response\":{\"id\":\"resp_failed_done_fixture\",\"status\":\"failed\"}}\n\n" +
            "data: [DONE]\n\n";
        var failureThenDone = Observe(
            failureThenDoneSse,
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 3);
        if (failureThenDone.CanConfirm ||
            !failureThenDone.SawTerminalFailure ||
            failureThenDone.SawTerminalSuccess)
        {
            throw new InvalidOperationException(
                "A [DONE] marker overrode an earlier SSE failure terminal.");
        }

        var framedFailureThenDone = Observe(
            "event: error\n" +
            "data: [DONE]\n\n",
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 2);
        if (framedFailureThenDone.CanConfirm ||
            !framedFailureThenDone.SawTerminalFailure ||
            framedFailureThenDone.SawTerminalSuccess)
        {
            throw new InvalidOperationException(
                "An error-framed [DONE] event was incorrectly treated as success.");
        }

        var conflictingFraming = Observe(
            "event: response.failed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_conflicting_frame\"}}\n\n" +
            "data: [DONE]\n\n",
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 5);
        if (conflictingFraming.CanConfirm || !conflictingFraming.SawTerminalFailure)
        {
            throw new InvalidOperationException(
                "A failure SSE envelope was hidden by a contradictory payload type.");
        }

        var framedFailureScalar = Observe(
            "event: error\n" +
            "data: 1\n\n" +
            "data: [DONE]\n\n",
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 1);
        if (framedFailureScalar.CanConfirm || !framedFailureScalar.SawTerminalFailure)
        {
            throw new InvalidOperationException(
                "A failure-framed scalar event was incorrectly confirmable.");
        }

        const string ambiguousThenDoneSse =
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_done_first\"}}\n\n" +
            "data: {\"type\":\"response.in_progress\",\"response\":{\"id\":\"resp_done_second\"}}\n\n" +
            "data: [DONE]\n\n";
        var ambiguousThenDone = Observe(
            ambiguousThenDoneSse,
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 4);
        if (ambiguousThenDone.CanConfirm ||
            !ambiguousThenDone.IsAmbiguous ||
            ambiguousThenDone.SawTerminalSuccess)
        {
            throw new InvalidOperationException(
                "A [DONE] marker accepted conflicting response ids.");
        }

        var conflicting = Observe(
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_first\"}}\n\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_second\"}}\n\n",
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 11);
        if (conflicting.CanConfirm || !conflicting.IsAmbiguous)
        {
            throw new InvalidOperationException("Conflicting response ids were not rejected.");
        }

        var malformedTerminal = Observe(
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_malformed_terminal\"}}\n\n" +
            "event: response.completed\n" +
            "data: {not-json}\n\n",
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 17);
        if (malformedTerminal.CanConfirm || malformedTerminal.IsComplete)
        {
            throw new InvalidOperationException(
                "Malformed terminal SSE data was incorrectly accepted for confirmation.");
        }

        var truncatedTerminal = Observe(
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_truncated_terminal\"}}\n\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_truncated_terminal\"}}\n",
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 13);
        if (truncatedTerminal.CanConfirm || truncatedTerminal.IsComplete)
        {
            throw new InvalidOperationException(
                "An unterminated SSE terminal event was incorrectly accepted for confirmation.");
        }

        var oversizedLine = Observe(
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_oversized_line\"}}\n\n" +
            "data: " + new string('x', MaximumSseLineBytes + 1) + "\n\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_oversized_line\"}}\n\n",
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 4096);
        if (oversizedLine.CanConfirm || oversizedLine.IsComplete)
        {
            throw new InvalidOperationException(
                "An SSE line overflow was incorrectly accepted for confirmation.");
        }

        var oversizedEventDataPayload = new string('x', MaximumSseEventDataBytes / 2);
        var oversizedEventData = Observe(
            "data: {\"type\":\"response.created\",\"response\":{\"id\":\"resp_oversized_event\"}}\n\n" +
            "data: \"" + oversizedEventDataPayload + "\"\n" +
            "data: \"" + oversizedEventDataPayload + "\"\n\n" +
            "event: response.completed\n" +
            "data: {\"type\":\"response.completed\",\"response\":{\"id\":\"resp_oversized_event\"}}\n\n",
            OpenAIResponseWireFormat.ServerSentEvents,
            chunkSize: 4096);
        if (oversizedEventData.CanConfirm || oversizedEventData.IsComplete)
        {
            throw new InvalidOperationException(
                "An SSE event-data overflow was incorrectly accepted for confirmation.");
        }

        if (IsValidResponseId("msg_not_a_response") ||
            IsValidResponseId("resp_invalid space") ||
            !IsValidResponseId("resp_valid-123_ABC"))
        {
            throw new InvalidOperationException("Response-id validation accepted an unsafe shape.");
        }
    }

    private enum JsonProperty
    {
        None,
        Other,
        Id,
        Response,
        Status,
        Type
    }

    private readonly record struct JsonScope(
        bool IsObject,
        bool IsRoot,
        bool IsResponse);
}

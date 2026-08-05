namespace Clavenar.AgentSdk;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Accumulates streaming tool-call fragments and inspects an assembled batch when the caller signals
/// a close. A provider stream wrapper drives it: <see cref="Start"/> when a tool call opens,
/// <see cref="Update"/> for each fragment, then <see cref="CloseAsync(string[])"/> /
/// <see cref="CloseByPrefixAsync"/> — called BEFORE the closing event is forwarded — to inspect.
///
/// <para>Not safe for concurrent use; drive it from the single stream-reading loop.</para>
/// </summary>
public sealed class StreamGate
{
    private const int MaxToolArgumentBytes = 1024 * 1024;
    private const int MaxBatchArgumentBytes = 4 * 1024 * 1024;
    private readonly ClavenarInspector _inspector;
    private readonly Dictionary<string, ToolBuf> _bufs = new();
    private readonly List<string> _order = new();
    private readonly List<string> _shapeErrors = new();

    public StreamGate(ClavenarOptions options)
    {
        _inspector = new ClavenarInspector(options);
    }

    /// <summary>Register an opening tool call under key with its id and name.</summary>
    public void Start(string key, string id, string name)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(id) || string.IsNullOrEmpty(name))
        {
            _shapeErrors.Add(
                "clavenar stream: tool call start is missing a valid key, id, or name");
            return;
        }

        if (_bufs.ContainsKey(key))
        {
            _shapeErrors.Add($"clavenar stream: duplicate tool call start for key {key}");
            return;
        }

        if (_bufs.Count >= 128)
        {
            _shapeErrors.Add("clavenar stream: more than 128 tool-call buffers are open");
            return;
        }

        var b = Ensure(key);
        b.Id = id;
        b.Name = name;
    }

    /// <summary>Merge a fragment into key, creating it if no Start arrived first (OpenAI deltas).</summary>
    public void Update(string key, string? id, string? name, string? argsFragment)
    {
        if (string.IsNullOrEmpty(key))
        {
            _shapeErrors.Add("clavenar stream: tool-call delta is missing a valid key");
            return;
        }

        if (!_bufs.ContainsKey(key) && _bufs.Count >= 128)
        {
            _shapeErrors.Add("clavenar stream: more than 128 tool-call buffers are open");
            return;
        }

        var b = Ensure(key);
        if (!string.IsNullOrEmpty(id))
        {
            b.Id = id;
        }

        if (!string.IsNullOrEmpty(name))
        {
            b.Name = name;
        }

        if (!string.IsNullOrEmpty(argsFragment))
        {
            int fragmentBytes = Encoding.UTF8.GetByteCount(argsFragment);
            if (b.ArgumentBytes + fragmentBytes > MaxToolArgumentBytes
                || TotalArgumentBytes() + fragmentBytes > MaxBatchArgumentBytes)
            {
                _bufs.Remove(key);
                _order.Remove(key);
                _shapeErrors.Add(
                    "clavenar stream: buffered tool arguments exceeded the configured limit");
                return;
            }

            b.Args.Append(argsFragment);
            b.ArgumentBytes += fragmentBytes;
        }
    }

    /// <summary>Whether a tool-call buffer is open under key.</summary>
    public bool Has(string key) => _bufs.ContainsKey(key);

    /// <summary>Assemble and inspect the buffered calls for the given keys. Unknown keys are skipped.</summary>
    public Task CloseAsync(params string[] keys) => CloseAsync(keys, CancellationToken.None);

    /// <summary>Assemble and inspect the buffered calls for the given keys. Unknown keys are skipped.</summary>
    public async Task CloseAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var calls = new List<NormalizedToolCall>();
        foreach (var key in keys)
        {
            if (_bufs.Remove(key, out var b))
            {
                _order.Remove(key);
                try
                {
                    calls.Add(b.ToCall());
                }
                catch (ClavenarTransportException error)
                {
                    _shapeErrors.Add(error.Message);
                }
            }
            else
            {
                _shapeErrors.Add(
                    $"clavenar stream: terminal event referenced an unknown tool buffer {key}");
            }
        }

        foreach (var error in _shapeErrors)
        {
            await _inspector.ProviderShapeErrorAsync(error, cancellationToken).ConfigureAwait(false);
        }

        _shapeErrors.Clear();
        if (calls.Count > 0)
        {
            await _inspector.InspectAllAsync(calls, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Close every open key with the given prefix, in first-seen order (OpenAI per-choice drain).</summary>
    public async Task CloseByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var keys = _order.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        if (keys.Count == 0)
        {
            await _inspector.ProviderShapeErrorAsync(
                $"clavenar stream: terminal event had no tool buffers for prefix {prefix}",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        await CloseAsync(keys, cancellationToken).ConfigureAwait(false);
    }

    private ToolBuf Ensure(string key)
    {
        if (!_bufs.TryGetValue(key, out var b))
        {
            if (_bufs.Count >= 128)
            {
                throw new ClavenarTransportException(
                    "clavenar stream: more than 128 tool-call buffers are open");
            }

            b = new ToolBuf();
            _bufs[key] = b;
            _order.Add(key);
        }

        return b;
    }

    private int TotalArgumentBytes() => _bufs.Values.Sum(value => value.ArgumentBytes);

    private sealed class ToolBuf
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Args { get; } = new();

        public int ArgumentBytes { get; set; }

        public NormalizedToolCall ToCall()
        {
            if (string.IsNullOrEmpty(Id) || string.IsNullOrEmpty(Name))
            {
                throw new ClavenarTransportException(
                    "clavenar stream: tool call buffer missing id or name");
            }

            var raw = Args.ToString();
            if (raw.Length == 0)
            {
                return new NormalizedToolCall(Id, Name, new JsonObject());
            }

            try
            {
                return new NormalizedToolCall(Id, Name, JsonNode.Parse(raw));
            }
            catch (JsonException)
            {
                throw new ClavenarTransportException(
                    $"clavenar stream: tool call {Id} ({Name}) has unparseable arguments");
            }
        }
    }
}

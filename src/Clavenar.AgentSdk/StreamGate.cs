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
    private readonly ClavenarInspector _inspector;
    private readonly Dictionary<string, ToolBuf> _bufs = new();
    private readonly List<string> _order = new();

    public StreamGate(ClavenarOptions options)
    {
        _inspector = new ClavenarInspector(options);
    }

    /// <summary>Register an opening tool call under key with its id and name.</summary>
    public void Start(string key, string id, string name)
    {
        var b = Ensure(key);
        b.Id = id;
        b.Name = name;
    }

    /// <summary>Merge a fragment into key, creating it if no Start arrived first (OpenAI deltas).</summary>
    public void Update(string key, string? id, string? name, string? argsFragment)
    {
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
            b.Args.Append(argsFragment);
        }
    }

    /// <summary>Whether a tool-call buffer is open under key.</summary>
    public bool Has(string key) => _bufs.ContainsKey(key);

    /// <summary>Assemble and inspect the buffered calls for the given keys. Unknown keys are skipped.</summary>
    public Task CloseAsync(params string[] keys) => CloseAsync(keys, CancellationToken.None);

    /// <summary>Assemble and inspect the buffered calls for the given keys. Unknown keys are skipped.</summary>
    public Task CloseAsync(IEnumerable<string> keys, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keys);
        var calls = new List<NormalizedToolCall>();
        foreach (var key in keys)
        {
            if (_bufs.Remove(key, out var b))
            {
                _order.Remove(key);
                calls.Add(b.ToCall());
            }
        }

        return calls.Count == 0 ? Task.CompletedTask : _inspector.InspectAllAsync(calls, cancellationToken);
    }

    /// <summary>Close every open key with the given prefix, in first-seen order (OpenAI per-choice drain).</summary>
    public Task CloseByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var keys = _order.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        return CloseAsync(keys, cancellationToken);
    }

    private ToolBuf Ensure(string key)
    {
        if (!_bufs.TryGetValue(key, out var b))
        {
            b = new ToolBuf();
            _bufs[key] = b;
            _order.Add(key);
        }

        return b;
    }

    private sealed class ToolBuf
    {
        public string? Id { get; set; }

        public string? Name { get; set; }

        public StringBuilder Args { get; } = new();

        public NormalizedToolCall ToCall()
        {
            if (string.IsNullOrEmpty(Id) || string.IsNullOrEmpty(Name))
            {
                throw new ClavenarConfigException("clavenar stream: tool call buffer missing id or name");
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
                throw new ClavenarConfigException(
                    $"clavenar stream: tool call {Id} ({Name}) has unparseable arguments");
            }
        }
    }
}

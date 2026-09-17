using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Photone.Ipc.Internal;

namespace Photone.Ipc;

/// <summary>
/// <see cref="ITagSerializer"/> over System.Text.Json. The writing side needs no registration: a tag type is registered on first use under its full
/// type name. A reader turns a tag back into its type only when that type is registered in the reader's process under the same name
/// (<see cref="Register{TTag}(string?)"/>); anything else arrives as an <see cref="UnknownTag"/> with the JSON payload. Thread-safe.
/// </summary>
/// <example>
/// <code>
/// var tags = new JsonTagSerializer().Register&lt;SampleRate&gt;().Register&lt;BurstStart&gt;();
/// var buffer = RingBuffer&lt;float&gt;.Open("sdr", new RingBufferOptions { TagSerializer = tags });
/// </code>
/// </example>
public sealed class JsonTagSerializer : ITagSerializer
{
    [ThreadStatic]
    private static Utf8JsonWriter? t_writer;

    private readonly ConcurrentDictionary<Type, Registration> _byType = new();
    private readonly ConcurrentDictionary<string, Registration> _byName = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// Reflection-based JSON with <see cref="JsonSerializerOptions.Default"/>: any tag type with public properties works without preparation.
    /// Not compatible with trimming or native AOT; use <see cref="JsonTagSerializer(JsonSerializerOptions)"/> with a source-generated resolver there.
    /// </summary>
    [RequiresUnreferencedCode("Reflection-based JSON serialization of tag types. Use the JsonSerializerOptions overload with a source-generated TypeInfoResolver when trimming.")]
    [RequiresDynamicCode("Reflection-based JSON serialization of tag types. Use the JsonSerializerOptions overload with a source-generated TypeInfoResolver for native AOT.")]
    public JsonTagSerializer()
        : this(JsonSerializerOptions.Default)
    {
    }

    /// <summary>JSON with <paramref name="options"/>, whose <see cref="JsonSerializerOptions.TypeInfoResolver"/> must provide the metadata of every tag type (for example a source-generated context).</summary>
    public JsonTagSerializer(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
    }

    /// <summary>The options every payload is written and read with.</summary>
    public JsonSerializerOptions Options { get; }

    /// <summary>Registers <typeparamref name="TTag"/> with the metadata of <see cref="Options"/>.</summary>
    /// <param name="typeName">The name written with each tag; <see langword="null"/> = the full type name. Writer and reader must agree on it.</param>
    /// <returns>This serializer (for chaining).</returns>
    /// <exception cref="InvalidOperationException">The type or the name is already registered differently.</exception>
    /// <exception cref="NotSupportedException"><see cref="Options"/> cannot provide metadata for <typeparamref name="TTag"/>.</exception>
    public JsonTagSerializer Register<TTag>(string? typeName = null) where TTag : ITag
        => Register((JsonTypeInfo<TTag>)Options.GetTypeInfo(typeof(TTag)), typeName);

    /// <summary>Registers <typeparamref name="TTag"/> with explicit metadata (for example <c>MyJsonContext.Default.SampleRate</c>).</summary>
    /// <param name="typeInfo">The metadata to write and read the tag with.</param>
    /// <param name="typeName">The name written with each tag; <see langword="null"/> = the full type name. Writer and reader must agree on it.</param>
    /// <returns>This serializer (for chaining).</returns>
    /// <exception cref="InvalidOperationException">The type or the name is already registered differently.</exception>
    public JsonTagSerializer Register<TTag>(JsonTypeInfo<TTag> typeInfo, string? typeName = null) where TTag : ITag
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        if (typeof(TTag).IsInterface)
        {
            throw new ArgumentException($"Register the concrete tag type, not the interface {typeof(TTag)}.", nameof(typeInfo));
        }

        string name = typeName ?? DefaultName(typeof(TTag));
        if (name.Length == 0 || Encoding.UTF8.GetByteCount(name) > TagFormat.MaxNameBytes)
        {
            throw new ArgumentException($"A tag type name must be 1 to {TagFormat.MaxNameBytes} UTF-8 bytes.", nameof(typeName));
        }

        lock (_gate)
        {
            if (_byType.TryGetValue(typeof(TTag), out Registration? existing))
            {
                if (existing.Name != name)
                {
                    throw new InvalidOperationException($"{typeof(TTag)} is already registered as '{existing.Name}'.");
                }

                return this;
            }

            if (_byName.TryGetValue(name, out Registration? other))
            {
                throw new InvalidOperationException($"The tag type name '{name}' is already registered for {other.Type}.");
            }

            var registration = new Registration<TTag>(name, typeInfo);
            _byName[name] = registration;
            _byType[typeof(TTag)] = registration;
        }

        return this;
    }

    /// <inheritdoc/>
    public string GetTypeName<TTag>() where TTag : ITag => Get<TTag>().Name;

    /// <inheritdoc/>
    public void Serialize<TTag>(TTag tag, IBufferWriter<byte> destination) where TTag : ITag
    {
        ArgumentNullException.ThrowIfNull(destination);
        Registration<TTag> registration = Get<TTag>();
        Utf8JsonWriter writer = t_writer ??= new Utf8JsonWriter(Stream.Null);
        writer.Reset(destination);
        try
        {
            JsonSerializer.Serialize(writer, tag, registration.TypeInfo);
            writer.Flush();
        }
        finally
        {
            writer.Reset(Stream.Null);                              // do not keep the destination alive through the thread-static writer
        }
    }

    /// <inheritdoc/>
    public ITag? Deserialize(string typeName, ReadOnlySpan<byte> payload)
        => _byName.TryGetValue(typeName, out Registration? registration) ? registration.Deserialize(payload) : null;

    private Registration<TTag> Get<TTag>() where TTag : ITag
    {
        if (!_byType.TryGetValue(typeof(TTag), out Registration? registration))
        {
            Register<TTag>();
            registration = _byType[typeof(TTag)];
        }

        return (Registration<TTag>)registration;
    }

    private static string DefaultName(Type type) => type.FullName ?? type.Name;

    private abstract class Registration(string name, Type type)
    {
        public string Name { get; } = name;

        public Type Type { get; } = type;

        public abstract ITag Deserialize(ReadOnlySpan<byte> payload);
    }

    private sealed class Registration<TTag>(string name, JsonTypeInfo<TTag> typeInfo) : Registration(name, typeof(TTag)) where TTag : ITag
    {
        public JsonTypeInfo<TTag> TypeInfo { get; } = typeInfo;

        public override ITag Deserialize(ReadOnlySpan<byte> payload)
            => JsonSerializer.Deserialize(payload, TypeInfo) ?? throw new JsonException($"The payload of a '{Name}' tag is null.");
    }
}
